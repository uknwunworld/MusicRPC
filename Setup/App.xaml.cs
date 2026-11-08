using System;
using System.Linq;
using System.Windows;

namespace MusicRPC.Setup
{
    public partial class App : Application
    {
        public static string DirOverride;      // /DIR=путь
        public static string ScreenshotDir;    // /SCREENSHOT=папка (проверка внешнего вида)
        public static bool NoRegistry;         // /NOREG — не трогать реестр и ярлыки (для тестов)

        static string Arg(string[] args, string name)
        {
            var a = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            return a == null ? null : a.Substring(name.Length + 1).Trim('"');
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args;
            DirOverride = Arg(args, "/DIR");
            ScreenshotDir = Arg(args, "/SCREENSHOT");
            NoRegistry = args.Any(x => x.Equals("/NOREG", StringComparison.OrdinalIgnoreCase));
            bool silent = args.Any(x => x.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) || x.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase));

            if (silent)
            {
                // Тихая установка (так её запускает автообновление): прежние настройки сохраняем, программу запускаем.
                int code = 0;
                try
                {
                    var dir = Installer.Normalize(DirOverride ?? Installer.DefaultDir());
                    var opt = new InstallOptions
                    {
                        Dir = dir,
                        DesktopShortcut = Installer.WasDesktopShortcut(),
                        Autostart = Installer.WasAutostart(),
                    };
                    Installer.Install(opt, NoRegistry, (p, t) => { });
                    if (!args.Any(x => x.Equals("/NOLAUNCH", StringComparison.OrdinalIgnoreCase))) Installer.Launch(dir);
                }
                catch { code = 1; }
                Shutdown(code);
                return;
            }

            new MainWindow().Show();
        }
    }
}
