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
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
            using var resp = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", _cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Status = "Не удалось проверить обновления";
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(_cts.Token));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver))
            {
                Status = "Не удалось определить версию релиза";
                return;
            }
            if (ver <= Current)
            {
                Available = null;
                Status = $"Установлена последняя версия ({Current})";
                return;
            }

            string prefix = $"https://github.com/{Repo}/releases/download/";
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d &&
                    d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = d[7..];

                long size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                Available = new UpdateInfo(ver, tag, url, size, sha, notes);
                Status = $"Доступна версия {ver}";
                return;
            }
            Status = "В релизе нет установщика";
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

    /// <summary>Скачивает установщик во временную папку и проверяет SHA-256.</summary>
    public async Task<string> DownloadAsync(UpdateInfo u, IProgress<double> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MusicRPC-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }
        var path = Path.Combine(dir, $"{AssetPrefix}-{u.Version}.exe");

        using var resp = await _http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct);
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
        Process.Start(new ProcessStartInfo(path, "/SILENT /NORESTART /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
