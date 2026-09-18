using DotCraft.Blender.Attach;
using DotCraft.Plugins;
using DotCraft.Tools;

namespace DotCraft.Blender;

public sealed class Plugin : IDotCraftPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
    {
        var bridgeRoot = Path.Combine(context.ContentRoot, "bridge");
        if (!File.Exists(Path.Combine(bridgeRoot, "bootstrap.py")))
            throw new InvalidOperationException($"The plugin bundle has no bridge at {bridgeRoot}.");

        var service = new BlenderAttachService(bridgeRoot);
        context.Lifetime.OwnAsync(service);
        context.Contributions.Add<IToolSource>(new BlenderToolSource(service));
        return ValueTask.CompletedTask;
    }
}
