using System.Text.Json.Serialization;

namespace SMTCPlayer.Core.Models;

public class AuthStatus
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("pin_policy")]
    public PinPolicy? PinPolicy { get; set; }
}

public class PinPolicy
{
    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }

    [JsonPropertyName("allowed")]
    public string? Allowed { get; set; }
}

public class AuthResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class ApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class HealthStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("auth_configured")]
    public bool AuthConfigured { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("smtc")]
    public SmtcInfo? Smtc { get; set; }

    [JsonPropertyName("netease_watcher")]
    public WatcherInfo? NeteaseWatcher { get; set; }

    [JsonPropertyName("volume")]
    public VolumeInfo? Volume { get; set; }

    [JsonPropertyName("config")]
    public ConfigInfo? Config { get; set; }
}

public class SmtcInfo
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class WatcherInfo
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }
}

public class VolumeInfo
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }
}

public class ConfigInfo
{
    [JsonPropertyName("has_pin")]
    public bool HasPin { get; set; }
}
