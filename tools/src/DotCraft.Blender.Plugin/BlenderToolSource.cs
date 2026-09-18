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
    [Description("List Blender sessions listening for DotCraft, and the installations on this machine.")]
    public ValueTask<ToolExecutionResult> List(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(new JsonObject
        {
            ["sessions"] = service.List(),
            ["installations"] = new JsonArray(service.Installations().Select(p => (JsonNode)p!).ToArray()),
        }));

    [GeneratedTool(Name = "connect")]
    [Description("Connect this task to a Blender session by pid, or omit pid to take the newest one.")]
    public ValueTask<ToolExecutionResult> Connect(
        ToolInvocationContext context,
        [Range(1, int.MaxValue)]
        [Description("Process id from blender.list; omit for the newest session.")] int? pid = null,
        [Description("Start Blender when no session is listening.")] bool launch = false,
        [Description("A .blend to open when launching.")] string? blendFile = null,
        [Range(1, 120)]
        [Description("Seconds to wait before reporting the session as still starting.")] int waitSeconds = 20,
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
    [Description("Read the connected session's file, scene, mode, selection and running jobs.")]
    public ValueTask<ToolExecutionResult> Status(
        ToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        Guard(() => service.CallAsync(context.ThreadId, "status", null, cancellationToken));

    [GeneratedTool(Name = "scene")]
    [Description("Read the scene graph, or one object in detail when a name is given.")]
    public ValueTask<ToolExecutionResult> Scene(
        ToolInvocationContext context,
        [Description("An object name; omit for the whole scene.")] string? name = null,
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
    [Description("Execute Python on the connected Blender's main thread. `bpy` and the `dc` helpers are in scope.")]
    public ValueTask<ToolExecutionResult> Execute(
        ToolInvocationContext context,
        [Description("Python source, run as a module body.")] string code,
        CancellationToken cancellationToken = default) =>
        planMode
            ? ValueTask.FromResult(ModeDenied())
            : Guard(() => service.CallAsync(
                context.ThreadId,
                "execute",
                new JsonObject { ["code"] = code },
                cancellationToken));

    [GeneratedTool(Name = "view")]
    [Description("Return an image of the viewport, or of a rendered file when a path is given.")]
    public ValueTask<ToolExecutionResult> View(
        ToolInvocationContext context,
        [Description("An image file to read back; omit it to capture the viewport.")] string? filepath = null,
        [Range(64, 2048)]
        [Description("Longest edge of the returned image.")] int maxSize = 800,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject { ["maxSize"] = maxSize };
        if (filepath is not null)
            parameters["filepath"] = filepath;
        return GuardImage(() => service.CallAsync(
            context.ThreadId,
            filepath is null ? "view" : "image",
            parameters,
            cancellationToken));
    }

    [GeneratedTool(Name = "render")]
    [Description("Start a render and return a job id; it does not block the session.")]
    public ValueTask<ToolExecutionResult> Render(
        ToolInvocationContext context,
        [Description("Render engine identifier; omit to keep the scene's engine.")] string? engine = null,
        [Range(16, 16384)] [Description("Output width in pixels.")] int? width = null,
        [Range(16, 16384)] [Description("Output height in pixels.")] int? height = null,
        [Description("Frame to render; omit for the current frame.")] int? frame = null,
        [Description("Output path; its extension picks the format.")] string? filepath = null,
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
    [Description("Disconnect this task from its Blender session; it does not close Blender.")]
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
