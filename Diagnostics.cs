using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MusicRPC;

/// <summary>Лог ошибок и сборка отчёта для кнопки «Сообщить об ошибке».</summary>
public static class Diagnostics
{
    const string Repo = "uknwunworld/MusicRPC";
    const long MaxLogBytes = 256 * 1024;
    // GitHub принимает в ссылке ~8 КБ; кириллица в URL раздувается втрое, поэтому в заготовку кладём немного.
    const int UrlBodyLimit = 1800;

    static readonly object Gate = new();
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicRPC");
    public static string LogPath => Path.Combine(Dir, "log.txt");

    public static void Log(string where, Exception? ex = null, string? note = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    // оставляем последнюю половину файла
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
                }
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(where).Append("] ");
                if (note != null) sb.Append(note).Append(' ');
                if (ex != null) sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace('\n', ' ').Replace('\r', ' '));
                File.AppendAllText(LogPath, sb.AppendLine().ToString());
            }
        }
        catch { /* лог не должен ронять программу */ }
    }

    /// <summary>Повторяющиеся ошибки (цикл раз в 400 мс) пишем не чаще раза в минуту на источник.</summary>
    static readonly Dictionary<string, DateTime> _throttle = new();
    public static void LogThrottled(string where, Exception ex)
    {
        var key = where + ex.GetType().Name + ex.Message;
        lock (_throttle)
        {
            if (_throttle.TryGetValue(key, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(1)) return;
            _throttle[key] = DateTime.UtcNow;
        }
        Log(where, ex);
    }

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("crash", e.ExceptionObject as Exception, e.ExceptionObject as Exception == null ? "non-exception" : null);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Log("task", e.Exception?.GetBaseException()); e.SetObserved(); };
    }

    /// <summary>Технический отчёт: версия, система, настройки, состояние и хвост лога. Без названий треков.</summary>
    public static string BuildReport(Settings s, PresenceController c, UpdateService u)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MusicRPC " + UpdateService.Current);
        sb.AppendLine("OS: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
        sb.AppendLine(".NET: " + Environment.Version);
        sb.AppendLine("Discord: " + c.Discord.Status + (c.Discord.LastError != null ? " (" + c.Discord.LastError + ")" : ""));
        sb.AppendLine("Обновления: " + u.Status);
        sb.AppendLine($"Поиск обложек: Yandex={c.Search.Yandex}, iTunes={c.Search.ITunes}, Deezer={c.Search.Deezer}");
        var np = c.Now;
        sb.AppendLine("Источник сейчас: " + (np == null ? "нет" : $"{np.Source} ({np.AppId}), играет={np.Playing}"));
        sb.AppendLine();
        sb.AppendLine("Настройки:");
        sb.AppendLine(JsonSerializer.Serialize(s));
        sb.AppendLine();
        sb.AppendLine("Последние записи лога:");
        var tail = ReadLogTail(40);
        sb.AppendLine(tail.Count == 0 ? "(ошибок не записано)" : string.Join(Environment.NewLine, tail));
        return sb.ToString();
    }

    static List<string> ReadLogTail(int n)
    {
        try { return File.Exists(LogPath) ? File.ReadAllLines(LogPath).TakeLast(n).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Одна кнопка: полный отчёт в буфер обмена + файл на диске, браузер открывается на новом issue.
    /// Ничего не отправляется само — пользователь сам нажимает «Submit» на GitHub.
    /// </summary>
    public static string Report(Settings s, PresenceController c, UpdateService u)
    {
        var full = BuildReport(s, c, u);
        string? file = null;
        try
        {
            Directory.CreateDirectory(Dir);
            file = Path.Combine(Dir, "report.txt");
            File.WriteAllText(file, full);
        }
        catch (Exception ex) { Log("report", ex); }

        try { System.Windows.Clipboard.SetText(full); } catch (Exception ex) { Log("clipboard", ex); }

        var shortBody = "**Что случилось?**\n\n\n\n<details><summary>Технический отчёт</summary>\n\n```\n" +
                        Truncate(full, UrlBodyLimit) + "\n```\n\n(Полный отчёт скопирован в буфер обмена — вставьте его сюда через Ctrl+V.)\n</details>";
        var url = $"https://github.com/{Repo}/issues/new?title={Uri.EscapeDataString("Ошибка в " + UpdateService.Current)}&body={Uri.EscapeDataString(shortBody)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log("report-open", ex); }
        return file ?? "";
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
}
