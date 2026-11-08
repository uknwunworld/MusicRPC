using System;
using System.IO;
using System.Text.Json;

namespace MusicRPC;

public enum SourceKind { Spotify, Yandex, SoundCloud, OtherBrowser, Other }

public static class SourceNames
{
    public static string Of(SourceKind k) => k switch
    {
        SourceKind.Spotify => "Spotify",
        SourceKind.Yandex => "Яндекс Музыка",
        SourceKind.SoundCloud => "SoundCloud",
        SourceKind.OtherBrowser => "Браузер",
        _ => "Плеер",
    };

    /// <summary>Подпись кнопки в статусе Discord — своя для каждого сервиса.</summary>
    public static string ButtonLabel(SourceKind k) => k switch
    {
        SourceKind.Spotify => "Слушать в Spotify",
        SourceKind.Yandex => "Слушать в Яндекс Музыке",
        SourceKind.SoundCloud => "Слушать в SoundCloud",
        _ => "Открыть трек",
    };

    /// <summary>Значок сервиса на обложке (лежит в репозитории, Discord подтягивает его по ссылке).</summary>
    public static string? BadgeUrl(SourceKind k)
    {
        const string root = "https://raw.githubusercontent.com/uknwunworld/MusicRPC/main/docs/badges/";
        return k switch
        {
            SourceKind.Spotify => root + "spotify.png",
            SourceKind.Yandex => root + "yandex.png",
            SourceKind.SoundCloud => root + "soundcloud.png",
            _ => null,
        };
    }

    /// <summary>Подсказка при наведении на обложку: альбом и сервис.</summary>
    public static string CoverHint(SourceKind k, string album)
    {
        bool named = k is SourceKind.Spotify or SourceKind.Yandex or SourceKind.SoundCloud;
        if (string.IsNullOrWhiteSpace(album)) return Of(k);
        return named ? $"{album.Trim()} · {Of(k)}" : album.Trim();
    }
}

/// <summary>Сырая медиа-сессия из Windows (SMTC).</summary>
public sealed class MediaSession
{
    public string AppId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public bool Playing { get; set; }
    public bool IsCurrent { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public byte[]? Thumb { get; set; }
}

public sealed record NowPlaying(
    string Title, string Artist, string Album,
    TimeSpan Position, TimeSpan Duration, bool Playing,
    SourceKind Source, string AppId, byte[]? Thumb, string ThumbKey,
    string? CoverUrl, string? TrackUrl, string? FoundBy, DateTime Taken);

public enum SearchState { NotUsed, Searching, Found, NotFound, Error }

public sealed class SearchInfo
{
    public SearchState Yandex = SearchState.NotUsed;
    public SearchState ITunes = SearchState.NotUsed;
    public SearchState Deezer = SearchState.NotUsed;
}

public sealed class Settings
{
    /// <summary>Встроенный Application ID (публичный, не секрет) — работает у всех из коробки.</summary>
    public const string DefaultClientId = "1564250441032933558";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ClientId => DefaultClientId;

    // Источники
    public bool UseSpotify { get; set; } = true;
    public bool UseYandex { get; set; } = true;
    public bool UseSoundCloud { get; set; } = true;
    public bool UseOtherBrowser { get; set; } = false;
    public bool UseOtherPlayers { get; set; } = false;

    // Активность
    public bool ShowCover { get; set; } = true;
    public bool ShowButtons { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowPaused { get; set; } = false;
    public bool NameAsTrack { get; set; } = true;   // "Артист — Трек" вместо названия приложения

    // Поиск обложек
    public bool SearchYandex { get; set; } = true;
    public bool SearchITunes { get; set; } = true;
    public bool SearchDeezer { get; set; } = true;

    // Приложение
    public bool LightTheme { get; set; } = false;
    public bool AutoUpdate { get; set; } = true;
    public bool Autostart { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicRPC");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
