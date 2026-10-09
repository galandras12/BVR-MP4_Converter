using System.Globalization;
using System.Text;

namespace BvrMp4Converter.Services;

/// <summary>A BVR-fájl áttekintése: kodek, képkockák száma, hossz és valós képsebesség.</summary>
public sealed record BvrInfo(string Codec, long FrameCount, double DurationSec, double Fps, long OtherChunks, string TypeSummary);

/// <summary>
/// A Blue Iris .bvr konténer bontása. A fájl "BLUE" fejlécű darabokból áll
/// (32 bájtos fejléc: magic, típus, verzió, sorszám, méret, 64 bites ms időbélyeg, utána a tartalom).
/// A videó darabok tartalma Annex-B H.264/H.265 – ezeket fejléc nélkül, egymás után az ffmpeg stdin-jére küldjük,
/// a képsebességet pedig a darabok időbélyegeiből számoljuk (az ffmpeg nyers adatfolyamnál nem tudná).
/// </summary>
public static class BvrDemuxer
{
    private const int HeaderSize = 32;

    private readonly record struct Chunk(long Pos, ushort Type, uint Size, ulong TimeMs, bool IsVideo);

    public static bool LooksLikeBvr(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && b[0] == 'B' && b[1] == 'L' && b[2] == 'U' && b[3] == 'E';
        }
        catch { return false; }
    }

    /// <summary>Végigjárja a darabok fejléceit (a tartalmat nem olvassa be). Null, ha nincs videó.</summary>
    /// <param name="progress">0–100 közötti százalék az elemzés közben (a beolvasó szálon hívódik).</param>
    public static Task<BvrInfo?> ScanAsync(string path, Action<double>? progress, CancellationToken ct) => Task.Run<BvrInfo?>(() =>
    {
        // Nagy, szekvenciális pufferrel: a darabok közti "ugrások" a pufferen belül maradnak, így lassú (HDD, hálózati)
        // lemezen sem lesz fájlonként több tízezer véletlen elérés.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4 << 20, FileOptions.SequentialScan);

        long frames = 0, other = 0, len = fs.Length;
        int lastPct = -1;
        ulong firstTs = 0, lastTs = 0;
        string? codec = null;
        var types = new SortedDictionary<ushort, long>();

        foreach (var c in Chunks(fs, ct))
        {
            int pct = len > 0 ? (int)(c.Pos * 100 / len) : 0;
            if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
            types[c.Type] = types.GetValueOrDefault(c.Type) + 1;
            if (!c.IsVideo) { other++; continue; }
            if (frames == 0) { firstTs = c.TimeMs; codec = DetectCodec(fs, c); }
            lastTs = c.TimeMs;
            frames++;
        }
        if (frames == 0 || codec == null) return null;

        double fps = 25, duration;
        double span = lastTs > firstTs ? (lastTs - firstTs) / 1000.0 : 0;
        if (frames >= 2 && span > 0)
        {
            fps = Math.Clamp((frames - 1) / span, 1, 120);
            duration = frames / fps;
        }
        else duration = frames / fps;

        var summary = string.Join(", ", types.Select(kv => $"{kv.Key:X4}x{kv.Value}"));
        return new BvrInfo(codec, frames, duration, fps, other, summary);
    }, ct);

    /// <summary>Az ffmpeg bemeneti kapcsolói: nyers folyam az stdin-ről, a mért képsebességgel.</summary>
    public static InputProfile CreateProfile(BvrInfo info) => new(
        "bvr-demux",
        new[]
        {
            "-fflags", "+genpts", "-f", info.Codec,
            "-framerate", info.Fps.ToString("0.####", CultureInfo.InvariantCulture)
        },
        info.Codec,
        UsesPipe: true,
        Fps: info.Fps);

    /// <summary>A videó darabok tartalmát (fejléc nélkül) a célfolyamba írja.</summary>
    public static async Task PumpAsync(string path, Stream dest, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 20];
        foreach (var c in Chunks(fs, ct))
        {
            if (!c.IsVideo) continue;
            fs.Position = c.Pos + HeaderSize;
            long left = c.Size;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
                if (n <= 0) return;
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                left -= n;
            }
        }
        await dest.FlushAsync(ct);
    }

    // ---------- belső ----------

    /// <summary>A fejléceken végigmenő bejáró; a fogyasztó szabadon mozgathatja a stream pozícióját.</summary>
    private static IEnumerable<Chunk> Chunks(FileStream fs, CancellationToken ct)
    {
        long len = fs.Length, next = 0;
        var h = new byte[HeaderSize + 8];

        while (next + HeaderSize <= len)
        {
            ct.ThrowIfCancellationRequested();
            fs.Position = next;
            int got = ReadFully(fs, h, (int)Math.Min(h.Length, len - next));
            if (got < HeaderSize) yield break;

            if (!(h[0] == 'B' && h[1] == 'L' && h[2] == 'U' && h[3] == 'E'))
            {
                long found = FindMagic(fs, next + 1, ct);
                if (found < 0) yield break;
                next = found;
                continue;
            }

            ushort type = BitConverter.ToUInt16(h, 4);
            uint size = BitConverter.ToUInt32(h, 12);
            ulong ts = BitConverter.ToUInt64(h, 16);

            if (next + HeaderSize + size > len)
            {
                // csonka utolsó darab: a hibás fejlécre is gyanús, ezért újraszinkronizálunk
                long found = FindMagic(fs, next + 1, ct);
                if (found < 0) yield break;
                next = found;
                continue;
            }

            int avail = got - HeaderSize;
            bool video = size >= 16 && avail >= 4 && IsStartCode(h, HeaderSize);
            yield return new Chunk(next, type, size, ts, video);
            next += HeaderSize + size;
        }
    }

    private static bool IsStartCode(byte[] b, int o) =>
        b[o] == 0 && b[o + 1] == 0 && (b[o + 2] == 1 || (b[o + 2] == 0 && b[o + 3] == 1));

    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int n = 0, r;
        while (n < count && (r = s.Read(buf, n, count - n)) > 0) n += r;
        return n;
    }

    private static long FindMagic(FileStream fs, long from, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        long p = from;
        while (p < fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            fs.Position = p;
            int n = ReadFully(fs, buf, (int)Math.Min(buf.Length, fs.Length - p));
            if (n < 4) return -1;
            for (int i = 0; i <= n - 4; i++)
                if (buf[i] == 'B' && buf[i + 1] == 'L' && buf[i + 2] == 'U' && buf[i + 3] == 'E') return p + i;
            p += n - 3;
        }
        return -1;
    }

    /// <summary>Kodek: a fájlfejléc "H265"/"H264" azonosítója, ennek hiányában az első NAL egység.</summary>
    private static string DetectCodec(FileStream fs, Chunk firstVideo)
    {
        try
        {
            fs.Position = 0;
            var head = new byte[256];
            int n = ReadFully(fs, head, (int)Math.Min(256, fs.Length));
            var text = Encoding.ASCII.GetString(head, 0, n);
            if (text.Contains("H265") || text.Contains("HEVC")) return "hevc";
            if (text.Contains("H264") || text.Contains("AVC1")) return "h264";

            fs.Position = firstVideo.Pos + HeaderSize;
            var p = new byte[8];
            ReadFully(fs, p, 8);
            int o = p[2] == 1 ? 3 : 4;
            byte nal = p[o];
            return nal is 0x40 or 0x42 or 0x44 ? "hevc" : "h264";
        }
        catch { return "h264"; }
    }
}
