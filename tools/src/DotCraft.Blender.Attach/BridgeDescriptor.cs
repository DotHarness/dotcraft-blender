using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DotCraft.Blender.Attach;

public sealed record BridgeDescriptor
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("blenderVersion")] public string BlenderVersion { get; init; } = string.Empty;
    [JsonPropertyName("background")] public bool Background { get; init; }
    [JsonPropertyName("filepath")] public string Filepath { get; init; } = string.Empty;
    [JsonPropertyName("startedAt")] public double StartedAt { get; init; }

    /// <summary>The descriptor file this was read from.</summary>
    [JsonIgnore] public string? Path { get; init; }

    public JsonObject ToSummary() => new()
    {
        ["pid"] = Pid,
        ["port"] = Port,
        ["blenderVersion"] = BlenderVersion,
        ["background"] = Background,
        ["filepath"] = Filepath,
        ["startedAt"] = DateTimeOffset.FromUnixTimeMilliseconds((long)(StartedAt * 1000)).ToString("O"),
    };
}
