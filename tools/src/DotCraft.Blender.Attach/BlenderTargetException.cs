using System.Text.Json.Nodes;

namespace DotCraft.Blender.Attach;

public sealed class BlenderTargetException(string code, string message, JsonNode? partial = null)
    : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>Whatever the bridge produced before failing, such as stdout from a failed script.</summary>
    public JsonNode? Partial { get; } = partial;
}
