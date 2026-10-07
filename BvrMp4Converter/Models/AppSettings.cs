using System.Text.Json;
using System.Text.Json.Serialization;

namespace BvrMp4Converter.Models;

public enum ConvertMode { Remux, Reencode }

public enum ExistsPolicy { Ask, Overwrite, Skip, Rename }

public enum ConflictAnswer { Overwrite, Rename, Skip }

/// <summary>Felhasználói beállítások; a %APPDATA%\BvrMp4Converter\settings.json fájlban maradnak meg.</summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = "";
    public ConvertMode Mode { get; set; } = ConvertMode.Remux;
    public string Codec { get; set; } = "h264";   // h264 | hevc
    public int Quality { get; set; } = 23;        // CRF / CQ
    public bool HalfResolution { get; set; }
    public bool Rotate180 { get; set; }
    public bool DeleteSource { get; set; }
    public int Parallel { get; set; } = 1;
    public ExistsPolicy Policy { get; set; } = ExistsPolicy.Ask;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BvrMp4Converter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* sérült fájl: alapértékek */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* a beállítás mentése nem kritikus */ }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
