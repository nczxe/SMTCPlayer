using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>局域网协议 NDJSON 编解码与消息模型。</summary>
internal static class LanJson
{
    /// <summary>
    /// 统一序列化选项：属性 camelCase、反序列化大小写不敏感、忽略 null、
    /// 不转义非 ASCII（保证中文错误信息可读）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>解析客户端消息；格式非法返回 null（由连接层处理）。</summary>
    public static LanInboundMessage? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<LanInboundMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>客户端 → 服务端消息。</summary>
internal sealed class LanInboundMessage
{
    public string? Type { get; set; }
    public string? Pin { get; set; }
    public string? Action { get; set; }
    public double? Value { get; set; }
}

/// <summary>服务端 → 客户端消息。</summary>
internal sealed class LanOutboundMessage
{
    public required string Type { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public string? Action { get; init; }
    public object? Data { get; init; }
}

/// <summary>status 推送载荷（媒体快照的对外投影）。</summary>
internal sealed record LanStatusPayload
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public string Status { get; init; } = "stopped";
    public string? Source { get; init; }
    public bool IsPlaying { get; init; }
    public bool HasPrevious { get; init; }
    public bool HasNext { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Volume { get; init; }
    public bool Muted { get; init; }
    public long? SongId { get; init; }

    public static LanStatusPayload From(MediaSnapshot s) => new()
    {
        Title = s.Title,
        Artist = s.Artist,
        AlbumTitle = s.AlbumTitle,
        Status = s.Status,
        Source = s.Source,
        IsPlaying = s.IsPlaying,
        HasPrevious = s.HasPrevious,
        HasNext = s.HasNext,
        Position = s.Position,
        Duration = s.Duration,
        Volume = s.Volume,
        Muted = s.Muted,
        SongId = s.SongId,
    };
}
