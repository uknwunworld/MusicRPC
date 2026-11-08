using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace MusicRPC.Setup
{
    sealed class InstallOptions
    {
        public string Dir;
        public bool DesktopShortcut;
        public bool Autostart;
    }

    /// <summary>Вся логика установки: распаковка, ярлыки, запись в реестр. Без UI.</summary>
    static class Installer
    {
        const string AppExe = "MusicRPC.exe";
        const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        const string InnoKey = "{6B1E3C52-7A0D-4F0B-9C6E-2D5F8E4A9B11}_is1";   // запись прежнего установщика (Inno Setup)
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        static string LnkPath(Environment.SpecialFolder f) { return Path.Combine(Environment.GetFolderPath(f), "MusicRPC.lnk"); }

        /// <summary>Куда ставить по умолчанию: туда же, где уже стоит (в том числе поставленная прежним установщиком).</summary>
        public static string DefaultDir()
        {
            foreach (var name in new[] { "MusicRPC", InnoKey })
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Uninstall + name))
                    {
                        var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc)) return loc.TrimEnd('\\', '/');
                    }
                }
                catch { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MusicRPC");
        }

        public static bool WasAutostart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MusicRPC") != null; }
            catch { return false; }
        }

        public static bool WasDesktopShortcut() { return File.Exists(LnkPath(Environment.SpecialFolder.DesktopDirectory)); }

        public static bool IsInstalled()
        {
            try { return Registry.CurrentUser.OpenSubKey(Uninstall + "MusicRPC") != null || Registry.CurrentUser.OpenSubKey(Uninstall + InnoKey) != null; }
            catch { return false; }
        }

        /// <summary>Размер распакованной программы, байт (записан в начале payload).</summary>
        public static long UnpackedSize()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.bin"))
            {
                if (s == null) return 0;
                var b = new byte[8];
                s.Read(b, 0, 8);
                return BitConverter.ToInt64(b, 0);
            }
        }

        /// <summary>Папка установки всегда называется MusicRPC, чтобы при удалении не задеть чужие файлы.</summary>
        public static string Normalize(string dir)
        {
            dir = dir.TrimEnd('\\', '/');
            return string.Equals(Path.GetFileName(dir), "MusicRPC", StringComparison.OrdinalIgnoreCase) ? dir : Path.Combine(dir, "MusicRPC");
        }

        public static void Install(InstallOptions o, bool noRegistry, Action<double, string> report)
        {
            var dir = o.Dir;
            var target = Path.Combine(dir, AppExe);

            report(0.02, "Закрываю MusicRPC…");
            if (!noRegistry) CloseRunning();   // в тестовом режиме запущенную программу не трогаем

            Directory.CreateDirectory(dir);
            var tmp = target + ".new";

            report(0.05, "Распаковываю файлы…");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.bin"))
            {
                if (res == null) throw new InvalidOperationException("Установщик собран без файлов программы.");
                var head = new byte[8];
                res.Read(head, 0, 8);
                long total = BitConverter.ToInt64(head, 0);
                using (var def = new DeflateStream(res, CompressionMode.Decompress))
                using (var outp = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    long done = 0; int n;
                    while ((n = def.Read(buf, 0, buf.Length)) > 0)
                    {
                        outp.Write(buf, 0, n);
                        done += n;
                        report(0.05 + 0.85 * (total > 0 ? (double)done / total : 0), "Распаковываю файлы…");
                    }
                }
            }

            report(0.92, "Устанавливаю…");
            Replace(tmp, target);
            foreach (var f in new[] { "unins000.exe", "unins000.dat", "unins000.msg" })
                try { File.Delete(Path.Combine(dir, f)); } catch { }

            if (noRegistry) { report(1, "Готово"); return; }

            report(0.95, "Создаю ярлыки…");
            try { Shortcut(LnkPath(Environment.SpecialFolder.Programs), target, dir, null); } catch { }
            var desk = LnkPath(Environment.SpecialFolder.DesktopDirectory);
            try
            {
                if (o.DesktopShortcut) Shortcut(desk, target, dir, null);
                else if (File.Exists(desk)) File.Delete(desk);
            }
            catch { }

            report(0.98, "Регистрирую программу…");
            try
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (o.Autostart) run.SetValue("MusicRPC", "\"" + target + "\" --minimized");
                    else run.DeleteValue("MusicRPC", false);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(Uninstall + "MusicRPC"))
                {
                    k.SetValue("DisplayName", "MusicRPC");
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "murphydev");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", target);
                    k.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                    k.SetValue("URLInfoAbout", "https://github.com/uknwunworld/MusicRPC");
                    k.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                Registry.CurrentUser.DeleteSubKeyTree(Uninstall + InnoKey, false);   // убираем запись прежнего установщика
            }
            catch { }
            report(1, "Готово");
        }

        public static void Launch(string dir)
        {
            try
            {
                var exe = Path.Combine(dir, AppExe);
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
            }
            catch { }
        }

        static void CloseRunning()
        {
            foreach (var p in Process.GetProcessesByName("MusicRPC"))
                try { p.Kill(); p.WaitForExit(4000); } catch { }
        }

        static void Replace(string tmp, string target)
        {
            // сразу после закрытия программы файл может быть занят ещё доли секунды
            for (int i = 0; ; i++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                    return;
                }
                catch (IOException) { if (i >= 20) throw; Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { if (i >= 20) throw; Thread.Sleep(300); }
            }
        }

        static void Shortcut(string lnk, string target, string dir, string args)
        {
            // через COM-отражение, без dynamic (чтобы не тянуть Microsoft.CSharp)
            var t = Type.GetTypeFromProgID("WScript.Shell");
            var sh = Activator.CreateInstance(t);
            var s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            var st = s.GetType();
            Action<string, object> set = (n, v) => st.InvokeMember(n, BindingFlags.SetProperty, null, s, new[] { v });
            set("TargetPath", target);
            set("WorkingDirectory", dir);
            set("IconLocation", target + ",0");
            set("Description", "MusicRPC");
            if (args != null) set("Arguments", args);
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
        }
    }
}
