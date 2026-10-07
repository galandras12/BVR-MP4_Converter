using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BvrMp4Converter.Models;

public enum ItemStatus { Waiting, Running, Done, Error, Skipped, Cancelled }

public sealed class FileItem : INotifyPropertyChanged
{
    public FileItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
    }

    public string Path { get; }
    public string Name { get; }
    public long SizeBytes { get; }
    public string SizeText => FormatSize(SizeBytes);

    private ItemStatus _status = ItemStatus.Waiting;
    public ItemStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { OnChanged(nameof(StatusText)); OnChanged(nameof(Fraction)); } }
    }

    public string StatusText => Status switch
    {
        ItemStatus.Waiting => Loc.T("st_waiting"),
        ItemStatus.Running => Loc.T("st_running"),
        ItemStatus.Done => Loc.T("st_done"),
        ItemStatus.Error => Loc.T("st_error"),
        ItemStatus.Skipped => Loc.T("st_skipped"),
        ItemStatus.Cancelled => Loc.T("st_cancelled"),
        _ => ""
    };

    /// <summary>Nyelvváltás után az állapotszöveg újraolvasása.</summary>
    public void RefreshLanguage() => OnChanged(nameof(StatusText));

    private double _progress;
    /// <summary>0–100</summary>
    public double Progress
    {
        get => _progress;
        set { if (Set(ref _progress, value)) OnChanged(nameof(Fraction)); }
    }

    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    /// <summary>ffmpeg kimenet utolsó sorai / hibaüzenet.</summary>
    private string _details = "";
    public string Details { get => _details; set => Set(ref _details, value); }

    public string? OutputPath { get; set; }

    /// <summary>Az összesített haladáshoz: 0..1</summary>
    public double Fraction => Status switch
    {
        ItemStatus.Done or ItemStatus.Skipped or ItemStatus.Error => 1,
        ItemStatus.Running => Math.Clamp(Progress / 100.0, 0, 1),
        _ => 0
    };

    public void Reset()
    {
        Status = ItemStatus.Waiting;
        Progress = 0;
        Indeterminate = false;
        Note = "";
        Details = "";
        OutputPath = null;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(n);
        return true;
    }
}
