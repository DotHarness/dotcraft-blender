using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotCraft.Blender.Attach;

/// <summary>The wire is strictly request/response, so calls are serialized.</summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private const int HeaderSize = 4;
    private const int MaxFrame = 64 * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _counter;

    private BridgeClient(TcpClient tcp, BridgeDescriptor descriptor)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        Descriptor = descriptor;
    }

    public BridgeDescriptor Descriptor { get; }

    public JsonObject Identity { get; private set; } = new();

    public bool IsConnected => _tcp.Connected;

    public static async Task<BridgeClient> ConnectAsync(
        BridgeDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1", descriptor.Port, cancellationToken);
        }
        catch (SocketException error)
        {
            tcp.Dispose();
            throw new BlenderTargetException(
                "BridgeUnreachable",
                $"Blender {descriptor.Pid} published port {descriptor.Port} but refused the connection: {error.Message}");
        }

        var client = new BridgeClient(tcp, descriptor);
        var hello = new JsonObject
        {
            ["id"] = "0",
            ["type"] = "hello",
            ["token"] = descriptor.Token,
        };

        var response = await client.SendFrameAsync(hello, cancellationToken);
        if (response["ok"]?.GetValue<bool>() != true)
        {
            await client.DisposeAsync();
            throw new BlenderTargetException(
                response["error"]?["code"]?.GetValue<string>() ?? "HandshakeFailed",
                response["error"]?["message"]?.GetValue<string>() ?? "The bridge rejected the handshake.");
        }

        client.Identity = response["result"]?.AsObject() ?? new JsonObject();
        return client;
    }

    public async Task<JsonObject> CallAsync(
        string command,
        JsonObject? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _counter).ToString(),
            ["type"] = command,
            ["params"] = parameters ?? new JsonObject(),
        };
        return await SendFrameAsync(request, cancellationToken);
    }

    private async Task<JsonObject> SendFrameAsync(JsonObject request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var body = Encoding.UTF8.GetBytes(request.ToJsonString());
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await _stream.WriteAsync(header, cancellationToken);
            await _stream.WriteAsync(body, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
            return await ReadFrameAsync(cancellationToken);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            throw new BlenderTargetException(
                "BridgeLost",
                $"The connection to Blender {Descriptor.Pid} dropped: {error.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await _stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrame)
            throw new BlenderTargetException("FrameTooLarge", $"Blender sent a {length} byte frame.");

        var body = new byte[length];
        await _stream.ReadExactlyAsync(body, cancellationToken);
        return JsonNode.Parse(body)?.AsObject()
               ?? throw new BlenderTargetException("ProtocolViolation", "Blender sent a non-object frame.");
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    /// <summary>Unwraps an envelope into a result, turning a bridge error into an exception.</summary>
    public static JsonNode? Unwrap(JsonObject envelope)
    {
        if (envelope["ok"]?.GetValue<bool>() == true)
            return envelope["result"];

        var code = envelope["error"]?["code"]?.GetValue<string>() ?? "BridgeFailure";
        var message = envelope["error"]?["message"]?.GetValue<string>() ?? "Blender reported a failure.";
        throw new BlenderTargetException(code, message, envelope["result"]?.DeepClone());
    }

    public static string Describe(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
