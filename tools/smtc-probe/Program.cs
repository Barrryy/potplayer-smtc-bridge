// SmtcProbe —— 从系统那一侧读回 SMTC 会话，用来确认桥接到底写进去没有。
//
// 为什么需要它：本项目的全部价值就是把元数据写进 PotPlayer 的 SMTC 会话，而这件事
// 只能从系统侧验证 —— 注入模块自己的日志只能说明它调用了谁（只看到 `inject ok`
// 并不代表元数据写成了）。任何读 SMTC 的程序（Windows 媒体浮窗、Discord 等）看到的
// 都是同一个来源，所以这里读到的就是它们看到的。
//
//   SmtcProbe.exe              列出所有会话与媒体属性
//   SmtcProbe.exe --app Pot    只看 AUMID 匹配的会话（子串匹配，忽略大小写）
//   SmtcProbe.exe --json       输出 JSON，便于脚本消费
//   SmtcProbe.exe --help
//
// 注意：要在**普通用户上下文**里运行。受限令牌（例如沙箱）下 WinRT 会话管理器会
// 直接返回「指定的服务不存在」，读不到任何会话。

using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Media.Control;

string? filter = null;
var json = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--app" when i + 1 < args.Length:
            filter = args[++i];
            break;
        case "--json":
            json = true;
            break;
        case "-h":
        case "--help":
            PrintHelp();
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            PrintHelp();
            return 2;
    }
}

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var current = manager.GetCurrentSession();
var sessions = new List<ProbeSession>();

foreach (var session in manager.GetSessions())
{
    var aumid = session.SourceAppUserModelId;
    if (filter is not null && !aumid.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();

    // 注意区分两种情况：调用抛异常 / 调用返回了 null 对象。这是排查
    // 「别的程序读得到、我读不到」时最关键的一条信息。
    GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
    string? mediaError = null;
    try
    {
        media = await session.TryGetMediaPropertiesAsync();
        if (media is null) mediaError = "TryGetMediaPropertiesAsync returned null";
    }
    catch (Exception ex)
    {
        mediaError = $"{ex.GetType().Name}: {ex.Message}";
    }

    sessions.Add(new ProbeSession
    {
        Aumid = aumid,
        IsCurrent = current is not null && current.SourceAppUserModelId == aumid,
        Status = playback.PlaybackStatus.ToString(),
        PlaybackType = playback.PlaybackType?.ToString(),
        Position = timeline.Position.ToString(),
        Duration = timeline.EndTime.ToString(),
        Title = media?.Title,
        Artist = media?.Artist,
        AlbumArtist = media?.AlbumArtist,
        Album = media?.AlbumTitle,
        Track = media?.TrackNumber ?? 0,
        TrackCount = media?.AlbumTrackCount ?? 0,
        Genres = media?.Genres?.ToArray() ?? Array.Empty<string>(),
        HasThumbnail = media?.Thumbnail is not null,
        MediaError = mediaError,
    });
}

if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(new ProbeReport
    {
        CapturedAt = DateTimeOffset.Now,
        Count = sessions.Count,
        Sessions = sessions,
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    }));
    return 0;
}

if (sessions.Count == 0)
{
    Console.WriteLine(filter is null
        ? "no SMTC session"
        : $"no SMTC session matching '{filter}'");
    return 0;
}

foreach (var session in sessions)
{
    Console.WriteLine($"{session.Aumid}{(session.IsCurrent ? "  (current)" : string.Empty)}");
    Console.WriteLine($"  status   {session.Status}  {session.PlaybackType}");
    Console.WriteLine($"  timeline {session.Position} / {session.Duration}");

    if (session.MediaError is not null)
    {
        Console.WriteLine($"  media    !! {session.MediaError}");
    }
    else
    {
        Console.WriteLine($"  title    {Show(session.Title)}");
        Console.WriteLine($"  artist   {Show(session.Artist)}");
        Console.WriteLine($"  album    {Show(session.Album)}{(session.AlbumArtist is null ? string.Empty : $"  (album artist: {session.AlbumArtist})")}");
        Console.WriteLine($"  track    {(session.TrackCount > 0 ? $"{session.Track} / {session.TrackCount}" : session.Track.ToString())}{(session.Genres.Length > 0 ? $"   genres: {string.Join(", ", session.Genres)}" : string.Empty)}");
        Console.WriteLine($"  cover    {(session.HasThumbnail ? "yes" : "no")}");
    }

    Console.WriteLine();
}

return 0;

static string Show(string? value) =>
    string.IsNullOrEmpty(value) ? "(empty)" : value;

static void PrintHelp()
{
    Console.WriteLine("SmtcProbe - dump what Windows SMTC exposes for each media session");
    Console.WriteLine();
    Console.WriteLine("usage: SmtcProbe.exe [--app <substring>] [--json]");
    Console.WriteLine();
    Console.WriteLine("  --app <substring>  only sessions whose AUMID contains the text");
    Console.WriteLine("  --json             emit JSON instead of the text report");
    Console.WriteLine();
    Console.WriteLine("Run it in a normal user context: restricted tokens (sandboxes, some");
    Console.WriteLine("service accounts) cannot reach the SMTC session manager at all.");
}

/// <summary>一个会话的快照。字段名直接照搬 WinRT，方便与其它工具对照。</summary>
internal sealed class ProbeSession
{
    public string Aumid { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? PlaybackType { get; init; }
    public string Position { get; init; } = string.Empty;
    public string Duration { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int Track { get; init; }
    public int TrackCount { get; init; }
    public string[] Genres { get; init; } = Array.Empty<string>();
    public bool HasThumbnail { get; init; }
    /// <summary>读媒体属性失败时的原因；正常为 null。</summary>
    public string? MediaError { get; init; }
}

internal sealed class ProbeReport
{
    public DateTimeOffset CapturedAt { get; init; }
    public int Count { get; init; }
    public List<ProbeSession> Sessions { get; init; } = new();
}
