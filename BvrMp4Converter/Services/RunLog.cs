namespace BvrMp4Converter.Services;

/// <summary>Szálbiztos naplófájl a kimeneti mappában.</summary>
public sealed class RunLog
{
    private readonly object _lock = new();
    private string? _path;

    public string? Path => _path;

    public void Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _path = System.IO.Path.Combine(directory, $"bvr_convert_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        }
        catch { _path = null; }
    }

    public void Write(string line)
    {
        if (_path == null) return;
        lock (_lock)
        {
            try { File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
            catch { }
        }
    }
}
