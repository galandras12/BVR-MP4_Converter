using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BvrMp4Converter.Services;

public sealed record ProbeResult(double DurationSec, bool HasVideo, IReadOnlyList<string> AudioCodecs, string VideoCodec = "");

/// <summary>Az exe mellé tett ffmpeg.exe / ffprobe.exe megkeresése, ffprobe és hardveres kódoló érzékelés.</summary>
public sealed class FfmpegTools
{
    public string? FfmpegPath { get; private init; }
    public string? FfprobePath { get; private init; }

    public bool Available => FfmpegPath != null;

    public static FfmpegTools Locate()
    {
        var dirs = new List<string>();
        var procDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(procDir)) dirs.Add(procDir);
        dirs.Add(AppContext.BaseDirectory);

        foreach (var dir in dirs.Distinct())
        {
            foreach (var sub in new[] { "", "ffmpeg", "bin" })
            {
                var d = Path.Combine(dir, sub);
                var ff = Path.Combine(d, "ffmpeg.exe");
                if (File.Exists(ff))
                {
                    var fp = Path.Combine(d, "ffprobe.exe");
                    return new FfmpegTools { FfmpegPath = ff, FfprobePath = File.Exists(fp) ? fp : null };
                }
            }
        }
        return new FfmpegTools();
    }

    public static string ExpectedLocation =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    // ---------- Probe ----------

    /// <param name="inputArgs">Extra bemeneti kapcsolók (a fájlnév elé), pl. -f hevc.</param>
    public async Task<ProbeResult> ProbeAsync(string file, CancellationToken ct, IReadOnlyList<string>? inputArgs = null)
    {
        inputArgs ??= Array.Empty<string>();
        if (FfprobePath != null)
        {
            try
            {
                var args = new List<string> { "-v", "error", "-print_format", "json", "-show_format", "-show_streams" };
                args.AddRange(inputArgs);
                args.Add(file);
                var r = await ProcessUtil.RunCaptureAsync(FfprobePath, args, ct);
                if (r.ExitCode == 0) return ParseProbeJson(r.Stdout);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* tartalék: ffmpeg -i */ }
        }
        return await ProbeViaFfmpegAsync(file, ct, inputArgs);
    }

    private static ProbeResult ParseProbeJson(string json)
    {
        double dur = 0;
        bool video = false;
        string vcodec = "";
        var audio = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            dur = ParseDouble(d.GetString());

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                if (type == "video") { video = true; if (vcodec == "") vcodec = codec; }
                else if (type == "audio") audio.Add(codec);
                if (dur <= 0 && s.TryGetProperty("duration", out var sd)) dur = Math.Max(dur, ParseDouble(sd.GetString()));
            }
        }
        return new ProbeResult(dur, video, audio, vcodec);
    }

    private async Task<ProbeResult> ProbeViaFfmpegAsync(string file, CancellationToken ct, IReadOnlyList<string> inputArgs)
    {
        var a = new List<string> { "-hide_banner" };
        a.AddRange(inputArgs);
        a.AddRange(new[] { "-i", file });
        var r = await ProcessUtil.RunCaptureAsync(FfmpegPath!, a, ct);
        var text = r.Stderr;
        double dur = 0;
        var m = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (m.Success)
            dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + ParseDouble(m.Groups[3].Value);
        bool video = Regex.IsMatch(text, @"Stream #\S+.*: Video:");
        var audio = Regex.Matches(text, @"Stream #\S+.*: Audio:\s*([A-Za-z0-9_]+)")
            .Select(x => x.Groups[1].Value).ToList();
        var vm = Regex.Match(text, @"Stream #\S+.*: Video:\s*([A-Za-z0-9_]+)");
        return new ProbeResult(dur, video, audio, vm.Success ? vm.Groups[1].Value : "");
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ---------- Kódoló érzékelés ----------

    private static readonly string[] H264 = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
    private static readonly string[] Hevc = { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" };

    private readonly ConcurrentDictionary<string, Task<string>> _encoderCache = new();
    private Task<string>? _encoderList;

    /// <summary>A "h264" / "hevc" kodekhez a legjobb ténylegesen működő kódoló neve (hardveres, ennek hiányában szoftveres).</summary>
    public Task<string> DetectEncoderAsync(string codec) =>
        _encoderCache.GetOrAdd(codec, c => DetectCoreAsync(c));

    private async Task<string> DetectCoreAsync(string codec)
    {
        var candidates = codec == "hevc" ? Hevc : H264;
        var software = candidates[^1];
        if (FfmpegPath == null) return software;

        _encoderList ??= Task.Run(async () =>
        {
            try { return (await ProcessUtil.RunCaptureAsync(FfmpegPath, new[] { "-hide_banner", "-encoders" })).Stdout; }
            catch { return ""; }
        });
        var list = await _encoderList;

        foreach (var enc in candidates.Take(candidates.Length - 1))
        {
            if (!Regex.IsMatch(list, @"\s" + Regex.Escape(enc) + @"\s")) continue;
            try
            {
                // A listában szereplő kódoló még nem jelenti, hogy a hardver is megvan: rövid próbakódolás.
                var r = await ProcessUtil.RunCaptureAsync(FfmpegPath, new[]
                {
                    "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=25:d=0.5",
                    "-frames:v", "5", "-pix_fmt", "yuv420p", "-c:v", enc, "-f", "null", "-"
                });
                if (r.ExitCode == 0) return enc;
            }
            catch { }
        }
        return software;
    }
}
