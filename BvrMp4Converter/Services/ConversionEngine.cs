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
                item.Note = "a kimeneti fájl már létezik";
                _log.Write("Kihagyva: a kimenet már létezik.");
                return;
            }
            item.OutputPath = outPath;
            var tmp = outPath + ".part";

            var probe = await _tools.ProbeAsync(item.Path, ct);
            _log.Write($"Probe: hossz={probe.DurationSec:0.##}s, videó={probe.HasVideo}, hang=[{string.Join(",", probe.AudioCodecs)}]");
            item.Indeterminate = probe.DurationSec <= 0;

            bool ok = false;
            string error = "";

            if (_opt.Mode == ConvertMode.Remux && !_opt.Rotate180)
            {
                item.Note = "remux";
                var args = BuildRemuxArgs(item.Path, tmp, probe);
                var run = await RunFfmpegAsync(item, args, probe.DurationSec, ct);
                error = run.Exit == 0 ? await ValidateAsync(tmp, probe, ct) : $"ffmpeg kilépési kód: {run.Exit}";
                ok = error == "";
                if (!ok)
                {
                    _log.Write($"Remux sikertelen: {error}\n{run.Tail}");
                    item.Details = run.Tail;
                    item.Note = "remux hiba → újrakódolás";
                    SafeDelete(tmp);
                    item.Progress = 0;
                }
            }

            if (!ok)
            {
                string codec = _opt.Codec == "hevc" ? "hevc" : "h264";
                string encoder = await _tools.DetectEncoderAsync(codec);
                var prefix = _opt.Mode == ConvertMode.Remux && !_opt.Rotate180 ? "remux hiba → " : "";
                item.Note = $"{prefix}újrakódolás ({encoder})";
                var args = BuildEncodeArgs(item.Path, tmp, encoder);
                var run = await RunFfmpegAsync(item, args, probe.DurationSec, ct);
                error = run.Exit == 0 ? await ValidateAsync(tmp, probe, ct) : $"ffmpeg kilépési kód: {run.Exit}";
                ok = error == "";
                if (!ok)
                {
                    item.Details = (run.Tail + Environment.NewLine + error).Trim();
                    _log.Write($"Újrakódolás sikertelen: {error}\n{run.Tail}");
                }
            }

            ct.ThrowIfCancellationRequested();

            if (!ok)
            {
                SafeDelete(tmp);
                item.Status = ItemStatus.Error;
                item.Note = "hiba: " + error;
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
            item.Note = "megszakítva";
            item.Indeterminate = false;
            _log.Write("Megszakítva.");
            throw;
        }
        catch (Exception ex)
        {
            item.Status = ItemStatus.Error;
            item.Note = "hiba: " + ex.Message;
            item.Details = ex.ToString();
            item.Indeterminate = false;
            _log.Write("Kivétel: " + ex);
        }
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

    private static List<string> CommonStart(string input) => new()
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-progress", "pipe:1", "-nostats", "-i", input
    };

    private static List<string> BuildRemuxArgs(string input, string output, ProbeResult probe)
    {
        var a = CommonStart(input);
        // -map 0 helyett csak videó + hang: az MP4 az adat/felirat stream-eket gyakran nem fogadja el.
        a.AddRange(new[] { "-map", "0:v", "-map", "0:a?" });
        bool audioOk = probe.AudioCodecs.All(c => Mp4Audio.Contains(c));
        if (audioOk)
            a.AddRange(new[] { "-c", "copy" });
        else
            a.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "128k" }); // csak a hangot kódolja
        a.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", output });
        return a;
    }

    private List<string> BuildEncodeArgs(string input, string output, string encoder)
    {
        var a = CommonStart(input);
        a.AddRange(new[] { "-map", "0:v", "-map", "0:a?" });

        var filters = new List<string>();
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

        a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", output });
        return a;
    }

    // ---------- Futtatás ----------

    private sealed record RunResult(int Exit, string Tail);

    private async Task<RunResult> RunFfmpegAsync(FileItem item, List<string> args, double durationSec, CancellationToken ct)
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
        try { p.StandardInput.Close(); } catch { }
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
            if (!fi.Exists || fi.Length == 0) return "a kimeneti fájl üres vagy hiányzik";

            var po = await _tools.ProbeAsync(output, ct);
            if (!po.HasVideo) return "a kimeneten nincs videó stream";
            if (po.DurationSec <= 0) return "a kimenet hossza nem olvasható";

            if (input.DurationSec > 0)
            {
                double diff = Math.Abs(po.DurationSec - input.DurationSec);
                double tol = Math.Max(1.5, input.DurationSec * 0.02);
                if (diff > tol)
                    return $"a hossz eltér (be: {input.DurationSec:0.0}s, ki: {po.DurationSec:0.0}s)";
            }
            return "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "ellenőrzési hiba: " + ex.Message; }
    }

    private static void SafeDelete(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }
}
