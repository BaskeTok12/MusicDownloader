using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace MusicDownloaderGui;

public partial class MainWindow : Window
{
    static readonly Regex ItemRx = new(@"Downloading item (\d+) of (\d+)");
    static readonly Regex PercentRx = new(@"\[download\]\s+([\d.]+)%");

    readonly string? _ytDlp = FindYtDlp();
    readonly string? _ffmpegDir;
    Process? _process;
    string _item = "";

    public MainWindow()
    {
        InitializeComponent();
        _ffmpegDir = FindFfmpegDir(_ytDlp);
        FolderBox.Text = LoadFolder();
        if (_ytDlp == null)
        {
            DownloadButton.IsEnabled = UpdateButton.IsEnabled = false;
            StatusText.Text = "yt-dlp.exe не найден";
            Loaded += (_, _) => MessageBox.Show(this,
                "yt-dlp.exe не найден ни рядом с программой, ни в корне репозитория.\nПоложите yt-dlp.exe рядом с MusicDownloaderGui.exe.",
                "MusicDownloader", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (_ffmpegDir == null)
            Loaded += (_, _) => MessageBox.Show(this,
                "ffmpeg не найден (рядом с программой, рядом с yt-dlp или в PATH): треки не будут сконвертированы в FLAC (останутся webm).\nРаспакуйте ffmpeg.7z и добавьте папку bin в PATH.",
                "MusicDownloader", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Next to the exe, then up the tree to the repo root (gui/bin/Release/net9.0-windows -> repo).
    static string? FindYtDlp()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "yt-dlp.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // Next to the exe, next to yt-dlp, then PATH.
    static string? FindFfmpegDir(string? ytDlp) =>
        new[] { AppContext.BaseDirectory, ytDlp == null ? "" : Path.GetDirectoryName(ytDlp)! }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .FirstOrDefault(d => { try { return d.Trim().Length > 0 && File.Exists(Path.Combine(d.Trim(), "ffmpeg.exe")); } catch { return false; } })
            ?.Trim();

    static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicDownloader", "folder.txt");

    static string LoadFolder()
    {
        try { var saved = File.ReadAllText(SettingsFile).Trim(); if (saved.Length > 0) return saved; } catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MusicDownloader");
    }

    static void SaveFolder(string folder)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, folder); } catch { }
    }

    // Public for the arg-level test: exactly what the GUI passes to yt-dlp.
    public static string[] BuildArgs(string url, string folder, string? ffmpegDir)
    {
        var args = new List<string> { url, "--yes-playlist", "--ignore-errors", "--newline",
            "--download-archive", Path.Combine(folder, "archive.txt"),
            "-f", "bestaudio/best", "--extract-audio", "--audio-format", "flac",
            "-o", Path.Combine(folder, "%(playlist_title|Singles)s", "%(playlist_index&{} |)s%(title)s.%(ext)s") };
        if (ffmpegDir != null) args.AddRange(["--ffmpeg-location", ffmpegDir]);
        return args.ToArray();
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_process != null) { Cancel(); return; }

        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            StatusText.Text = "Вставьте ссылку на YouTube / YouTube Music";
            return;
        }
        var folder = FolderBox.Text.Trim();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { StatusText.Text = "Не удалось создать папку: " + ex.Message; return; }

        SaveFolder(folder);
        await Run("Скачивание…", BuildArgs(url, folder, _ffmpegDir));
    }

    async void Update_Click(object sender, RoutedEventArgs e) => await Run("Обновление yt-dlp…", "-U");

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) SaveFolder(FolderBox.Text = dlg.FolderName);
    }

    async Task Run(string status, params string[] args)
    {
        var psi = new ProcessStartInfo(_ytDlp!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; // Cyrillic titles in the log

        _item = "";
        Progress.Value = 0;
        StatusText.Text = status;
        LogBox.Clear();
        SetBusy(true);
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, ev) => OnLine(ev.Data);
            p.ErrorDataReceived += (_, ev) => OnLine(ev.Data);
            _process = p;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            if (StatusText.Text != "Отменено")
                StatusText.Text = p.ExitCode == 0 ? "Готово" : $"Завершено с ошибками (код {p.ExitCode}), см. лог";
            if (p.ExitCode == 0) Progress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            _process = null;
            SetBusy(false);
        }
    }

    void OnLine(string? line)
    {
        if (line == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
            if (ItemRx.Match(line) is { Success: true } m) _item = $"Трек {m.Groups[1].Value} из {m.Groups[2].Value}";
            if (PercentRx.Match(line) is { Success: true } pm &&
                double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                Progress.Value = pct;
                StatusText.Text = (_item.Length > 0 ? _item + " — " : "") + $"{pct:0.#}%";
            }
        });
    }

    void Cancel()
    {
        try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        StatusText.Text = "Отменено";
    }

    protected override void OnClosed(EventArgs e)
    {
        Cancel();
        base.OnClosed(e);
    }

    void SetBusy(bool busy)
    {
        DownloadButton.Content = busy ? "Отмена" : "Скачать";
        UpdateButton.IsEnabled = !busy;
        UrlBox.IsEnabled = FolderBox.IsEnabled = !busy;
    }
}
