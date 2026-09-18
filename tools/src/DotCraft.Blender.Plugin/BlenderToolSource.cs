using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCraft.Blender.Attach;
using DotCraft.Tools;
using Microsoft.Extensions.AI;

namespace DotCraft.Blender;

internal sealed class BlenderToolSource(BlenderAttachService service) : AIFunctionToolSource
{
    public override string SourceId => "DotCraft.Blender";

    protected override IEnumerable<AIFunction> CreateFunctions(ToolPlanningContext context)
    {
        var tools = new BlenderTools(service, IsPlanMode(context));
        return
        [
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_List(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Connect(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Status(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Scene(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Execute(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_View(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Render(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Job(tools),
            GeneratedTools.Blender.GeneratedToolFunctions.BlenderTools_Disconnect(tools),
        ];
    }

    protected override string? GetNamespace(AIFunction function, ToolPlanningContext context) => "blender";

    protected override ToolPolicyHints GetPolicyHints(AIFunction function, ToolPlanningContext context) => new(
        RequiresApproval: function.Name is "connect" or "execute" or "render" or "disconnect",
        ReadOnly: function.Name is "list" or "status" or "scene" or "view" or "job");

    private static bool IsPlanMode(ToolPlanningContext context) =>
        context.Mode.Equals("plan", StringComparison.OrdinalIgnoreCase);
}

internal sealed class BlenderTools(BlenderAttachService service, bool planMode)
{
    [GeneratedTool(Name = "list")]
    [Description("List Blender sessions that are listening for DotCraft, plus the Blender installations on this machine.")]
    public ValueTask<ToolExecutionResult> List(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(new JsonObject
        {
            ["sessions"] = service.List(),
            ["installations"] = new JsonArray(service.Installations().Select(p => (JsonNode)p!).ToArray()),
        }));

    [GeneratedTool(Name = "connect")]
    [Description("Bind this task to a Blender session. Omit pid to take the newest one; set launch to start Blender when none is listening. A session that is still starting comes back as state 'starting' — call this again to attach to that same process.")]
    public ValueTask<ToolExecutionResult> Connect(
        ToolInvocationContext context,
        [Range(1, int.MaxValue)]
        [Description("Process id from blender.list. Omit it to take the newest listening session.")] int? pid = null,
        [Description("Start Blender when no session is listening.")] bool launch = false,
        [Description("A .blend to open when launching. Omit it for an empty scene.")] string? blendFile = null,
        [Range(1, 120)]
        [Description("Seconds to wait for a launching session before reporting it as still starting.")] int waitSeconds = 20,
        CancellationToken cancellationToken = default) =>
        planMode
            ? ValueTask.FromResult(ModeDenied())
            : Guard(() => service.ConnectAsync(
                context.ThreadId,
                pid,
                launch,
                blendFile,
                TimeSpan.FromSeconds(waitSeconds),
                cancellationToken));

    [GeneratedTool(Name = "status")]
    [Description("Read the connected session: file, scene, mode, selection, data counts, and running jobs.")]
    public ValueTask<ToolExecutionResult> Status(
        ToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        Guard(() => service.CallAsync(context.ThreadId, "status", null, cancellationToken));

    [GeneratedTool(Name = "scene")]
    [Description("Read the scene graph, or one object in detail when a name is given.")]
    public ValueTask<ToolExecutionResult> Scene(
        ToolInvocationContext context,
        [Description("An object name. Omit it for the whole scene.")] string? name = null,
        [Description("Include collections, materials and world state.")] bool full = false,
        CancellationToken cancellationToken = default) =>
        Guard(() => name is null
            ? service.CallAsync(
                context.ThreadId,
                "scene",
                new JsonObject { ["detail"] = full ? "full" : "summary" },
                cancellationToken)
            : service.CallAsync(
                context.ThreadId,
                "object",
                new JsonObject { ["name"] = name },
                cancellationToken));

    [GeneratedTool(Name = "execute")]
    [Description("Run Python inside the connected Blender on its main thread. `bpy` and the `dc` helpers are in scope; call dc.api(path) to read the running version's real signature instead of guessing, and dc.result(value) to return structured data. Long renders belong in blender.render, not here.")]
    public ValueTask<ToolExecutionResult> Execute(
        ToolInvocationContext context,
        [Description("Python source. It runs as a module body, so top-level statements are fine and `return` is not.")] string code,
        CancellationToken cancellationToken = default) =>
        planMode
            ? ValueTask.FromResult(ModeDenied())
            : Guard(() => service.CallAsync(
                context.ThreadId,
                "execute",
                new JsonObject { ["code"] = code },
                cancellationToken));

    [GeneratedTool(Name = "view")]
    [Description("Capture the connected session's 3D viewport as an image. Use it before and after changes; a text dump of the scene does not show what the render will look like.")]
    public ValueTask<ToolExecutionResult> View(
        ToolInvocationContext context,
        [Range(64, 2048)]
        [Description("Longest edge of the returned image.")] int maxSize = 800,
        CancellationToken cancellationToken = default) =>
        GuardImage(() => service.CallAsync(
            context.ThreadId,
            "view",
            new JsonObject { ["maxSize"] = maxSize },
            cancellationToken));

    [GeneratedTool(Name = "render")]
    [Description("Start a render and return a job id. The render runs through Blender's own modal path, so this call does not block the session; poll it with blender.job.")]
    public ValueTask<ToolExecutionResult> Render(
        ToolInvocationContext context,
        [Description("Render engine identifier. Omit it to keep the scene's engine; blender.scene reports the current one and an invalid value lists what this Blender supports.")] string? engine = null,
        [Range(16, 16384)] [Description("Output width in pixels.")] int? width = null,
        [Range(16, 16384)] [Description("Output height in pixels.")] int? height = null,
        [Description("Frame to render. Omit it for the current frame.")] int? frame = null,
        [Description("Output path. Omit it for a temporary file that blender.job reports back.")] string? filepath = null,
        CancellationToken cancellationToken = default)
    {
        if (planMode) return ValueTask.FromResult(ModeDenied());
        if (width.HasValue != height.HasValue)
            return ValueTask.FromResult(Failure(ToolErrorCodes.InputInvalid, "Specify both width and height, or neither."));

        var parameters = new JsonObject();
        if (engine is not null) parameters["engine"] = engine;
        if (frame is not null) parameters["frame"] = frame;
        if (filepath is not null) parameters["filepath"] = filepath;
        if (width is not null) parameters["resolution"] = new JsonArray(width.Value, height!.Value);
        return Guard(() => service.CallAsync(context.ThreadId, "render", parameters, cancellationToken));
    }

    [GeneratedTool(Name = "job")]
    [Description("Inspect or cancel a background job such as a render. Omit the id to list jobs.")]
    public ValueTask<ToolExecutionResult> Job(
        ToolInvocationContext context,
        [Description("Job id returned by blender.render.")] string? jobId = null,
        [Description("Request cancellation before reporting the state.")] bool terminate = false,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject();
        if (jobId is not null) parameters["jobId"] = jobId;
        if (terminate) parameters["terminate"] = true;
        return Guard(() => service.CallAsync(context.ThreadId, "job", parameters, cancellationToken));
    }

    [GeneratedTool(Name = "disconnect")]
    [Description("Release this task's Blender session. It does not close Blender.")]
    public ValueTask<ToolExecutionResult> Disconnect(
        ToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        Guard(() => service.DisconnectAsync(context.ThreadId));


    private static async ValueTask<ToolExecutionResult> Guard(Func<Task<JsonObject>> operation)
    {
        try
        {
            var result = await operation();
            return ToolExecutionResult.Succeeded(
                result.ToJsonString(),
                JsonSerializer.SerializeToElement(result));
        }
        catch (Exception error)
        {
            return Translate(error);
        }
    }

    /// <summary>Lifts a base64 payload into real image content.</summary>
    private static async ValueTask<ToolExecutionResult> GuardImage(Func<Task<JsonObject>> operation)
    {
        try
        {
            var result = await operation();
            var encoded = result["base64"]?.GetValue<string>();
            if (encoded is null)
                return ToolExecutionResult.Succeeded(result.ToJsonString());

            result.Remove("base64");
            return ToolExecutionResult.Succeeded(
                result.ToJsonString(),
                JsonSerializer.SerializeToElement(result),
                contentItems: [new DataContent(Convert.FromBase64String(encoded), "image/png")]);
        }
        catch (Exception error)
        {
            return Translate(error);
        }
    }

    private static ToolExecutionResult Translate(Exception error) => error switch
    {
        OperationCanceledException => Failure(
            "BlenderOutcomeUnknown",
            "The call stopped waiting. Blender may still be running the work; check blender.status before replaying it."),
        BlenderTargetException target => Failure(
            target.Code,
            target.Partial is null ? target.Message : $"{target.Message}\n{target.Partial.ToJsonString()}"),
        ArgumentException invalid => Failure(ToolErrorCodes.InputInvalid, invalid.Message),
        _ => Failure("BlenderAttachFailed", error.Message),
    };

    private static ToolExecutionResult ModeDenied() =>
        Failure("BlenderModeDenied", "Blender mutations are unavailable in Plan mode.");

    private static ToolExecutionResult Failure(string code, string text) =>
        ToolExecutionResult.Failed(new ToolError(code, text), text);
}
