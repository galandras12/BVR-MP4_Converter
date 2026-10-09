using System.Diagnostics;
using System.Globalization;
using BvrMp4Converter.Models;

namespace BvrMp4Converter.Services;

/// <summary>A fájlok feldolgozása: remux / újrakódolás, fallback, ellenőrzés, dátumok átvitele.</summary>
public sealed class ConversionEngine
{
    private static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "mp3", "mp2", "ac3", "eac3", "opus", "alac" };

    private readonly FfmpegTools _tools;
    private readonly AppSettings _opt;
    private readonly RunLog _log;
    private readonly Func<string, Task<ConflictAnswer>> _askConflict;

    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    public ConversionEngine(FfmpegTools tools, AppSettings options, RunLog log,
        Func<string, Task<ConflictAnswer>> askConflict)
    {
        _tools = tools;
        _opt = options;
        _log = log;
        _askConflict = askConflict;
    }

    public async Task RunAsync(IReadOnlyList<FileItem> items, CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(_opt.Parallel, 1, 4),
            CancellationToken = ct
        };
        try
        {
            await Parallel.ForEachAsync(items, options, async (item, token) => await ProcessAsync(item, token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(FileItem item, CancellationToken ct)
    {
        item.Status = ItemStatus.Running;
        item.Progress = 0;
        try
        {
            _log.Write($"=== {item.Path}");
            var outPath = await ResolveOutputAsync(item);
            if (outPath == null)
            {
                item.Status = ItemStatus.Skipped;
                item.Note = Loc.T("note_exists");
                _log.Write("Kihagyva: a kimenet már létezik.");
                return;
            }
            item.OutputPath = outPath;
            var tmp = outPath + ".part";

            bool ok = false;
            string error = "";

            // 1) saját BVR-bontás: a konténer darabjaiból tiszta H.264/H.265 folyam, a fájlból mért valós képsebességgel
            if (BvrDemuxer.LooksLikeBvr(item.Path))
            {
                // az elemzés a teljes fájlt végigolvassa: lassú lemezen percekig is tarthat, ezért látható a haladása
                item.Note = Loc.T("note_scanning");
                item.Indeterminate = false;
                var scanSw = Stopwatch.StartNew();
                var info = await BvrDemuxer.ScanAsync(item.Path, pct => item.Progress = pct, ct);
                item.Progress = 0;
                _log.Write($"BVR-elemzés ideje: {scanSw.Elapsed.TotalSeconds:0.#} s");
                if (info == null)
                    _log.Write("BVR-bontás: nem található videó a konténerben.");
                else
                {
                    _log.Write($"BVR-bontás: kodek={info.Codec}, képkockák={info.FrameCount}, hossz={info.DurationSec:0.#}s, " +
                               $"fps={info.Fps:0.###}, egyéb darabok={info.OtherChunks}, típusok=[{info.TypeSummary}]");
                    var bProfile = BvrDemuxer.CreateProfile(info);
                    var bProbe = new ProbeResult(info.DurationSec, true, Array.Empty<string>(), info.Codec);
                    var bSuffix = " – " + Loc.F("note_bvr", info.Codec.ToUpperInvariant(), info.Fps.ToString("0.##"));
                    item.Indeterminate = false;
                    (ok, error) = await ConvertAsync(item, tmp, bProbe, bProfile, bSuffix, ct);
                    if (!ok)
                    {
                        _log.Write($"A BVR-bontás sikertelen ({error}); ffmpeg közvetlen beolvasás következik.");
                        SafeDelete(tmp);
                        item.Progress = 0;
                    }
                }
            }

            // 2) ffmpeg közvetlen beolvasás: alapértelmezett, nem megnyíló fájlnál tartalék módok
            if (!ok)
            {
                var profile = InputProfile.Default;
                var probe = await _tools.ProbeAsync(item.Path, ct);
                _log.Write($"Probe: hossz={probe.DurationSec:0.##}s, videó={probe.HasVideo}, kodek={probe.VideoCodec}, hang=[{string.Join(",", probe.AudioCodecs)}]");

                string suffix = "";
                if (!probe.HasVideo)
                {
                    _log.Write("Nem található videó stream; tartalék beolvasási módok kipróbálása.");
                    foreach (var p in InputProfiles.Fallbacks(item.Path))
                    {
                        ct.ThrowIfCancellationRequested();
                        var pr = await _tools.ProbeAsync(item.Path, ct, p.Args);
                        _log.Write($"Probe [{p.Name}]: videó={pr.HasVideo}, kodek={pr.VideoCodec}, hossz={pr.DurationSec:0.##}s");
                        if (!pr.HasVideo) continue;
                        profile = p;
                        probe = pr;
                        break;
                    }

                    if (!probe.HasVideo)
                    {
                        var hex = InputProfiles.HexHeader(item.Path);
                        _log.Write("Ismeretlen formátum, a fájl eleje:\n" + hex);
                        item.Details = hex;
                        item.Indeterminate = false;
                        item.Status = ItemStatus.Error;
                        item.Note = Loc.T("err_unrecognized");
                        return;
                    }

                    if (profile.RawCodec != null)
                    {
                        // nyers adatfolyamnál a hossz nem megbízható, a sebesség pedig feltételezett
                        probe = probe with { DurationSec = 0 };
                        suffix = " – " + Loc.F("note_raw", profile.RawCodec.ToUpperInvariant(), InputProfiles.RawFps);
                    }
                    else suffix = " – " + Loc.T("note_tolerant");
                    _log.Write($"Használt beolvasási mód: {profile.Name}: {string.Join(" ", profile.Args)}");
                }
                item.Indeterminate = probe.DurationSec <= 0;
                (ok, error) = await ConvertAsync(item, tmp, probe, profile, suffix, ct);
            }

            ct.ThrowIfCancellationRequested();

            if (!ok)
            {
                SafeDelete(tmp);
                item.Status = ItemStatus.Error;
                item.Note = Loc.F("note_error", error);
                return;
            }

            File.Move(tmp, outPath, overwrite: true);
            try
            {
                var src = new FileInfo(item.Path);
                File.SetCreationTimeUtc(outPath, src.CreationTimeUtc);
                File.SetLastWriteTimeUtc(outPath, src.LastWriteTimeUtc);
            }
            catch (Exception ex) { _log.Write("Dátumok átvitele sikertelen: " + ex.Message); }

            if (_opt.DeleteSource)
            {
                try { File.Delete(item.Path); _log.Write("Eredeti .bvr törölve."); }
                catch (Exception ex) { _log.Write("Az eredeti törlése sikertelen: " + ex.Message); }
            }

            item.Progress = 100;
            item.Indeterminate = false;
            item.Status = ItemStatus.Done;
            _log.Write($"Kész: {outPath}");
        }
        catch (OperationCanceledException)
        {
            SafeDelete((item.OutputPath ?? "") + ".part");
            item.Status = ItemStatus.Cancelled;
            item.Note = Loc.T("st_cancelled");
            item.Indeterminate = false;
            _log.Write("Megszakítva.");
            throw;
        }
        catch (Exception ex)
        {
            item.Status = ItemStatus.Error;
            item.Note = Loc.F("note_error", ex.Message);
            item.Details = ex.ToString();
            item.Indeterminate = false;
            _log.Write("Kivétel: " + ex);
        }
    }

    /// <summary>Egy beolvasási móddal: előbb remux (ha kérték), hiba esetén újrakódolás. Az ideiglenes fájl a tmp.</summary>
    private async Task<(bool Ok, string Error)> ConvertAsync(
        FileItem item, string tmp, ProbeResult probe, InputProfile profile, string suffix, CancellationToken ct)
    {
        Func<Stream, CancellationToken, Task>? feeder =
            profile.UsesPipe ? (s, t) => BvrDemuxer.PumpAsync(item.Path, s, t) : null;

        bool ok = false;
        string error = "";

        if (_opt.Mode == ConvertMode.Remux && !_opt.Rotate180)
        {
            item.Note = "remux" + suffix;
            var args = BuildRemuxArgs(item.Path, tmp, probe, profile);
            var run = await RunFfmpegAsync(item, args, probe.DurationSec, ct, feeder);
            error = run.Exit == 0 ? await ValidateAsync(tmp, probe, ct) : Loc.F("err_exit", run.Exit);
            ok = error == "";
            if (!ok)
            {
                _log.Write($"Remux sikertelen: {error}\n{run.Tail}");
                item.Details = run.Tail;
                item.Note = Loc.T("note_remux_fail") + suffix;
                SafeDelete(tmp);
                item.Progress = 0;
            }
        }

        if (!ok)
        {
            string codec = _opt.Codec == "hevc" ? "hevc" : "h264";
            string encoder = await _tools.DetectEncoderAsync(codec);
            bool afterRemux = _opt.Mode == ConvertMode.Remux && !_opt.Rotate180;
            item.Note = Loc.F(afterRemux ? "note_reenc_after_remux" : "note_reenc", encoder) + suffix;
            var args = BuildEncodeArgs(item.Path, tmp, encoder, profile);
            var run = await RunFfmpegAsync(item, args, probe.DurationSec, ct, feeder);
            error = run.Exit == 0 ? await ValidateAsync(tmp, probe, ct) : Loc.F("err_exit", run.Exit);
            ok = error == "";
            if (!ok)
            {
                item.Details = (run.Tail + Environment.NewLine + error).Trim();
                _log.Write($"Újrakódolás sikertelen: {error}\n{run.Tail}");
            }
        }
        return (ok, error);
    }

    // ---------- Kimeneti név ----------

    private async Task<string?> ResolveOutputAsync(FileItem item)
    {
        var dir = string.IsNullOrWhiteSpace(_opt.OutputFolder)
            ? Path.GetDirectoryName(item.Path)!
            : _opt.OutputFolder;
        Directory.CreateDirectory(dir);
        var baseName = Path.GetFileNameWithoutExtension(item.Path);
        var path = Path.Combine(dir, baseName + ".mp4");

        await _outputGate.WaitAsync();
        try
        {
            bool inBatch = _reserved.Contains(path);
            bool onDisk = File.Exists(path);
            if (inBatch || onDisk)
            {
                // Egy futáson belüli névütközésnél soha nem írunk felül (egymás eredményét törölnénk).
                var policy = inBatch ? ExistsPolicy.Rename : _opt.Policy;
                if (policy == ExistsPolicy.Ask)
                {
                    policy = await _askConflict(path) switch
                    {
                        ConflictAnswer.Overwrite => ExistsPolicy.Overwrite,
                        ConflictAnswer.Rename => ExistsPolicy.Rename,
                        _ => ExistsPolicy.Skip
                    };
                }
                switch (policy)
                {
                    case ExistsPolicy.Skip:
                        return null;
                    case ExistsPolicy.Rename:
                        for (int i = 1; ; i++)
                        {
                            var cand = Path.Combine(dir, $"{baseName} ({i}).mp4");
                            if (!File.Exists(cand) && !_reserved.Contains(cand)) { path = cand; break; }
                        }
                        break;
                    // Overwrite: marad az eredeti útvonal
                }
            }
            _reserved.Add(path);
            return path;
        }
        finally { _outputGate.Release(); }
    }

    // ---------- ffmpeg paraméterek ----------

    private static List<string> CommonStart(string input, InputProfile profile)
    {
        var a = new List<string> { "-hide_banner", "-y", "-loglevel", "warning", "-progress", "pipe:1", "-nostats" };
        if (!profile.UsesPipe) a.Insert(1, "-nostdin"); // csőből olvasásnál az stdin a bemenet
        a.AddRange(profile.Args);
        a.AddRange(new[] { "-i", profile.UsesPipe ? "pipe:0" : input });
        return a;
    }

    private static List<string> BuildRemuxArgs(string input, string output, ProbeResult probe, InputProfile profile)
    {
        var a = CommonStart(input, profile);
        // -map 0 helyett csak videó + hang: az MP4 az adat/felirat stream-eket gyakran nem fogadja el.
        a.AddRange(new[] { "-map", "0:v", "-map", "0:a?" });
        bool audioOk = probe.AudioCodecs.All(c => Mp4Audio.Contains(c));
        if (audioOk)
            a.AddRange(new[] { "-c", "copy" });
        else
            a.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "128k" }); // csak a hangot kódolja
        // a folyamba beágyazott időzítést (VUI/SEI) felülírjuk a fájlból mért képsebességgel
        if (profile.UsesPipe) a.AddRange(new[] { "-bsf:v", $"setts=ts=N/({FpsText(profile)}*TB)" });
        if (probe.VideoCodec == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" }); // H.265 az MP4-ben lejátszókompatibilisen
        a.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", output });
        return a;
    }

    private List<string> BuildEncodeArgs(string input, string output, string encoder, InputProfile profile)
    {
        var a = CommonStart(input, profile);
        a.AddRange(new[] { "-map", "0:v", "-map", "0:a?" });

        var filters = new List<string>();
        // a folyamba beágyazott időzítést (VUI/SEI) felülírjuk a fájlból mért képsebességgel
        if (profile.UsesPipe) filters.Add($"setpts=N/({FpsText(profile)}*TB)");
        if (_opt.Rotate180) filters.Add("hflip,vflip");
        if (_opt.HalfResolution) filters.Add("scale=trunc(iw/4)*2:trunc(ih/4)*2");
        if (filters.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", filters) });

        var q = _opt.Quality.ToString(CultureInfo.InvariantCulture);
        a.AddRange(new[] { "-c:v", encoder });
        if (encoder.EndsWith("_nvenc"))
            a.AddRange(new[] { "-preset", "p5", "-rc", "vbr", "-cq", q, "-b:v", "0" });
        else if (encoder.EndsWith("_qsv"))
            a.AddRange(new[] { "-global_quality", q, "-preset", "medium" });
        else if (encoder.EndsWith("_amf"))
        {
            a.AddRange(new[] { "-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q });
            if (encoder.StartsWith("h264")) a.AddRange(new[] { "-qp_b", q });
        }
        else
            a.AddRange(new[] { "-crf", q, "-preset", "medium", "-pix_fmt", "yuv420p" });

        if (encoder.StartsWith("hevc") || encoder == "libx265") a.AddRange(new[] { "-tag:v", "hvc1" });

        if (profile.UsesPipe) a.AddRange(new[] { "-r", FpsText(profile) });

        a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", output });
        return a;
    }

    private static string FpsText(InputProfile profile) => profile.Fps.ToString("0.####", CultureInfo.InvariantCulture);

    // ---------- Futtatás ----------

    private sealed record RunResult(int Exit, string Tail);

    private async Task<RunResult> RunFfmpegAsync(FileItem item, List<string> args, double durationSec, CancellationToken ct,
        Func<Stream, CancellationToken, Task>? feeder = null)
    {
        _log.Write("ffmpeg " + string.Join(" ", args.Select(Quote)));
        var tail = new Queue<string>();
        using var p = new Process { StartInfo = ProcessUtil.CreateStartInfo(_tools.FfmpegPath!, args) };

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            // out_time_us (mikroszekundum); régebbi ffmpeg-nél out_time_ms ugyanilyen egységben
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                var val = e.Data[(e.Data.IndexOf('=') + 1)..];
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0 && durationSec > 0)
                    item.Progress = Math.Min(99.5, us / 1_000_000.0 / durationSec * 100.0);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 30) tail.Dequeue();
            }
        };

        p.Start();
        Task? feed = null;
        if (feeder != null)
        {
            var stdin = p.StandardInput.BaseStream;
            feed = Task.Run(async () =>
            {
                try { await feeder(stdin, ct); }
                catch (IOException) { /* az ffmpeg idő előtt kilépett: a hibát a kilépési kód jelzi */ }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.Write("Bemeneti adatfolyam hiba: " + ex.Message); }
                finally { try { stdin.Close(); } catch { } }
            });
        }
        else
        {
            try { p.StandardInput.Close(); } catch { }
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            ProcessUtil.Kill(p);
            throw;
        }
        finally
        {
            if (feed != null) await feed;
        }
        p.WaitForExit();

        string t;
        lock (tail) t = string.Join(Environment.NewLine, tail);
        item.Details = t;
        return new RunResult(p.ExitCode, t);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    // ---------- Ellenőrzés ----------

    /// <summary>Üres string = rendben, egyébként a hiba leírása.</summary>
    private async Task<string> ValidateAsync(string output, ProbeResult input, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(output);
            if (!fi.Exists || fi.Length == 0) return Loc.T("err_empty");

            var po = await _tools.ProbeAsync(output, ct);
            if (!po.HasVideo) return Loc.T("err_no_video");
            if (po.DurationSec <= 0) return Loc.T("err_no_duration");

            if (input.DurationSec > 0)
            {
                double diff = Math.Abs(po.DurationSec - input.DurationSec);
                double tol = Math.Max(1.5, input.DurationSec * 0.02);
                if (diff > tol)
                    return Loc.F("err_duration_diff", input.DurationSec, po.DurationSec);
            }
            return "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Loc.F("err_validate", ex.Message); }
    }

    private static void SafeDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
