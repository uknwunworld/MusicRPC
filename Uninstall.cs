using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace MusicRPC;

/// <summary>Удаление программы: запускается из «Установленных приложений» как «MusicRPC.exe --uninstall».</summary>
static class Uninstaller
{
    public static void Run()
    {
        if (MessageBox.Show("Удалить MusicRPC с этого компьютера?\nНастройки программы тоже будут удалены.",
                "MusicRPC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var exe = Environment.ProcessPath ?? "";
        var dir = Path.GetDirectoryName(exe) ?? "";

        foreach (var p in Process.GetProcessesByName("MusicRPC"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        try { using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); run?.DeleteValue("MusicRPC", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicRPC", false); } catch { }

        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MusicRPC.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MusicRPC.lnk"));
        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicRPC"), true); } catch { }

        // Файл запущенной программы удалить нельзя — делаем это отдельным процессом после выхода.
        // Папку убираем только если она опустела, чтобы не задеть чужие файлы.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{exe}\" & rmdir \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }

        MessageBox.Show("MusicRPC удалён.", "MusicRPC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
