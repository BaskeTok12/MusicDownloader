using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    string? _denoDir;
    Process? _process;
    string _item = "";
    volatile string? _hint; // set from yt-dlp output, replaces the generic error status

    public MainWindow()
    {
        InitializeComponent();
        _ffmpegDir = FindToolDir(_ytDlp, "ffmpeg.exe");
        _denoDir = FindToolDir(_ytDlp, "deno.exe");
        FolderBox.Text = LoadSetting("folder.txt") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MusicDownloader");
        FormatBox.SelectedIndex = LoadSetting("format.txt") == "flac" ? 1 : 0;
        FormatBox.SelectionChanged += (_, _) => SaveSetting("format.txt", Format);
        var login = LoadSetting("login.txt");
        if (LoginBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == login) is { } saved)
            LoginBox.SelectedItem = saved;
        LoginBox.SelectionChanged += (_, _) => SaveSetting("login.txt", Login);
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
                "ffmpeg не найден (рядом с программой, рядом с yt-dlp или в PATH): треки не будут сконвертированы в MP3/FLAC (останутся webm).\nРаспакуйте ffmpeg.7z и добавьте папку bin в PATH.",
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
    static string? FindToolDir(string? ytDlp, string exe) =>
        new[] { AppContext.BaseDirectory, ytDlp == null ? "" : Path.GetDirectoryName(ytDlp)! }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .FirstOrDefault(d => { try { return d.Trim().Length > 0 && File.Exists(Path.Combine(d.Trim(), exe)); } catch { return false; } })
            ?.Trim();

    static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicDownloader");

    static string? LoadSetting(string file)
    {
        try { var saved = File.ReadAllText(Path.Combine(SettingsDir, file)).Trim(); if (saved.Length > 0) return saved; } catch { }
        return null;
    }

    static void SaveSetting(string file, string value)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(Path.Combine(SettingsDir, file), value); } catch { }
    }

    string Format => (string)((System.Windows.Controls.ComboBoxItem)FormatBox.SelectedItem).Tag;
    string Login => (string)((System.Windows.Controls.ComboBoxItem)LoginBox.SelectedItem).Tag;
    static string CookiesFile => Path.Combine(AppContext.BaseDirectory, "cookies.txt");

    // Public for the arg-level test: exactly what the GUI passes to yt-dlp. format: "mp3" or "flac".
    // login: "" = anonymous, "cookies.txt" = file next to the exe, otherwise a yt-dlp browser name.
    // Private playlists (Liked Music, list=LM) only exist for a logged-in account.
    // denoDir: logged-in YouTube clients need a JS runtime to solve signature challenges; passed explicitly
    // because deno may sit next to the GUI rather than next to yt-dlp or in PATH.
    public static string[] BuildArgs(string url, string folder, string format, string? ffmpegDir, string login = "", string? denoDir = null)
    {
        var args = new List<string> { url, "--yes-playlist", "--ignore-errors", "--newline",
            // Per-format archive: switching format re-downloads instead of skipping.
            "--download-archive", Path.Combine(folder, $"archive-{format}.txt"),
            "-f", "bestaudio/best", "--extract-audio", "--audio-format", format,
            // No --embed-thumbnail: large covers break some car head units.
            "--embed-metadata",
            "-o", Path.Combine(folder, "%(playlist_title|Singles)s", "%(playlist_index&{} |)s%(title)s.%(ext)s") };
        if (format == "mp3") args.AddRange(["--audio-quality", "320K"]);
        if (ffmpegDir != null) args.AddRange(["--ffmpeg-location", ffmpegDir]);
        if (login == "cookies.txt") args.AddRange(["--cookies", CookiesFile]);
        else if (login.Length > 0) args.AddRange(["--cookies-from-browser", login]);
        if (denoDir != null) args.AddRange(["--js-runtimes", "deno:" + denoDir]);
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

        if (Login == "cookies.txt" && !File.Exists(CookiesFile))
        {
            StatusText.Text = "Положите cookies.txt рядом с MusicDownloaderGui.exe";
            return;
        }

        // Logged-in downloads fail without a JS runtime, so fetch deno once instead of failing every track.
        if (Login.Length > 0 && _denoDir == null && !await InstallDeno()) return;

        SaveSetting("folder.txt", folder);
        await Run("Скачивание…", BuildArgs(url, folder, Format, _ffmpegDir, Login, _denoDir));
    }

    const string DenoZipUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    // shortcut: no progress bar or cancel for the one-time ~41 MB download; add them if users complain.
    async Task<bool> InstallDeno()
    {
        var dir = Path.GetDirectoryName(_ytDlp)!; // yt-dlp also finds deno.exe in its own folder
        var zip = Path.Combine(Path.GetTempPath(), $"deno-{Environment.ProcessId}.zip");
        StatusText.Text = "Скачиваю deno.exe (один раз, ~41 МБ)…";
        DownloadButton.IsEnabled = UpdateButton.IsEnabled = false;
        try
        {
            using (var http = new HttpClient())
            await using (var src = await http.GetStreamAsync(DenoZipUrl))
            await using (var dst = File.Create(zip))
                await src.CopyToAsync(dst);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true));
            _denoDir = dir;
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не удалось скачать deno.exe ({ex.Message}). Скачайте deno-x86_64-pc-windows-msvc.zip с github.com/denoland/deno и положите deno.exe рядом с программой";
            return false;
        }
        finally
        {
            try { File.Delete(zip); } catch { }
            DownloadButton.IsEnabled = UpdateButton.IsEnabled = true;
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e) => await Run("Обновление yt-dlp…", "-U");

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) SaveSetting("folder.txt", FolderBox.Text = dlg.FolderName);
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
        _hint = null;
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
                StatusText.Text = p.ExitCode == 0 ? "Готово"
                    : _hint ?? $"Завершено с ошибками (код {p.ExitCode}), см. лог";
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
        if (line.Contains("The playlist does not exist"))
            _hint = "Плейлист не найден. Для «Понравившихся» выберите вход: браузер или свежий cookies.txt";
        else if (line.Contains("Could not copy Chrome cookie database"))
            _hint = "Chrome открыт и держит куки: закройте его полностью (и значок в трее) и нажмите «Скачать» снова";
        else if (line.Contains("challenge solving failed"))
            _hint = "YouTube не отдал треки: со входом нужен deno.exe рядом с программой";
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
        UrlBox.IsEnabled = FolderBox.IsEnabled = FormatBox.IsEnabled = LoginBox.IsEnabled = !busy;
    }
}
