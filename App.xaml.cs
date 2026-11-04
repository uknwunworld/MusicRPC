using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace MusicRPC;

public partial class App : System.Windows.Application
{
    Mutex? _mutex;
    System.Windows.Forms.NotifyIcon? _tray;
    PresenceController? _controller;
    UpdateService? _updater;
    string? _notifiedVersion;
    MainWindow? _window;
    public bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "MusicRPC.SingleInstance.5F2B", out bool first);
        var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "MusicRPC.Show.5F2B");
        if (!first)
        {
            // второй запуск — просто показываем окно уже работающего экземпляра
            showEvt.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (showEvt.WaitOne())
            {
                if (Exiting) break;
                Dispatcher.BeginInvoke(new Action(ShowWindow));
            }
        }) { IsBackground = true }.Start();

        var settings = Settings.Load();
        ApplyTheme(settings.LightTheme);
        _controller = new PresenceController(settings);
        _controller.Start();

        _updater = new UpdateService(settings);
        _window = new MainWindow(settings, _controller, _updater);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "MusicRPC",
            Visible = true,
            Icon = LoadIcon(),
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        _updater.Changed += () => Dispatcher.BeginInvoke(new Action(NotifyUpdate));
        _updater.Start();

        bool minimized = e.Args.Contains("--minimized") || (settings.StartMinimized && !string.IsNullOrWhiteSpace(settings.ClientId));
        if (!minimized) ShowWindow();
    }

    static Icon LoadIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (info != null) return new Icon(info.Stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    System.Windows.Forms.ContextMenuStrip BuildMenu()
    {
        var m = new System.Windows.Forms.ContextMenuStrip();
        m.Items.Add("Открыть", null, (_, _) => ShowWindow());
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add("Выход", null, (_, _) => ExitApp());
        return m;
    }

    void NotifyUpdate()
    {
        var u = _updater?.Available;
        if (u == null || _notifiedVersion == u.Tag) return;
        _notifiedVersion = u.Tag;
        if (_window == null || !_window.IsVisible)
            _tray?.ShowBalloonTip(6000, "MusicRPC", $"Доступна версия {u.Version}. Откройте программу, чтобы обновиться.", System.Windows.Forms.ToolTipIcon.Info);
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        Exiting = true;
        _tray?.Dispose();
        _controller?.Dispose();
        _updater?.Dispose();
        _window?.Close();
        Shutdown();
    }

    public static void ApplyTheme(bool light)
    {
        var r = Current.Resources;
        void B(string key, string hex) => r[key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        System.Windows.Media.LinearGradientBrush G(string c1, string c2, double o2 = 1) => new()
        {
            StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(c1), 0),
                new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(c2), o2),
            }
        };

        if (light)
        {
            B("Bg", "#F3F3F3"); B("Card", "#FFFFFF"); B("CardHi", "#F0F0F0"); B("Line", "#E1E1E1");
            B("Text", "#121212"); B("Muted", "#6B6B6B"); B("Accent", "#14A044");
            B("Good", "#14A044"); B("Bad", "#D93A4A");
            B("ChipBg", "#EDEDED"); B("TabOn", "#121212"); B("TabOnText", "#FFFFFF");
            B("Track", "#CBCBCB"); B("ThumbOff", "#FFFFFF"); B("ThumbOn", "#FFFFFF");
            B("BtnBg", "#EDEDED"); B("BtnHover", "#E0E0E0"); B("BtnPress", "#D2D2D2");
            B("CoverBg", "#E4E4E4"); B("Faint", "#9A9A9A"); B("Preview", "#FAFAFA");
            r["AccentGrad"] = G("#1ED760", "#17A84B");
            r["HeroGrad"] = G("#C9F2D9", "#FFFFFF", 0.85);
        }
        else
        {
            B("Bg", "#121212"); B("Card", "#181818"); B("CardHi", "#242424"); B("Line", "#2A2A2A");
            B("Text", "#FFFFFF"); B("Muted", "#B3B3B3"); B("Accent", "#1ED760");
            B("Good", "#1ED760"); B("Bad", "#F15E6C");
            B("ChipBg", "#2A2A2A"); B("TabOn", "#FFFFFF"); B("TabOnText", "#000000");
            B("Track", "#4D4D4D"); B("ThumbOff", "#E0E0E0"); B("ThumbOn", "#FFFFFF");
            B("BtnBg", "#242424"); B("BtnHover", "#333333"); B("BtnPress", "#3D3D3D");
            B("CoverBg", "#282828"); B("Faint", "#7F7F7F"); B("Preview", "#0F0F0F");
            r["AccentGrad"] = G("#1ED760", "#17A84B");
            r["HeroGrad"] = G("#1F5A3A", "#181818", 0.85);
        }
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("MusicRPC", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("MusicRPC", false);
        }
        catch { }
    }
}
