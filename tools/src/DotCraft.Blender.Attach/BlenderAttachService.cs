using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DotCraft.Blender.Attach;

/// <summary>
/// A dropped socket reconnects transparently while the session is alive; a dead session surfaces
/// instead of being silently replaced.
/// </summary>
public sealed class BlenderAttachService(string bridgeRoot) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly BlenderLauncher _launcher = new(bridgeRoot);

    private sealed class Binding
    {
        public int Pid;
        public int PendingPid;
        public BridgeClient? Client;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    public JsonArray List() => new(BridgeDiscovery.List().Select(d => (JsonNode)d.ToSummary()).ToArray());

    public IReadOnlyList<string> Installations() => BlenderLauncher.Installations();

    /// <summary>
    /// A session that has not finished starting is reported as <c>starting</c>; calling again
    /// attaches to that same process rather than starting another.
    /// </summary>
    public async Task<JsonObject> ConnectAsync(
        string threadId,
        int? pid,
        bool launch,
        string? blendFile,
        TimeSpan? wait,
        CancellationToken cancellationToken)
    {
        var binding = _bindings.GetOrAdd(threadId, _ => new Binding());
        await binding.Gate.WaitAsync(cancellationToken);
        try
        {
            await ResetAsync(binding);

            var descriptor = Resolve(pid, binding.Pid, binding.PendingPid);
            if (descriptor is null && pid is null)
            {
                if (IsAlive(binding.PendingPid))
                    descriptor = await AwaitPendingAsync(binding.PendingPid, wait, cancellationToken);
                else if (launch)
                    return await StartAsync(binding, blendFile, wait, cancellationToken);
            }

            if (descriptor is null)
                throw new BlenderTargetException(
                    "NoBlenderSession",
                    pid is null
                        ? "No Blender session is listening. Start Blender with the DotCraft Bridge add-on, or connect with launch enabled."
                        : $"No listening Blender session with pid {pid}.");

            return await AttachAsync(binding, descriptor, cancellationToken);
        }
        finally
        {
            binding.Gate.Release();
        }
    }

    public async Task<JsonObject> CallAsync(
        string threadId,
        string command,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var binding = _bindings.GetOrAdd(threadId, _ => new Binding());
        await binding.Gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureClientAsync(binding, cancellationToken);
            var envelope = await client.CallAsync(command, parameters, cancellationToken);
            return BridgeClient.Unwrap(envelope)?.AsObject() ?? new JsonObject();
        }
        finally
        {
            binding.Gate.Release();
        }
    }

    public async Task<JsonObject> DisconnectAsync(string threadId)
    {
        if (!_bindings.TryRemove(threadId, out var binding))
            return new JsonObject { ["state"] = "notConnected" };

        var pid = binding.Pid;
        await ResetAsync(binding);
        binding.Gate.Dispose();
        return new JsonObject { ["state"] = "disconnected", ["pid"] = pid };
    }

    private async Task<JsonObject> StartAsync(
        Binding binding,
        string? blendFile,
        TimeSpan? wait,
        CancellationToken cancellationToken)
    {
        var outcome = await _launcher.LaunchAsync(null, blendFile, wait, cancellationToken);
        if (outcome.Descriptor is null)
        {
            binding.PendingPid = outcome.Pid;
            return new JsonObject
            {
                ["state"] = "starting",
                ["pid"] = outcome.Pid,
                ["message"] = "Blender is still starting. Call blender.connect again to attach; it will reuse this process.",
                ["output"] = outcome.Output,
            };
        }

        binding.PendingPid = 0;
        return await AttachAsync(binding, outcome.Descriptor, cancellationToken);
    }

    private static async Task<BridgeDescriptor?> AwaitPendingAsync(
        int pendingPid,
        TimeSpan? wait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (wait ?? BlenderLauncher.DefaultWait);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BridgeDiscovery.Find(pendingPid) is { } descriptor)
                return descriptor;
            if (!IsAlive(pendingPid))
                return null;
            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    private async Task<JsonObject> AttachAsync(
        Binding binding,
        BridgeDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        binding.Client = await BridgeClient.ConnectAsync(descriptor, cancellationToken);
        binding.Pid = descriptor.Pid;
        binding.PendingPid = 0;
        return new JsonObject
        {
            ["state"] = "connected",
            ["session"] = descriptor.ToSummary(),
            ["identity"] = binding.Client.Identity.DeepClone(),
        };
    }

    private async Task<BridgeClient> EnsureClientAsync(Binding binding, CancellationToken cancellationToken)
    {
        if (binding.Client is { IsConnected: true } live)
            return live;

        await ResetAsync(binding);
        var descriptor = Resolve(null, binding.Pid, binding.PendingPid)
                         ?? throw new BlenderTargetException(
                             "NotConnected",
                             Unavailable(binding));

        binding.Client = await BridgeClient.ConnectAsync(descriptor, cancellationToken);
        binding.Pid = descriptor.Pid;
        binding.PendingPid = 0;
        return binding.Client;
    }

    private static string Unavailable(Binding binding)
    {
        if (IsAlive(binding.PendingPid))
            return $"Blender {binding.PendingPid} is still starting. Call blender.connect again once it is up.";
        return binding.Pid == 0
            ? "This task is not connected to a Blender session. Call blender.connect first."
            : $"Blender {binding.Pid} is no longer listening. Reconnect to pick a session.";
    }

    private static BridgeDescriptor? Resolve(int? requested, int remembered, int pending)
    {
        if (requested is { } pid)
            return BridgeDiscovery.Find(pid);
        if (remembered != 0 && BridgeDiscovery.Find(remembered) is { } known)
            return known;
        if (pending != 0 && BridgeDiscovery.Find(pending) is { } started)
            return started;
        return remembered == 0 && pending == 0 ? BridgeDiscovery.Newest() : null;
    }

    private static bool IsAlive(int pid)
    {
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task ResetAsync(Binding binding)
    {
        if (binding.Client is null)
            return;
        await binding.Client.DisposeAsync();
        binding.Client = null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var binding in _bindings.Values)
            await ResetAsync(binding);
        _bindings.Clear();
    }
}
