using System.Globalization;
using System.Text;
using BvrMp4Converter.Services;

namespace BvrMp4Converter.Models;

public enum ConvertMode { Remux, Reencode }

public enum ExistsPolicy { Ask, Overwrite, Skip, Rename }

public enum ConflictAnswer { Overwrite, Rename, Skip }

/// <summary>
/// Felhasználói beállítások. A config.ini az exe mellett jön létre; ha ott nem írható
/// (pl. Program Files), akkor a %APPDATA%\BvrMp4Converter mappába kerül.
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string LastInputFolder { get; set; } = "";
    public ConvertMode Mode { get; set; } = ConvertMode.Remux;
    public string Codec { get; set; } = "h264";   // h264 | hevc
    public int Quality { get; set; } = 23;        // CRF / CQ
    public bool HalfResolution { get; set; }
    public bool Rotate180 { get; set; }
    public bool DeleteSource { get; set; }
    public int Parallel { get; set; } = 1;
    public ExistsPolicy Policy { get; set; } = ExistsPolicy.Ask;

    private const string FileName = "config.ini";

    private static string ExeDirPath => Path.Combine(FfmpegTools.ExpectedLocation, FileName);

    private static string AppDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BvrMp4Converter", FileName);

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            var path = File.Exists(ExeDirPath) ? ExeDirPath : File.Exists(AppDataPath) ? AppDataPath : null;
            if (path != null) s.ReadIni(File.ReadAllLines(path, Encoding.UTF8));
        }
        catch { /* sérült fájl: alapértékek */ }

        if (s.Language != "hu" && s.Language != "en") s.Language = Loc.DefaultLanguage();
        return s;
    }

    public void Save()
    {
        var text = ToIni();
        try { File.WriteAllText(ExeDirPath, text, new UTF8Encoding(false)); return; }
        catch { /* nem írható: tartalék hely */ }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppDataPath)!);
            File.WriteAllText(AppDataPath, text, new UTF8Encoding(false));
        }
        catch { /* a beállítás mentése nem kritikus */ }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    // ---------- INI ----------

    private string ToIni()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("; BVR -> MP4 Converter - beállítások / settings");
        sb.AppendLine("; Language: hu | en      Mode: Remux | Reencode      Codec: h264 | hevc");
        sb.AppendLine("; Policy: Ask | Overwrite | Skip | Rename      Quality: 15-35      Parallel: 1-4");
        sb.AppendLine("[General]");
        sb.AppendLine($"Language={Language}");
        sb.AppendLine($"OutputFolder={OutputFolder}");
        sb.AppendLine($"LastInputFolder={LastInputFolder}");
        sb.AppendLine($"Mode={Mode}");
        sb.AppendLine($"Codec={Codec}");
        sb.AppendLine($"Quality={Quality.ToString(inv)}");
        sb.AppendLine($"HalfResolution={HalfResolution.ToString().ToLowerInvariant()}");
        sb.AppendLine($"Rotate180={Rotate180.ToString().ToLowerInvariant()}");
        sb.AppendLine($"DeleteSource={DeleteSource.ToString().ToLowerInvariant()}");
        sb.AppendLine($"Parallel={Parallel.ToString(inv)}");
        sb.AppendLine($"Policy={Policy}");
        return sb.ToString();
    }

    private void ReadIni(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0 || t[0] is ';' or '#' or '[') continue;
            int i = t.IndexOf('=');
            if (i > 0) d[t[..i].Trim()] = t[(i + 1)..].Trim();
        }

        string Str(string key, string def) => d.TryGetValue(key, out var v) ? v : def;
        bool Bool(string key, bool def) =>
            d.TryGetValue(key, out var v)
                ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                : def;
        int Int(string key, int def, int min, int max) =>
            d.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Clamp(n, min, max) : def;
        T EnumVal<T>(string key, T def) where T : struct, System.Enum =>
            d.TryGetValue(key, out var v) && System.Enum.TryParse<T>(v, true, out var e) && System.Enum.IsDefined(e) ? e : def;

        Language = Str("Language", Language).ToLowerInvariant();
        OutputFolder = Str("OutputFolder", OutputFolder);
        LastInputFolder = Str("LastInputFolder", LastInputFolder);
        Mode = EnumVal("Mode", Mode);
        Codec = Str("Codec", Codec).Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264";
        Quality = Int("Quality", Quality, 15, 35);
        HalfResolution = Bool("HalfResolution", HalfResolution);
        Rotate180 = Bool("Rotate180", Rotate180);
        DeleteSource = Bool("DeleteSource", DeleteSource);
        Parallel = Int("Parallel", Parallel, 1, 4);
        Policy = EnumVal("Policy", Policy);
    }
}
