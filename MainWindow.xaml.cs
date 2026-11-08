using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MusicRPC;

public partial class MainWindow : Window
{
    readonly Settings _s;
    readonly PresenceController _c;
    readonly DispatcherTimer _timer;
    readonly UpdateService _u;
    bool _bannerDismissed;
    bool _installing;
    bool _loading = true;
    string? _imgKey;
    ImageSource? _img;
    string? _trackUrl;
    string _lastSessions = "";

    public MainWindow(Settings s, PresenceController c, UpdateService u)
    {
        _s = s; _c = c; _u = u;
        InitializeComponent();

        SourceInitialized += (_, _) => Native.TitleBar(new WindowInteropHelper(this).Handle, !_s.LightTheme);

        foreach (var cb in Descendants<CheckBox>(this).Where(x => x.Tag is string))
        {
            var p = typeof(Settings).GetProperty((string)cb.Tag!);
            if (p?.PropertyType == typeof(bool)) cb.IsChecked = (bool)p.GetValue(_s)!;
            cb.Checked += Toggle_Changed;
            cb.Unchecked += Toggle_Changed;
        }
        _loading = false;
        Loaded += (_, _) => ShowPage(0);
        VerText.Text = $"{UpdateService.Current} ver";
        _u.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshUpdateUi));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
        Render();
    }

    // ---------- навигация и настройки ----------
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Page0 == null || Page1 == null || Page2 == null || Page3 == null) return;
        ShowPage(int.Parse((string)((RadioButton)sender).Tag));
    }

    void ShowPage(int idx)
    {
        var pages = new UIElement?[] { Page0, Page1, Page2, Page3 };
        for (int i = 0; i < pages.Length; i++)
            if (pages[i] != null) pages[i]!.Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
    }

    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cb = (CheckBox)sender;
        var p = typeof(Settings).GetProperty((string)cb.Tag!);
        p?.SetValue(_s, cb.IsChecked == true);
        _s.Save();
        if ((string)cb.Tag! == "Autostart") App.SetAutostart(_s.Autostart);
        if ((string)cb.Tag! == "LightTheme")
        {
            App.ApplyTheme(_s.LightTheme);
            Native.TitleBar(new WindowInteropHelper(this).Handle, !_s.LightTheme);
        }
        _c.Invalidate();
    }

    void Reconnect_Click(object sender, RoutedEventArgs e) => _c.Reconnect();

    void Report_Click(object sender, RoutedEventArgs e)
    {
        var file = Diagnostics.Report(_s, _c, _u);
        ReportStatus.Text = "Отчёт скопирован в буфер обмена. Вставьте его (Ctrl+V) в открывшееся окно GitHub." +
                            (file != "" ? "\nФайл: " + file : "");
    }

    // ---------- обновления ----------
    void RefreshUpdateUi()
    {
        UpdStatus.Text = _u.Status;
        CheckBtn.IsEnabled = !_u.Checking && !_installing;
        var info = _u.Available;
        if (info == null || _bannerDismissed) { if (!_installing) UpdateBanner.Visibility = Visibility.Collapsed; return; }
        UpdateBanner.Visibility = Visibility.Visible;
        if (!_installing)
        {
            UpdTitle.Text = $"Доступна версия {info.Version}";
            UpdSub.Text = $"У вас {UpdateService.Current}. Установка занимает несколько секунд, программа перезапустится сама.";
        }
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = false;
        await _u.CheckAsync();
        RefreshUpdateUi();
    }

    void UpdLater_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    async void UpdInstall_Click(object sender, RoutedEventArgs e)
    {
        var info = _u.Available;
        if (info == null || _installing) return;
        _installing = true;
        UpdInstall.IsEnabled = UpdLater.IsEnabled = false;
        UpdBar.Visibility = Visibility.Visible; UpdBar.Value = 0;
        UpdTitle.Text = $"Загружаю версию {info.Version}…";
        UpdSub.Text = "Не закрывайте программу";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdBar.Value = p;
                UpdSub.Text = $"Загружено {(int)(p * 100)}%";
            });
            var path = await _u.DownloadAsync(info, progress, CancellationToken.None);
            UpdTitle.Text = "Устанавливаю…";
            UpdSub.Text = "Программа перезапустится";
            UpdateService.LaunchInstaller(path);
            await Task.Delay(400);
            ((App)Application.Current).ExitApp();
        }
        catch (Exception ex)
        {
            _installing = false;
            UpdBar.Visibility = Visibility.Collapsed;
            UpdInstall.IsEnabled = UpdLater.IsEnabled = true;
            UpdTitle.Text = "Не удалось обновиться";
            UpdSub.Text = "Проверьте интернет и попробуйте ещё раз (" + ex.GetType().Name + ": " + ex.Message + ")";
        }
    }

    void TrackLink_Click(object sender, RoutedEventArgs e) => Open(_trackUrl);

    static void Open(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.Exiting && _s.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        if (!app.Exiting) app.ExitApp();
    }

    // ---------- отрисовка ----------
    void Render()
    {
        var np = _c.Now;

        // Discord
        var d = _c.Discord;
        DiscordDot.Fill = (Brush)FindResource(d.Connected ? "Good" : "Bad");
        DiscordStatus.Text = d.Status;

        // Поиск
        var si = _c.Search;
        int enabled = (_s.SearchYandex ? 1 : 0) + (_s.SearchITunes ? 1 : 0) + (_s.SearchDeezer ? 1 : 0);
        SearchCount.Text = enabled == 1 ? "1 источник" : $"{enabled} источника";
        SetState(StYandex, si.Yandex); SetState(StITunes, si.ITunes); SetState(StDeezer, si.Deezer);

        // Сессии
        var lines = _c.SessionLines;
        var joined = string.Join("\n", lines);
        if (joined != _lastSessions)
        {
            _lastSessions = joined;
            SessionsList.ItemsSource = lines.ToList();
            NoSessions.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (np == null)
        {
            NpHeader.Text = "СЕЙЧАС В ПЛЕЕРЕ";
            NpTitle.Text = "Музыка ещё не играет";
            NpArtist.Text = "Откройте плеер и включите трек";
            NpPos.Text = "00:00"; NpDur.Text = "--:--"; NpBar.Value = 0;
            SetCover(null, null);
            NpStatus.Text = "Ожидание воспроизведения";
            NpMetaBox.Visibility = NpLinkBox.Visibility = Visibility.Collapsed;
            _trackUrl = null;

            PvHeader.Text = "Ничего не играет";
            PvTitle.Text = "—"; PvArtist.Text = "—"; PvPos.Text = "00:00"; PvDur.Text = "--:--"; PvBar.Value = 0;
            return;
        }

        var pos = np.Position;
        if (np.Playing) pos += DateTime.UtcNow - np.Taken;
        if (np.Duration > TimeSpan.Zero && pos > np.Duration) pos = np.Duration;

        string head = np.Playing ? $"Слушает {np.Artist} — {np.Title}" : "На паузе";
        NpHeader.Text = np.Playing ? "СЕЙЧАС ИГРАЕТ" : "НА ПАУЗЕ";
        NpTitle.Text = np.Title;
        NpArtist.Text = string.IsNullOrWhiteSpace(np.Album) ? np.Artist : $"{np.Artist} · {np.Album}";
        NpPos.Text = Fmt(pos);
        NpDur.Text = np.Duration > TimeSpan.Zero ? Fmt(np.Duration) : "--:--";
        NpBar.Value = np.Duration > TimeSpan.Zero ? Math.Min(1, pos.TotalSeconds / np.Duration.TotalSeconds) : 0;
        SetCover(np.Thumb, np.ThumbKey);

        if (!d.Connected) NpStatus.Text = "Статус не отправляется: " + d.Status;
        else if (_c.PresenceActive) NpStatus.Text = "Статус активен";
        else NpStatus.Text = np.Playing ? "Статус отправляется…" : "Статус скрыт (пауза)";

        NpMeta.Text = $"{np.AppId}  ·  Источник: {SourceNames.Of(np.Source)}" +
                      (np.FoundBy != null ? $"  ·  Подтверждено: {np.FoundBy}" : "");
        NpMetaBox.Visibility = Visibility.Visible;
        _trackUrl = np.TrackUrl;
        NpLinkBox.Visibility = np.TrackUrl != null ? Visibility.Visible : Visibility.Collapsed;

        // превью статуса
        PvHeader.Text = _s.NameAsTrack ? head : $"Слушает MusicRPC";
        PvTitle.Text = np.Title;
        PvArtist.Text = np.Artist;
        PvPos.Text = NpPos.Text; PvDur.Text = NpDur.Text; PvBar.Value = NpBar.Value;
        PvBar.Visibility = PvPos.Visibility = PvDur.Visibility = _s.ShowProgress ? Visibility.Visible : Visibility.Hidden;
    }

    void SetCover(byte[]? bytes, string? key)
    {
        if (key != _imgKey)
        {
            _imgKey = key;
            _img = null;
            if (bytes != null)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.EndInit();
                    bi.Freeze();
                    _img = bi;
                }
                catch { }
            }
        }
        CoverImg.Source = PvImg.Source = _img;
        CoverNote.Visibility = PvNote.Visibility = _img == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetState(System.Windows.Controls.TextBlock tb, SearchState st)
    {
        (tb.Text, tb.Foreground) = st switch
        {
            SearchState.Found => ("✓  Найдено", (Brush)FindResource("Good")),
            SearchState.NotFound => ("✕  Не найдено", (Brush)FindResource("Bad")),
            SearchState.Error => ("!  Недоступен", (Brush)FindResource("Bad")),
            SearchState.Searching => ("…  Поиск", (Brush)FindResource("Accent")),
            _ => ("—  Не использовался", (Brush)FindResource("Muted")),
        };
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is T t) yield return t;
            foreach (var x in Descendants<T>(d)) yield return x;
        }
    }
}
