using System.Text.Json.Serialization;

namespace SMTCPlayer.Core.Models;

public class PlayerStatus
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "";

    [JsonPropertyName("album_title")]
    public string AlbumTitle { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "stopped";

    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("has_previous")]
    public bool HasPrevious { get; set; }

    [JsonPropertyName("has_next")]
    public bool HasNext { get; set; }

    [JsonPropertyName("volume")]
    public double Volume { get; set; }

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    [JsonPropertyName("volume_available")]
    public bool VolumeAvailable { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("netease_watcher_active")]
    public bool NeteaseWatcherActive { get; set; }

    [JsonPropertyName("song_id")]
    public long? SongId { get; set; }

    public double ProgressPercent => Duration > 0 ? (Position / Duration) * 100 : 0;

    public string PositionText => FormatTime(Position);

    public string DurationText => FormatTime(Duration);

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0) return "0:00";
        int m = (int)(seconds / 60);
        int s = (int)(seconds % 60);
        return $"{m}:{s:D2}";
    }
}
