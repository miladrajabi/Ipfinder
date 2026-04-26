using System.Text.Json.Serialization;

namespace IPfinder;

public sealed class IpLocationResponse
{
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("connection")]
    public ConnectionInfo? Connection { get; init; }

    [JsonPropertyName("flag")]
    public FlagInfo? Flag { get; init; }
}

public sealed class ConnectionInfo
{
    [JsonPropertyName("isp")]
    public string? Isp { get; init; }

    [JsonPropertyName("org")]
    public string? Org { get; init; }
}

public sealed class FlagInfo
{
    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }
}
