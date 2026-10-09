using System.Text;

namespace BvrMp4Converter.Services;

/// <summary>Az ffmpeg bemeneti (-i előtti) kapcsolói. A .bvr fájlok egy része az alapértelmezett felismeréssel nem nyitható meg.</summary>
public sealed record InputProfile(string Name, IReadOnlyList<string> Args, string? RawCodec = null, bool UsesPipe = false, double Fps = 0)
{
    public static readonly InputProfile Default = new("default", Array.Empty<string>());
}

public static class InputProfiles
{
    /// <summary>Nyers H.264/H.265 adatfolyamnál nincs időbélyeg a fájlban, ezért ennyi fps-t feltételezünk.</summary>
    public const int RawFps = 25;

    private const string BigProbe = "67108864";      // 64 MB
    private const string BigAnalyze = "60000000";    // 60 s (µs)

    /// <summary>Tartalék beolvasási módok, ha az alapértelmezett nem talál videót. Lustán állítja elő őket.</summary>
    public static IEnumerable<InputProfile> Fallbacks(string path)
    {
        // 1) hibatűrő beolvasás, nagyobb elemzési ablakkal
        yield return new InputProfile("tolerant", new[]
        {
            "-probesize", BigProbe, "-analyzeduration", BigAnalyze,
            "-err_detect", "ignore_err", "-fflags", "+genpts+discardcorrupt"
        });

        // 2) nyers H.265 / H.264 adatfolyam: előbb a fejlécben talált kodek (a videó kezdetétől), majd vakon mindkettő
        var tried = new HashSet<(string, long)>();
        var order = new List<(string Codec, long Offset)>();
        var found = FindRawStream(path);
        if (found != null) order.Add(found.Value);
        order.Add(("hevc", 0));
        order.Add(("h264", 0));

        foreach (var (codec, offset) in order)
        {
            if (!tried.Add((codec, offset))) continue;
            var args = new List<string> { "-probesize", BigProbe, "-analyzeduration", BigAnalyze };
            if (offset > 0) args.AddRange(new[] { "-skip_initial_bytes", offset.ToString() });
            args.AddRange(new[] { "-fflags", "+genpts", "-f", codec, "-framerate", RawFps.ToString() });
            yield return new InputProfile(offset > 0 ? $"raw-{codec}@{offset}" : $"raw-{codec}", args, codec);
        }
    }

    /// <summary>Az első 16 MB-ban megkeresi az első H.265 VPS vagy H.264 SPS NAL egységet (Annex-B kezdőkód után).</summary>
    private static (string Codec, long Offset)? FindRawStream(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[(int)Math.Min(16 * 1024 * 1024, fs.Length)];
            int n = 0, r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;

            for (int i = 0; i + 5 < n; i++)
            {
                if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1) continue;
                byte b = buf[i + 3];
                string? codec = null;
                if (b == 0x40 && buf[i + 4] == 0x01) codec = "hevc";               // VPS
                else if ((b & 0x80) == 0 && (b & 0x1F) == 7 && (b & 0x60) != 0) codec = "h264"; // SPS
                if (codec == null) continue;
                long start = i > 0 && buf[i - 1] == 0 ? i - 1 : i;
                return (codec, start);
            }
        }
        catch { }
        return null;
    }

    /// <summary>A fájl első bájtjai hexában + szövegesen – ismeretlen formátum diagnosztikájához.</summary>
    public static string HexHeader(string path, int count = 128)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(count, (int)Math.Min(fs.Length, int.MaxValue))];
            int n = fs.Read(buf, 0, buf.Length);
            var sb = new StringBuilder();
            sb.AppendLine($"Méret: {fs.Length} bájt");
            for (int i = 0; i < n; i += 16)
            {
                int len = Math.Min(16, n - i);
                sb.Append(i.ToString("X4")).Append("  ");
                for (int j = 0; j < 16; j++) sb.Append(j < len ? buf[i + j].ToString("X2") + " " : "   ");
                sb.Append(' ');
                for (int j = 0; j < len; j++) sb.Append(buf[i + j] is >= 32 and < 127 ? (char)buf[i + j] : '.');
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "A fájl eleje nem olvasható: " + ex.Message; }
    }
}
