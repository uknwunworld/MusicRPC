using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MusicRPC.Setup
{
    public partial class MainWindow : Window
    {
        static readonly double[] EqPreset = { 0.35, 0.6, 0.45, 0.85, 0.55, 1.0, 0.7, 0.5, 0.9, 0.4, 0.65, 0.3, 0.75, 0.5, 0.35 };

        string _dir;
        bool _installing;
        bool _canClose = true;

        public MainWindow()
        {
            InitializeComponent();
            VerText.Text = "v" + Installer.Version;
            _dir = Installer.Normalize(App.DirOverride ?? Installer.DefaultDir());
            DirText.Text = _dir;
            SizeText.Text = "Займёт ~" + Math.Max(1, Installer.UnpackedSize() / 1048576) + " МБ";

            if (Installer.IsInstalled())
            {
                Eyebrow.Text = "ОБНОВЛЕНИЕ";
                InstallBtn.Content = "Обновить";
                AutoChk.IsChecked = Installer.WasAutostart();
                DesktopChk.IsChecked = Installer.WasDesktopShortcut();
            }

            BuildEqualizer(animate: App.ScreenshotDir == null);
            Loaded += (s, e) => { if (App.ScreenshotDir != null) Dispatcher.BeginInvoke(new Action(Shoot), DispatcherPriority.ApplicationIdle); };
        }

        // ---------- фирменный «эквалайзер» ----------
        void BuildEqualizer(bool animate)
        {
            for (int i = 0; i < EqPreset.Length; i++)
            {
                var st = new ScaleTransform(1, animate ? 0.3 : EqPreset[i]);
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = 10, Height = 96, RadiusX = 5, RadiusY = 5,
                    Margin = new Thickness(0, 0, i == EqPreset.Length - 1 ? 0 : 8, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    RenderTransformOrigin = new Point(0.5, 1),
                    RenderTransform = st,
                    Opacity = 0.45 + 0.55 * EqPreset[i],
                    Fill = new LinearGradientBrush(Color.FromRgb(0x1E, 0xD7, 0x60), Color.FromRgb(0x0F, 0x5C, 0x2B), 90),
                };
                Eq.Children.Add(bar);
                if (!animate) continue;
                var a = new DoubleAnimation(0.22 + 0.1 * (i % 3), 0.55 + 0.45 * EqPreset[i], TimeSpan.FromMilliseconds(520 + (i * 83) % 470))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds((i * 61) % 420),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
        }

        // левые углы панели скруглены вместе с окном
        void Brand_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Brand.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width + 40, e.NewSize.Height), 17, 17);
        }

        // ---------- окно ----------
        void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase)) { try { DragMove(); } catch { } }
        }
        void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Close_Click(object sender, RoutedEventArgs e) { if (_canClose) Application.Current.Shutdown(); }

        void Change_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Выберите папку для MusicRPC", ShowNewFolderButton = true })
            {
                try { dlg.SelectedPath = Path.GetDirectoryName(_dir); } catch { }
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _dir = Installer.Normalize(dlg.SelectedPath);
                DirText.Text = _dir;
            }
        }

        // ---------- установка ----------
        void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            _installing = true; _canClose = false;
            CloseBtn.Opacity = 0.35;
            var opt = new InstallOptions { Dir = _dir, DesktopShortcut = DesktopChk.IsChecked == true, Autostart = AutoChk.IsChecked == true };
            bool update = Installer.IsInstalled();
            ProgTitle.Text = update ? "Обновляю…" : "Устанавливаю…";
            Show(PageProgress);
            SetProgress(0, "Подготовка…");

            Task.Run(() => Installer.Install(opt, App.NoRegistry, (p, t) => Dispatcher.BeginInvoke(new Action(() => SetProgress(p, t)))))
                .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _installing = false; _canClose = true; CloseBtn.Opacity = 1;
                    if (t.IsFaulted)
                    {
                        var ex = t.Exception == null ? null : t.Exception.GetBaseException();
                        ErrText.Text = (ex == null ? "Неизвестная ошибка." : ex.Message) + "\n\nЗакройте MusicRPC и запустите установщик ещё раз.";
                        Show(PageError);
                    }
                    else Show(PageDone);
                })));
        }

        void SetProgress(double p, string text)
        {
            p = Math.Max(0, Math.Min(1, p));
            Fill.Width = Track.ActualWidth * p;
            ProgText.Text = text;
            ProgPercent.Text = (int)Math.Round(p * 100) + "%";
        }

        void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchChk.IsChecked == true) Installer.Launch(_dir);
            Application.Current.Shutdown();
        }

        void Show(UIElement page)
        {
            foreach (var p in new UIElement[] { PageInstall, PageProgress, PageDone, PageError })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- скриншоты для проверки внешнего вида: /SCREENSHOT=папка ----------
        void Shoot()
        {
            var dir = App.ScreenshotDir;
            Directory.CreateDirectory(dir);
            Show(PageInstall); Save(Path.Combine(dir, "1-install.png"));
            Show(PageProgress); UpdateLayout(); SetProgress(0.62, "Распаковываю файлы…"); Save(Path.Combine(dir, "2-progress.png"));
            Show(PageDone); Save(Path.Combine(dir, "3-done.png"));
            ErrText.Text = "Файл занят другой программой.\n\nЗакройте MusicRPC и запустите установщик ещё раз.";
            Show(PageError); Save(Path.Combine(dir, "4-error.png"));
            Application.Current.Shutdown();
        }

        void Save(string path)
        {
            UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1D)), null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(20, 20, Root.ActualWidth, Root.ActualHeight));
            }
            var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var f = File.Create(path)) enc.Save(f);
        }
    }
}
