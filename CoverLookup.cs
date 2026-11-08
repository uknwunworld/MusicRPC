using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicRPC;

public sealed class LookupResult
{
    public string? CoverUrl;
    public string? TrackUrl;
    public string? FoundBy;
}

/// <summary>Ищет публичную ссылку на обложку и трек: Яндекс Музыка → iTunes → Deezer.
/// Из всех результатов выбирается лучший: совпадение названия, всех артистов и альбома.</summary>
public sealed class CoverLookup
{
    const int Confident = 10;

    sealed record Cand(string Title, string Artists, string Album, string? Cover, string? Link, bool IsAlbum = false);

    readonly HttpClient _http = new(new DirectFallbackHandler(true)) { Timeout = TimeSpan.FromSeconds(3) };
    readonly Dictionary<string, LookupResult> _cache = new();

    // MusicBrainz требует осмысленный User-Agent; редиректы Cover Art Archive читаем сами (нужна итоговая ссылка на картинку)
    readonly HttpClient _mb = new(new DirectFallbackHandler(false)) { Timeout = TimeSpan.FromSeconds(4) };

    public CoverLookup()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MusicRPC/1.0");
        _mb.DefaultRequestHeaders.UserAgent.ParseAdd("MusicRPC/1.2 (https://github.com/uknwunworld/MusicRPC)");
    }

    public async Task<LookupResult> FindAsync(string artist, string title, string album, Settings s, SearchInfo info, CancellationToken ct)
    {
        // Клипы и спец. версии: "Трек (Live) | presented by Spotify" -> "Трек (Live)"
        title = Regex.Replace(title, @"\s*[|｜].*$", "").Trim();
        album = Regex.Replace(album, @"\s*[|｜].*$", "").Trim();
        // У клипа "альбом" равен названию клипа и ничего не говорит о релизе — не требуем его совпадения.
        if (Norm(album).Length > 0 && Norm(album) == Norm(title)) album = "";
        // В запрос — название без скобок и «feat.»: длинное «Track (feat. A, B & C)» поиск часто вообще не находит.
        // Оценка совпадения (Score) и так не учитывает скобки, так что точность не страдает.
        var qTitle = Regex.Replace(title, @"\s*[\(\[].*?[\)\]]", " ");
        qTitle = Regex.Replace(qTitle, @"\s+[-–—]\s+(feat|ft)\b.*$", "", RegexOptions.IgnoreCase).Trim();
        if (qTitle.Length == 0) qTitle = title;
        string key = $"{artist}|{title}|{album}".ToLowerInvariant();
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                info.Yandex = info.ITunes = info.Deezer = SearchState.NotUsed;
                if (hit.FoundBy == "Yandex") info.Yandex = SearchState.Found;
                else if (hit.FoundBy == "iTunes") info.ITunes = SearchState.Found;
                else if (hit.FoundBy == "Deezer") info.Deezer = SearchState.Found;
                return hit;
            }
        }

        info.Yandex = s.SearchYandex ? SearchState.Searching : SearchState.NotUsed;
        info.ITunes = s.SearchITunes ? SearchState.Searching : SearchState.NotUsed;
        info.Deezer = s.SearchDeezer ? SearchState.Searching : SearchState.NotUsed;

        LookupResult? best = null;
        int bestScore = -1;

        // Каждый источник спрашиваем двумя запросами (с альбомом и без) и склеиваем результаты.
        async Task<List<Cand>> Both(Func<string, Task<List<Cand>>> run, Func<string, Task<List<Cand>>> runAlbum)
        {
            var tasks = new List<Task<List<Cand>>> { run($"{artist} {qTitle}") };
            if (!string.IsNullOrWhiteSpace(album))
            {
                tasks.Add(run($"{artist} {qTitle} {album}"));
                tasks.Add(runAlbum($"{artist} {album}"));   // альбом ищем отдельно: самого трека в базе может не быть
            }
            var parts = await Task.WhenAll(tasks);
            return parts.SelectMany(x => x).ToList();
        }

        // Все источники запрашиваются одновременно; результаты разбираем по приоритету.
        var jobs = new List<(string name, Task<List<Cand>?> task, Action<SearchState> set)>();
        void Add(string name, bool enabled, Func<Task<List<Cand>>> run, Action<SearchState> set)
        {
            if (!enabled) return;
            jobs.Add((name, Task.Run<List<Cand>?>(async () =>
            {
                try { return await run(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Diagnostics.LogThrottled("cover-" + name, ex); return null; }
            }), set));
        }
        Add("Yandex", s.SearchYandex, () => Both(q => Yandex(q, ct), q => YandexAlbum(q, ct)), st => info.Yandex = st);
        Add("iTunes", s.SearchITunes, () => Both(q => ITunes(q, ct), q => ITunesAlbum(q, ct)), st => info.ITunes = st);
        Add("Deezer", s.SearchDeezer, () => Both(q => Deezer(q, ct), q => DeezerAlbum(q, ct)), st => info.Deezer = st);

        int i = 0;
        for (; i < jobs.Count; i++)
        {
            var (name, task, set) = jobs[i];
            var list = await task;
            if (list == null) { set(SearchState.Error); continue; }
            var top = list.Select(c => (c, score: Score(artist, title, album, c)))
                          .Where(x => x.score >= 0)
                          .OrderByDescending(x => x.score)
                          .FirstOrDefault();
            if (top.c == null) { set(SearchState.NotFound); continue; }
            set(SearchState.Found);
            if (top.score > bestScore)
            {
                bestScore = top.score;
                best = new LookupResult { CoverUrl = top.c.Cover, TrackUrl = top.c.Link, FoundBy = name };
            }
            if (top.score >= Confident) { i++; break; }
        }
        for (; i < jobs.Count; i++) jobs[i].set(SearchState.NotUsed);

        // Запасной источник: MusicBrainz + Cover Art Archive. Открытая база без ключей и без региональных блокировок —
        // выручает, когда Яндекс Музыка трека не знает, а iTunes/Deezer недоступны. Спрашиваем только если обложки ещё нет.
        if (string.IsNullOrEmpty(best?.CoverUrl))
        {
            try
            {
                using var mbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                mbCts.CancelAfter(TimeSpan.FromSeconds(6));
                var list = await MusicBrainz(artist, qTitle, mbCts.Token);
                var top = list.Where(c => c.Cover != null)
                              .Select(c => (c, score: Score(artist, title, album, c)))
                              .Where(x => x.score >= 0)
                              .OrderByDescending(x => x.score)
                              .FirstOrDefault();
                if (top.c != null)
                {
                    if (best != null) best.CoverUrl = top.c.Cover;
                    else best = new LookupResult { CoverUrl = top.c.Cover, FoundBy = "MusicBrainz" };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Diagnostics.LogThrottled("cover-MusicBrainz", ex); }
        }

        var res = best ?? new LookupResult();
        ct.ThrowIfCancellationRequested();
        lock (_cache)
        {
            if (_cache.Count > 200) _cache.Clear();
            _cache[key] = res;
        }
        return res;
    }

    // ---------- MusicBrainz + Cover Art Archive ----------
    async Task<List<Cand>> MusicBrainz(string artist, string title, CancellationToken ct)
    {
        var res = new List<Cand>();
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var first = Regex.Split(artist, @"[,;&/]|\bfeat\.?\b|\bft\.?\b", RegexOptions.IgnoreCase)[0].Trim();
        if (title.Length == 0 || first.Length == 0) return res;

        var url = "https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query=" +
                  Uri.EscapeDataString($"recording:\"{Q(title)}\" AND artist:\"{Q(first)}\"");
        using var doc = JsonDocument.Parse(await _mb.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("recordings", out var recs)) return res;

        int lookups = 0;   // обращений к Cover Art Archive не больше четырёх
        foreach (var r in recs.EnumerateArray())
        {
            var rTitle = r.TryGetProperty("title", out var rt) ? rt.GetString() ?? "" : "";
            if (Norm(rTitle) != Norm(title)) continue;
            var artists = r.TryGetProperty("artist-credit", out var ac)
                ? string.Join(", ", ac.EnumerateArray().Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                : "";
            if (!r.TryGetProperty("releases", out var rels)) continue;
            foreach (var rel in rels.EnumerateArray().Take(3))
            {
                if (lookups >= 4) return res;
                if (!rel.TryGetProperty("id", out var idp)) continue;
                lookups++;
                var relTitle = rel.TryGetProperty("title", out var rlt) ? rlt.GetString() ?? "" : "";
                var cover = await CoverArt(idp.GetString() ?? "", ct);
                if (cover != null) res.Add(new Cand(rTitle, artists, relTitle, cover, null));
            }
        }
        return res;
    }

    /// <summary>Cover Art Archive отвечает редиректом на картинку, если обложка у релиза есть (иначе 404).</summary>
    async Task<string?> CoverArt(string releaseId, CancellationToken ct)
    {
        if (releaseId.Length == 0) return null;
        using var req = new HttpRequestMessage(HttpMethod.Head, $"https://coverartarchive.org/release/{releaseId}/front-500");
        using var resp = await _mb.SendAsync(req, ct);
        if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
        {
            var abs = loc.IsAbsoluteUri ? loc : new Uri(new Uri("https://coverartarchive.org"), loc);
            return abs.ToString();
        }
        return null;
    }

    // ---------- Яндекс Музыка ----------
    async Task<List<Cand>> Yandex(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://api.music.yandex.net/search?type=track&page=0&text=" + Uri.EscapeDataString(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023231");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tracks", out var tracks) ||
            !tracks.TryGetProperty("results", out var items)) return res;

        foreach (var t in items.EnumerateArray().Take(12))
        {
            var tTitle = t.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            var artists = t.TryGetProperty("artists", out var ar)
                ? string.Join(", ", ar.EnumerateArray().Select(a => a.GetProperty("name").GetString()))
                : "";
            string albumTitle = "";
            string? link = null;
            if (t.TryGetProperty("albums", out var albums) && albums.GetArrayLength() > 0)
            {
                var a0 = albums[0];
                albumTitle = a0.TryGetProperty("title", out var at) ? at.GetString() ?? "" : "";
                link = $"https://music.yandex.ru/album/{a0.GetProperty("id")}/track/{t.GetProperty("id")}";
            }
            string? cover = t.TryGetProperty("coverUri", out var cu) ? cu.GetString() : null;
            if (cover == null && link == null) continue;
            res.Add(new Cand(tTitle, artists, albumTitle, cover == null ? null : "https://" + cover.Replace("%%", "400x400"), link));
        }
        return res;
    }

    // ---------- iTunes ----------
    async Task<List<Cand>> ITunes(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://itunes.apple.com/search?media=music&entity=song&limit=15&term=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        foreach (var t in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string S(string n) => t.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            var art = S("artworkUrl100");
            res.Add(new Cand(S("trackName"), S("artistName"), S("collectionName"),
                art.Length > 0 ? art.Replace("100x100bb", "600x600bb") : null,
                S("trackViewUrl") is { Length: > 0 } l ? l : null));
        }
        return res;
    }

    // ---------- Deezer ----------
    async Task<List<Cand>> Deezer(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://api.deezer.com/search?limit=15&q=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)) return res;
        foreach (var t in data.EnumerateArray())
        {
            var tTitle = t.TryGetProperty("title", out var tn) ? tn.GetString() ?? "" : "";
            var tArtist = t.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            string albumTitle = "";
            string? cover = null;
            if (t.TryGetProperty("album", out var al))
            {
                albumTitle = al.TryGetProperty("title", out var at) ? at.GetString() ?? "" : "";
                cover = al.TryGetProperty("cover_xl", out var cx) ? cx.GetString()
                      : al.TryGetProperty("cover_big", out var cb) ? cb.GetString() : null;
            }
            res.Add(new Cand(tTitle, tArtist, albumTitle, cover, t.TryGetProperty("link", out var l) ? l.GetString() : null));
        }
        return res;
    }

    // ---------- поиск альбомов ----------
    async Task<List<Cand>> YandexAlbum(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://api.music.yandex.net/search?type=album&page=0&text=" + Uri.EscapeDataString(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023231");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("albums", out var albums) ||
            !albums.TryGetProperty("results", out var items)) return res;
        foreach (var a in items.EnumerateArray().Take(10))
        {
            var title = a.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            var artists = a.TryGetProperty("artists", out var ar)
                ? string.Join(", ", ar.EnumerateArray().Select(x => x.GetProperty("name").GetString())) : "";
            string? cover = a.TryGetProperty("coverUri", out var cu) ? cu.GetString() : null;
            if (cover == null) continue;
            res.Add(new Cand(title, artists, title, "https://" + cover.Replace("%%", "400x400"),
                $"https://music.yandex.ru/album/{a.GetProperty("id")}", true));
        }
        return res;
    }

    async Task<List<Cand>> ITunesAlbum(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://itunes.apple.com/search?media=music&entity=album&limit=10&term=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        foreach (var t in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string S(string n) => t.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            var art = S("artworkUrl100");
            res.Add(new Cand(S("collectionName"), S("artistName"), S("collectionName"),
                art.Length > 0 ? art.Replace("100x100bb", "600x600bb") : null,
                S("collectionViewUrl") is { Length: > 0 } l ? l : null, true));
        }
        return res;
    }

    async Task<List<Cand>> DeezerAlbum(string query, CancellationToken ct)
    {
        var res = new List<Cand>();
        var url = "https://api.deezer.com/search/album?limit=10&q=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("data", out var data)) return res;
        foreach (var a in data.EnumerateArray())
        {
            var title = a.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            var artist = a.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            var cover = a.TryGetProperty("cover_xl", out var cx) ? cx.GetString()
                      : a.TryGetProperty("cover_big", out var cb) ? cb.GetString() : null;
            res.Add(new Cand(title, artist, title, cover, a.TryGetProperty("link", out var l) ? l.GetString() : null, true));
        }
        return res;
    }

    // ---------- оценка совпадения ----------
    static string Norm(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"\(.*?\)|\[.*?\]", " ");
        return Regex.Replace(s, @"[^\p{L}\p{N}]+", "");
    }

    /// <summary>-1 — не подходит; чем больше, тем лучше (макс. 12).</summary>
    static int Score(string qArtist, string qTitle, string qAlbum, Cand c)
    {
        var qt = Norm(qTitle); var rt = Norm(c.Title);
        if (c.IsAlbum)
        {
            // релиз целиком: нужен точный альбом и тот же артист
            var qal0 = Norm(qAlbum);
            if (qal0.Length == 0 || qal0 != Norm(c.Album)) return -1;
            var ras0 = Regex.Split(c.Artists, @"[,;&/]", RegexOptions.IgnoreCase).Select(Norm).Where(x => x.Length > 0);
            var qas0 = Regex.Split(qArtist, @"[,;&/]|\bfeat\.?\b|\bft\.?\b|\bx\b", RegexOptions.IgnoreCase).Select(Norm).Where(x => x.Length > 0);
            return qas0.Any(q => ras0.Contains(q)) ? 11 : -1;
        }
        if (qt.Length == 0 || rt.Length == 0) return -1;
        int score;
        if (qt == rt) score = 4;
        else if (qt.Contains(rt) || rt.Contains(qt)) score = 2;
        else return -1;

        const string Split = @"[,;&/]|\bfeat\.?\b|\bft\.?\b|\bx\b";
        var ras = Regex.Split(c.Artists, Split, RegexOptions.IgnoreCase).Select(Norm).Where(x => x.Length > 0).ToList();
        var qas = Regex.Split(qArtist, Split, RegexOptions.IgnoreCase).Select(Norm).Where(x => x.Length > 0).ToList();
        if (qas.Count == 0 || ras.Count == 0) score += 2;
        else
        {
            // имя артиста должно совпасть целиком (иначе "Hanz" совпадёт с "Hanz Zimmer")
            int hit = qas.Count(q => ras.Any(r => r == q));
            if (hit == 0) return -1;
            score += (int)Math.Round(4.0 * hit / qas.Count);
        }

        // Альбом: если он известен и у кандидата другой — это другой релиз (сборник и т.п.), обложка будет чужой.
        var qal = Norm(qAlbum); var ral = Norm(c.Album);
        if (qal.Length == 0) score += 4;
        else if (ral.Length == 0) score += 0;
        else if (qal == ral || qal.Contains(ral) || ral.Contains(qal)) score += 4;
        else return -1;
        return score;
    }
}

/// <summary>Запросы идут через системный прокси Windows. Если он прописан, но сам прокси-клиент (VPN и т.п.) выключен,
/// соединение отвергается и поиск обложек не работал вовсе — тогда повторяем запрос напрямую.</summary>
sealed class DirectFallbackHandler : DelegatingHandler
{
    readonly HttpMessageInvoker _direct;

    public DirectFallbackHandler(bool autoRedirect) : base(new HttpClientHandler { AllowAutoRedirect = autoRedirect })
    {
        _direct = new HttpMessageInvoker(new HttpClientHandler { AllowAutoRedirect = autoRedirect, UseProxy = false });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await base.SendAsync(request, ct); }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException && request.Content == null)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return await _direct.SendAsync(copy, ct);
        }
    }
}
