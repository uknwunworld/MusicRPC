using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicRPC;

public sealed record UpdateInfo(Version Version, string Tag, string Url, long Size, string? Sha256, string Notes);

/// <summary>Проверяет GitHub Releases, скачивает установщик новой версии и запускает его.</summary>
public sealed class UpdateService : IDisposable
{
    const string Repo = "uknwunworld/MusicRPC";
    const string AssetPrefix = "MusicRPC-Setup";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly Settings _s;
    readonly CancellationTokenSource _cts = new();

    public UpdateInfo? Available { get; private set; }
    public string Status { get; private set; } = "";
    public bool Checking { get; private set; }
    public event Action? Changed;

    public UpdateService(Settings s)
    {
        _s = s;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicRPC-Updater/" + Current);
    }

    /// <summary>Текущая версия программы (для проверки можно подменить MUSICRPC_FAKE_VERSION).</summary>
    public static Version Current
    {
        get
        {
            var fake = Environment.GetEnvironmentVariable("MUSICRPC_FAKE_VERSION");
            if (fake != null && Version.TryParse(fake, out var fv)) return fv;
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), _cts.Token);
            while (!_cts.IsCancellationRequested)
            {
                if (_s.AutoUpdate) await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(6), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    });

    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        Status = "Проверяю обновления…";
        Changed?.Invoke();
        try
        {
            // Основной путь — GitHub API (даёт SHA-256). Он ограничен 60 запросами/час на IP,
            // поэтому при отказе проверяем через обычный редирект /releases/latest.
            UpdateInfo? latest = null;
            try { latest = await FromApiAsync(_cts.Token); } catch (OperationCanceledException) { throw; } catch { }
            latest ??= await FromRedirectAsync(_cts.Token);

            if (latest == null)
            {
                Status = "Не удалось проверить обновления";
            }
            else if (latest.Version <= Current)
            {
                Available = null;
                Status = $"Установлена последняя версия ({Current})";
            }
            else
            {
                Available = latest;
                Status = $"Доступна версия {latest.Version}";
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            Status = "Нет связи с GitHub";
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    string AssetUrlPrefix => $"https://github.com/{Repo}/releases/download/";

    async Task<UpdateInfo?> FromApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !url.StartsWith(AssetUrlPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d &&
                d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..];
            long size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(ver, tag, url, size, sha, notes);
        }
        return null;
    }

    async Task<UpdateInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MusicRPC-Updater/" + Current);
        using var resp = await c.GetAsync($"https://github.com/{Repo}/releases/latest", ct);
        var loc = resp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc) || !loc.Contains("/releases/tag/")) return null;
        var tag = loc[(loc.LastIndexOf('/') + 1)..];
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
        var url = $"{AssetUrlPrefix}{tag}/{AssetPrefix}-{ver}.exe";
        return new UpdateInfo(ver, tag, url, 0, null, "");
    }

    /// <summary>Скачивает установщик во временную папку и проверяет SHA-256 (если он известен).</summary>
    public async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        // Сеть бывает нестабильной (обрывы TLS) — пробуем несколько раз.
        Exception? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { return await DownloadOnceAsync(u, progress, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                last = ex;
                progress.Report(0);
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct);
            }
        }
        throw last ?? new InvalidOperationException("Не удалось скачать обновление");
    }

    async Task<string> DownloadOnceAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MusicRPC-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }
        var path = Path.Combine(dir, $"{AssetPrefix}-{u.Version}.exe");

        using var req = new HttpRequestMessage(HttpMethod.Get, u.Url);
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? u.Size;

        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        if (u.Sha256 != null)
        {
            var actual = Convert.ToHexString(sha.Hash!);
            if (!actual.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidDataException("Контрольная сумма не совпала");
            }
        }
        return path;
    }

    /// <summary>Запускает установщик: тихо ставит поверх и сам перезапускает программу.</summary>
    public static void LaunchInstaller(string path)
    {
        // Установщик запускается с задержкой в отдельном cmd: к этому моменту программа уже завершится
        // и освободит свой mutex (иначе Inno Setup решит, что приложение запущено, и прервётся).
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping 127.0.0.1 -n 4 >nul & \"{path}\" /SILENT /NORESTART /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
