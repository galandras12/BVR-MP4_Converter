using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BvrMp4Converter.Models;
using BvrMp4Converter.Services;
using Microsoft.Win32;

namespace BvrMp4Converter;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileItem> _items = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FfmpegTools _tools = FfmpegTools.Locate();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppSettings _settings = AppSettings.Load();
    private CancellationTokenSource? _cts;
    private Stopwatch _clock = new();
    private bool _loading = true;
    private bool _running;
    private List<FileItem> _runItems = new();
    private string? _encoderName;

    public MainWindow()
    {
        Loc.SetLanguage(_settings.Language); // a XAML-kötések előtt
        InitializeComponent();
        Title = $"BVR → MP4 Converter v{AboutWindow.VersionString}";
        FileList.ItemsSource = _items;
        ApplySettingsToUi();
        UpdateLanguageMenu();
        UpdateEncoderText();
        _loading = false;
        UpdateEnabledState();
        UpdateCount();

        _timer.Tick += (_, _) => RefreshProgress();
        _timer.Start();
        Closing += MainWindow_Closing;
        Loc.LanguageChanged += OnLanguageChanged;
        Loaded += async (_, _) =>
        {
            _settings.Save(); // a config.ini az első induláskor is létrejön
            await InitFfmpegAsync();
        };
    }

    // ---------- Nyelv ----------

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        var lang = (sender as MenuItem)?.Tag as string ?? "hu";
        Loc.SetLanguage(lang);
        _settings = ReadSettingsFromUi();
        _settings.Save();
    }

    private void OnLanguageChanged()
    {
        UpdateLanguageMenu();
        foreach (var it in _items) it.RefreshLanguage();
        UpdateEncoderText();
        UpdateCount();
        if (!_running && _runItems.Count == 0) TotalText.Text = Loc.T("total_idle");
    }

    private void UpdateLanguageMenu()
    {
        LangEn.IsChecked = Loc.Lang == "en";
        LangHu.IsChecked = Loc.Lang == "hu";
    }

    private void UpdateEncoderText() =>
        EncoderText.Text = !_tools.Available ? Loc.T("enc_missing")
            : _encoderName == null ? Loc.T("enc_detecting")
            : Loc.F("enc_label", _encoderName);

    // ---------- ffmpeg ellenőrzés ----------

    private async Task InitFfmpegAsync()
    {
        if (!_tools.Available)
        {
            ShowMissingFfmpeg();
            return;
        }
        await RefreshEncoderLabelAsync();
    }

    private void ShowMissingFfmpeg() =>
        MessageBox.Show(this, Loc.F("msg_ffmpeg_missing", FfmpegTools.ExpectedLocation),
            Loc.T("title_ffmpeg_missing"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task RefreshEncoderLabelAsync()
    {
        if (!_tools.Available) return;
        _encoderName = null;
        UpdateEncoderText();
        _encoderName = await _tools.DetectEncoderAsync(SelectedCodec());
        UpdateEncoderText();
    }

    // ---------- Beállítások ----------

    private void ApplySettingsToUi()
    {
        RemuxRadio.IsChecked = _settings.Mode == ConvertMode.Remux;
        ReencodeRadio.IsChecked = _settings.Mode == ConvertMode.Reencode;
        CodecCombo.SelectedIndex = _settings.Codec == "hevc" ? 1 : 0;
        QualitySlider.Value = Math.Clamp(_settings.Quality, 15, 35);
        QualityText.Text = ((int)QualitySlider.Value).ToString();
        HalfResCheck.IsChecked = _settings.HalfResolution;
        RotateCheck.IsChecked = _settings.Rotate180;
        DeleteCheck.IsChecked = _settings.DeleteSource;
        ParallelCombo.SelectedIndex = Math.Clamp(_settings.Parallel, 1, 4) - 1;
        PolicyCombo.SelectedIndex = (int)_settings.Policy;
        OutputBox.Text = _settings.OutputFolder;
    }

    private string SelectedCodec() => (CodecCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "h264";

    private AppSettings ReadSettingsFromUi() => new()
    {
        Language = Loc.Lang,
        LastInputFolder = _settings.LastInputFolder,
        OutputFolder = OutputBox.Text.Trim(),
        Mode = ReencodeRadio.IsChecked == true ? ConvertMode.Reencode : ConvertMode.Remux,
        Codec = SelectedCodec(),
        Quality = (int)QualitySlider.Value,
        HalfResolution = HalfResCheck.IsChecked == true,
        Rotate180 = RotateCheck.IsChecked == true,
        DeleteSource = DeleteCheck.IsChecked == true,
        Parallel = Math.Max(1, ParallelCombo.SelectedIndex + 1),
        Policy = Enum.TryParse<ExistsPolicy>((PolicyCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var p)
            ? p : ExistsPolicy.Ask
    };

    private async void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var prevCodec = _settings.Codec;
        _settings = ReadSettingsFromUi();
        _settings.Save();
        UpdateEnabledState();
        if (_settings.Codec != prevCodec) await RefreshEncoderLabelAsync();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText == null) return;
        QualityText.Text = ((int)QualitySlider.Value).ToString();
        Option_Changed(sender, e);
    }

    private void UpdateEnabledState()
    {
        bool reencode = ReencodeRadio.IsChecked == true;
        bool rotate = RotateCheck.IsChecked == true;
        bool encodeSettings = reencode || rotate; // a forgatás is újrakódolást jelent
        CodecCombo.IsEnabled = encodeSettings && !_running;
        QualitySlider.IsEnabled = encodeSettings && !_running;
        HalfResCheck.IsEnabled = encodeSettings && !_running;

        RemuxRadio.IsEnabled = ReencodeRadio.IsEnabled = !_running;
        RotateCheck.IsEnabled = DeleteCheck.IsEnabled = ParallelCombo.IsEnabled = PolicyCombo.IsEnabled = !_running;
        OutputBox.IsEnabled = !_running;

        StartBtn.IsEnabled = !_running;
        StopBtn.IsEnabled = _running;
        BrowseFilesBtn.IsEnabled = RemoveSelBtn.IsEnabled = ClearBtn.IsEnabled = !_running;
    }

    // ---------- Fájlok hozzáadása ----------

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && !_running)
        {
            DropZone.Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xEA, 0xF7));
            e.Effects = DragDropEffects.Copy;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropZone.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFA));

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (_running || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AddPaths(paths);
    }

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("dlg_files_title"),
            Filter = Loc.T("dlg_files_filter"),
            Multiselect = true
        };
        if (Directory.Exists(_settings.LastInputFolder)) dlg.InitialDirectory = _settings.LastInputFolder;
        if (dlg.ShowDialog(this) != true) return;

        AddPaths(dlg.FileNames);
        var folder = Path.GetDirectoryName(dlg.FileNames[0]);
        if (!string.IsNullOrEmpty(folder))
        {
            _settings.LastInputFolder = folder;
            _settings.Save();
        }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive
                    };
                    foreach (var f in Directory.EnumerateFiles(p, "*.bvr", opts)) AddFile(f);
                }
                else if (File.Exists(p) && p.EndsWith(".bvr", StringComparison.OrdinalIgnoreCase))
                    AddFile(p);
                else if (File.Exists(p))
                    AddFile(p); // tallózással kifejezetten kiválasztott, más kiterjesztésű fájl
            }
            catch { /* elérhetetlen elem kihagyása */ }
        }
        UpdateCount();
    }

    private void AddFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (_paths.Add(full)) _items.Add(new FileItem(full));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in FileList.SelectedItems.Cast<FileItem>().ToList())
        {
            _items.Remove(it);
            _paths.Remove(it.Path);
        }
        UpdateCount();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear();
        _paths.Clear();
        UpdateCount();
        TotalBar.Value = 0;
        TotalText.Text = Loc.T("total_idle");
        _runItems = new();
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && !_running) RemoveSelected_Click(sender, e);
    }

    private void UpdateCount()
    {
        long total = _items.Sum(i => i.SizeBytes);
        CountText.Text = _items.Count == 0 ? "" : Loc.F("count_text", _items.Count, FileItem.FormatSize(total));
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetails();

    private void RefreshDetails()
    {
        var text = (FileList.SelectedItem as FileItem)?.Details ?? "";
        if (DetailsBox.Text != text) DetailsBox.Text = text;
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // ---------- Kimeneti mappa ----------

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("dlg_folder_title") };
        if (Directory.Exists(OutputBox.Text)) dlg.InitialDirectory = OutputBox.Text;
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = OutputBox.Text.Trim();
        if (string.IsNullOrEmpty(dir) && FileList.SelectedItem is FileItem sel)
            dir = Path.GetDirectoryName(sel.OutputPath ?? sel.Path) ?? "";
        if (string.IsNullOrEmpty(dir) && _items.Count > 0)
            dir = Path.GetDirectoryName(_items[0].Path) ?? "";

        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, Loc.T("msg_folder_missing"), Loc.T("title_open_folder"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---------- Start / Stop ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_tools.Available) { ShowMissingFfmpeg(); return; }

        var todo = _items.Where(i => i.Status != ItemStatus.Done).ToList();
        if (todo.Count == 0)
        {
            MessageBox.Show(this, Loc.T("msg_no_files"), "BVR → MP4", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings = ReadSettingsFromUi();
        _settings.Save();

        if (!string.IsNullOrEmpty(_settings.OutputFolder))
        {
            try { Directory.CreateDirectory(_settings.OutputFolder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, Loc.F("msg_outdir_fail", ex.Message), Loc.T("title_error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        if (_settings.DeleteSource)
        {
            var r = MessageBox.Show(this, Loc.T("msg_confirm_delete"),
                Loc.T("title_confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        foreach (var i in todo) i.Reset();
        _runItems = todo;

        var log = new RunLog();
        log.Open(string.IsNullOrEmpty(_settings.OutputFolder)
            ? Path.GetDirectoryName(todo[0].Path)!
            : _settings.OutputFolder);
        log.Write($"Indítás: {todo.Count} fájl, mód={_settings.Mode}, kodek={_settings.Codec}, minőség={_settings.Quality}, " +
                  $"felezés={_settings.HalfResolution}, 180°={_settings.Rotate180}, párhuzamos={_settings.Parallel}, " +
                  $"törlés={_settings.DeleteSource}, ffmpeg={_tools.FfmpegPath}");

        _cts = new CancellationTokenSource();
        _running = true;
        _clock = Stopwatch.StartNew();
        UpdateEnabledState();

        var engine = new ConversionEngine(_tools, _settings.Clone(), log, AskConflictAsync);
        try
        {
            await engine.RunAsync(todo, _cts.Token);
        }
        finally
        {
            _running = false;
            _clock.Stop();
            var cancelled = _cts.IsCancellationRequested;
            _cts.Dispose();
            _cts = null;
            UpdateEnabledState();
            RefreshDetails();

            int ok = todo.Count(i => i.Status == ItemStatus.Done);
            int err = todo.Count(i => i.Status == ItemStatus.Error);
            int skip = todo.Count(i => i.Status == ItemStatus.Skipped);
            log.Write($"Vége: kész={ok}, hiba={err}, kihagyva={skip}, megszakítva={cancelled}, idő={_clock.Elapsed:hh\\:mm\\:ss}");
            TotalBar.Value = todo.Sum(i => Math.Max(1, i.SizeBytes) * i.Fraction) / todo.Sum(i => (double)Math.Max(1, i.SizeBytes)) * 100;
            TotalText.Text = Loc.F("total_done", Loc.T(cancelled ? "word_cancelled" : "word_done"), ok, err, skip);
            if (log.Path != null) TotalText.ToolTip = Loc.F("tip_log", log.Path);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;
        _cts?.Cancel();
    }

    private Task<ConflictAnswer> AskConflictAsync(string path) =>
        Dispatcher.InvokeAsync(() =>
        {
            var r = MessageBox.Show(this, Loc.F("msg_conflict", path),
                Loc.T("title_conflict"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return r switch
            {
                MessageBoxResult.Yes => ConflictAnswer.Overwrite,
                MessageBoxResult.No => ConflictAnswer.Rename,
                _ => ConflictAnswer.Skip
            };
        }).Task;

    // ---------- Összesített haladás ----------

    private void RefreshProgress()
    {
        RefreshDetails();
        if (_items.Count == 0 || !_running) return;

        var active = _runItems;
        double totalSize = active.Sum(i => (double)Math.Max(1, i.SizeBytes));
        double done = active.Sum(i => Math.Max(1, i.SizeBytes) * i.Fraction);
        double p = totalSize > 0 ? done / totalSize : 0;
        TotalBar.Value = p * 100;

        string eta = Loc.T("eta_estimating");
        if (p > 0.02 && _clock.IsRunning)
        {
            var remaining = TimeSpan.FromSeconds(_clock.Elapsed.TotalSeconds * (1 - p) / p);
            eta = remaining.ToString(remaining.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        }
        int finished = active.Count(i => i.Status is ItemStatus.Done or ItemStatus.Error or ItemStatus.Skipped);
        TotalText.Text = Loc.F("total_progress", p * 100, finished, active.Count, eta);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_running)
        {
            var r = MessageBox.Show(this, Loc.T("msg_exit_running"), Loc.T("title_exit"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _cts?.Cancel();
        }
        _settings = ReadSettingsFromUi();
        _settings.Save();
    }
}
