using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicRPC;

/// <summary>Читает все медиа-сессии Windows (SMTC): Spotify, Яндекс Музыка, браузеры (SoundCloud) и т.д.</summary>
public sealed class MediaWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    readonly Dictionary<string, byte[]?> _thumbs = new();

    public async Task<List<MediaSession>> PollAsync()
    {
        var list = new List<MediaSession>();
        _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var current = _mgr.GetCurrentSession();
        string? currentId = current?.SourceAppUserModelId;

        foreach (var s in _mgr.GetSessions())
        {
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var pb = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();

                var ms = new MediaSession
                {
                    AppId = s.SourceAppUserModelId ?? "",
                    Title = props.Title ?? "",
                    Artist = props.Artist ?? "",
                    Album = props.AlbumTitle ?? "",
                    Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    IsCurrent = s.SourceAppUserModelId == currentId,
                };

                var dur = tl.EndTime - tl.StartTime;
                if (dur > TimeSpan.Zero && dur < TimeSpan.FromHours(24)) ms.Duration = dur;
                var pos = tl.Position - tl.StartTime;
                if (ms.Playing && tl.LastUpdatedTime.Year > 2000)
                {
                    var rate = pb.PlaybackRate ?? 1.0;
                    var elapsed = DateTimeOffset.UtcNow - tl.LastUpdatedTime;
                    if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(6))
                        pos += TimeSpan.FromSeconds(elapsed.TotalSeconds * rate);
                }
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (ms.Duration > TimeSpan.Zero && pos > ms.Duration) pos = ms.Duration;
                ms.Position = pos;

                var key = $"{ms.AppId}|{ms.Artist}|{ms.Title}";
                if (!_thumbs.TryGetValue(key, out var thumb))
                {
                    thumb = await ReadThumbAsync(props.Thumbnail);
                    if (_thumbs.Count > 30) _thumbs.Clear();
                    _thumbs[key] = thumb;
                }
                ms.Thumb = thumb;
                list.Add(ms);
            }
            catch { /* сессия исчезла во время чтения */ }
        }
        return list;
    }

    static async Task<byte[]?> ReadThumbAsync(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            var size = (uint)stream.Size;
            if (size == 0 || size > 8_000_000) return null;
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }
}

static class Native
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static List<string> WindowTitles()
    {
        var res = new List<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            res.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return res;
    }

    public static void TitleBar(IntPtr hwnd, bool dark)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }
}
