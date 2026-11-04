using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MusicRPC;

/// <summary>Связывает всё: читает плееры → выбирает трек → ищет обложку → обновляет Discord.</summary>
public sealed class PresenceController : IDisposable
{
    static readonly string[] BrowserTokens = { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser" };

    readonly Settings _s;
    readonly MediaWatcher _watcher = new();
    readonly CoverLookup _lookup = new();
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, SourceKind> _sticky = new();

    public readonly DiscordIpc Discord = new();

    public NowPlaying? Now { get; private set; }
    public SearchInfo Search { get; private set; } = new();
    public IReadOnlyList<string> SessionLines { get; private set; } = Array.Empty<string>();
    public bool PresenceActive { get; private set; }

    int _version;
    volatile bool _lookupDone = true;
    string? _trackKey;
    LookupResult? _result;
    CancellationTokenSource? _lookupCts;
    string? _selectedApp;
    string? _lastSentKey;
    long _lastStart;
    DateTime _lastSend = DateTime.MinValue;
    DateTime _lastConnectTry = DateTime.MinValue;
    string _connectedId = "";
    bool _forceReconnect;

    public PresenceController(Settings s) => _s = s;

    public void Start() => _ = Task.Run(Loop);

    /// <summary>Настройки изменились — пересобрать статус.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    public void Reconnect() { _forceReconnect = true; }

    async Task Loop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (OperationCanceledException) { break; }
            catch { }
            try { await Task.Delay(400, ct); } catch { break; }
        }
    }

    async Task Tick(CancellationToken ct)
    {
        var sessions = await _watcher.PollAsync();
        var titles = sessions.Any(x => IsBrowser(x.AppId)) ? Native.WindowTitles() : new List<string>();

        var classified = sessions.Select(x => (s: x, kind: Classify(x, titles))).ToList();
        SessionLines = classified.Select(c =>
            $"{(c.s.Playing ? "▶" : "⏸")}  {c.s.AppId}  ·  {SourceNames.Of(c.kind)}  ·  " +
            $"{(string.IsNullOrWhiteSpace(c.s.Title) ? "—" : c.s.Artist + " — " + c.s.Title)}" +
            (Enabled(c.kind) ? "" : "   (выключено)")).ToList();

        // выбор активной сессии
        var cands = classified.Where(c => Enabled(c.kind) && !string.IsNullOrWhiteSpace(c.s.Title)).ToList();
        var playing = cands.Where(c => c.s.Playing).ToList();
        (MediaSession s, SourceKind kind)? pick = null;
        var pool = playing.Count > 0 ? playing : cands;
        if (pool.Count > 0)
        {
            int idx = pool.FindIndex(c => c.s.AppId == _selectedApp);
            if (idx < 0) idx = pool.FindIndex(c => c.s.IsCurrent);
            if (idx < 0) idx = 0;
            pick = pool[idx];
        }

        if (pick == null)
        {
            Now = null;
            _trackKey = null; _result = null;
            Search = new SearchInfo();
        }
        else
        {
            var (ms, kind) = pick.Value;
            _selectedApp = ms.AppId;
            string tk = $"{ms.Artist}|{ms.Title}";
            if (tk != _trackKey)
            {
                _trackKey = tk;
                _result = null;
                StartLookup(ms.Artist, ms.Title, ms.Album, tk);
            }
            Now = new NowPlaying(ms.Title, ms.Artist, ms.Album, ms.Position, ms.Duration, ms.Playing, kind,
                ms.AppId, ms.Thumb, $"{ms.AppId}|{tk}", _result?.CoverUrl, _result?.TrackUrl, _result?.FoundBy, DateTime.UtcNow);
        }

        await ManageConnection(ct);
        await UpdatePresence(ct);
    }

    void StartLookup(string artist, string title, string album, string trackKey)
    {
        _lookupCts?.Cancel();
        var cts = _lookupCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var info = new SearchInfo();
        Search = info;
        if (!(_s.ShowCover || _s.ShowButtons) || string.IsNullOrWhiteSpace(title))
        {
            _lookupDone = true;
            return;
        }
        _lookupDone = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _lookup.FindAsync(artist, title, album, _s, info, cts.Token);
                if (_trackKey == trackKey) _result = r;
            }
            catch { }
            finally
            {
                // статус нового трека отправляем только когда поиск обложки завершён
                if (_trackKey == trackKey) _lookupDone = true;
            }
        });
    }

    async Task ManageConnection(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.ClientId))
        {
            if (Discord.Connected) Discord.Disconnect();
            _connectedId = "";
            return;
        }
        bool idChanged = _connectedId != _s.ClientId.Trim();
        if (!Discord.Connected || idChanged || _forceReconnect)
        {
            if (!idChanged && !_forceReconnect && (DateTime.UtcNow - _lastConnectTry).TotalSeconds < 5) return;
            _forceReconnect = false;
            _lastConnectTry = DateTime.UtcNow;
            _lastSentKey = null;
            PresenceActive = false;
            bool ok = await Discord.ConnectAsync(_s.ClientId.Trim(), ct);
            _connectedId = ok ? _s.ClientId.Trim() : "";
        }
    }

    async Task UpdatePresence(CancellationToken ct)
    {
        var np = Now;
        bool show = np != null && (np.Playing || _s.ShowPaused);

        if (!Discord.Connected) { PresenceActive = false; return; }

        if (!show)
        {
            if (_lastSentKey != "clear")
            {
                await Discord.SetActivityAsync(null, ct);
                _lastSentKey = "clear";
                _lastSend = DateTime.UtcNow;
            }
            PresenceActive = false;
            return;
        }

        var n = np!;
        // ждём обложку, чтобы в Discord сразу появился правильный статус, а не промежуточный
        if ((_s.ShowCover || _s.ShowButtons) && !_lookupDone) return;
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long start = nowSec - (long)n.Position.TotalSeconds;
        bool useCover = _s.ShowCover && !string.IsNullOrEmpty(n.CoverUrl) && n.CoverUrl!.Length <= 256;
        bool useBtn = _s.ShowButtons && !string.IsNullOrEmpty(n.TrackUrl) && n.TrackUrl!.Length <= 512;
        string key = $"{_version}|{n.Source}|{n.Artist}|{n.Title}|{n.Playing}|{useCover}{n.CoverUrl}|{useBtn}{n.TrackUrl}";

        bool drift = n.Playing && _s.ShowProgress && Math.Abs(start - _lastStart) >= 3;
        if (key == _lastSentKey && !drift) { PresenceActive = true; return; }
        if ((DateTime.UtcNow - _lastSend).TotalSeconds < 1) return;

        var act = new JsonObject
        {
            ["type"] = 2, // Listening
            ["details"] = Fit(n.Title),
            ["state"] = Fit(n.Playing ? n.Artist : "⏸ " + n.Artist),
            ["instance"] = false,
        };
        if (_s.NameAsTrack)
            act["name"] = Fit(string.IsNullOrWhiteSpace(n.Artist) ? n.Title : $"{n.Artist} — {n.Title}");

        if (n.Playing && _s.ShowProgress)
        {
            var ts = new JsonObject { ["start"] = start };
            if (n.Duration > TimeSpan.Zero) ts["end"] = start + (long)n.Duration.TotalSeconds;
            act["timestamps"] = ts;
        }
        if (useCover)
        {
            act["assets"] = new JsonObject
            {
                ["large_image"] = n.CoverUrl,
                ["large_text"] = Fit(string.IsNullOrWhiteSpace(n.Album) ? SourceNames.Of(n.Source) : n.Album),
            };
        }
        if (useBtn)
        {
            act["buttons"] = new JsonArray(new JsonObject { ["label"] = "Открыть трек", ["url"] = n.TrackUrl });
        }

        await Discord.SetActivityAsync(act, ct);
        _lastSentKey = key;
        _lastStart = start;
        _lastSend = DateTime.UtcNow;
        PresenceActive = true;
    }

    static string Fit(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length < 2) s += "​​";
        return s.Length > 128 ? s[..127] + "…" : s;
    }

    static bool IsBrowser(string appId)
    {
        var id = appId.ToLowerInvariant();
        return BrowserTokens.Any(id.Contains);
    }

    bool Enabled(SourceKind k) => k switch
    {
        SourceKind.Spotify => _s.UseSpotify,
        SourceKind.Yandex => _s.UseYandex,
        SourceKind.SoundCloud => _s.UseSoundCloud,
        SourceKind.OtherBrowser => _s.UseOtherBrowser,
        _ => _s.UseOtherPlayers,
    };

    SourceKind Classify(MediaSession s, List<string> titles)
    {
        var id = s.AppId.ToLowerInvariant();
        if (id.Contains("spotify")) return SourceKind.Spotify;

        if (IsBrowser(id))
        {
            var key = $"{id}|{s.Artist}|{s.Title}";
            var kind = SourceKind.OtherBrowser;
            if (titles.Any(t => t.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)))
                kind = SourceKind.SoundCloud;
            else if (titles.Any(t => t.Contains("Яндекс Музык", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("Yandex Music", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("music.yandex", StringComparison.OrdinalIgnoreCase)))
                kind = SourceKind.Yandex;

            if (kind != SourceKind.OtherBrowser) _sticky[key] = kind;
            else if (_sticky.TryGetValue(key, out var k)) kind = k;
            if (_sticky.Count > 100) _sticky.Clear();
            return kind;
        }

        if (id.Contains("yandex") || id.Contains("яндекс")) return SourceKind.Yandex;
        return SourceKind.Other;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Discord.SetActivityAsync(null, CancellationToken.None).Wait(500); } catch { }
        Discord.Dispose();
    }
}
