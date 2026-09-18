using System.Text.Json.Nodes;
using DotCraft.Blender.Attach;

namespace DotCraft.Blender.Cli;

internal static class Program
{
    private const string Thread = "cli";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var bridgeRoot = Environment.GetEnvironmentVariable("DOTCRAFT_BLENDER_BRIDGE")
                         ?? FindBridgeRoot();
        await using var service = new BlenderAttachService(bridgeRoot);

        try
        {
            return await RunAsync(service, args);
        }
        catch (BlenderTargetException error)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            if (error.Partial is not null)
                Console.Error.WriteLine(BridgeClient.Describe(error.Partial));
            return 1;
        }
    }

    private static async Task<int> RunAsync(BlenderAttachService service, string[] args)
    {
        var command = args[0];
        var rest = args.Skip(1).ToArray();

        switch (command)
        {
            case "installs":
                foreach (var path in service.Installations())
                    Console.WriteLine(path);
                return 0;

            case "list":
                Print(service.List());
                return 0;

            case "connect":
            {
                var pid = TryOption(rest, "--pid", out var raw) && int.TryParse(raw, out var value)
                    ? value
                    : (int?)null;
                var launch = rest.Contains("--launch");
                TryOption(rest, "--file", out var file);
                var wait = TryOption(rest, "--wait", out var seconds) && int.TryParse(seconds, out var value2)
                    ? TimeSpan.FromSeconds(value2)
                    : (TimeSpan?)null;
                Print(await service.ConnectAsync(Thread, pid, launch, file, wait, CancellationToken.None));
                return 0;
            }

            case "disconnect":
                Print(await service.DisconnectAsync(Thread));
                return 0;

            case "exec":
            {
                var code = rest.FirstOrDefault() ?? string.Empty;
                if (code.StartsWith('@'))
                    code = await File.ReadAllTextAsync(code[1..]);
                Print(await Call(service, "execute", new JsonObject { ["code"] = code }));
                return 0;
            }

            case "object":
                Print(await Call(service, "object", new JsonObject { ["name"] = rest.FirstOrDefault() }));
                return 0;

            case "view":
            case "image":
            {
                var parameters = new JsonObject { ["maxSize"] = Size(rest) };
                if (command == "image" && TryOption(rest, "--file", out var source))
                    parameters["filepath"] = source;
                var result = await Call(service, command, parameters);
                if (TryOption(rest, "--save", out var target) && target is not null)
                {
                    await File.WriteAllBytesAsync(
                        target,
                        Convert.FromBase64String(result["base64"]!.GetValue<string>()));
                    result.Remove("base64");
                    result["savedTo"] = Path.GetFullPath(target);
                }

                Print(result);
                return 0;
            }

            case "render":
            {
                var parameters = new JsonObject();
                if (TryOption(rest, "--engine", out var engine)) parameters["engine"] = engine;
                if (TryOption(rest, "--width", out var width) && TryOption(rest, "--height", out var height))
                    parameters["resolution"] = new JsonArray(int.Parse(width!), int.Parse(height!));
                var job = await Call(service, "render", parameters);
                Print(job);
                if (!rest.Contains("--wait") || job["state"]?.GetValue<string>() != "running")
                    return 0;

                var id = job["jobId"]!.GetValue<string>();
                for (var attempt = 0; attempt < 600; attempt++)
                {
                    await Task.Delay(250);
                    var snapshot = await Call(service, "job", new JsonObject { ["jobId"] = id });
                    if (snapshot["state"]?.GetValue<string>() == "running")
                        continue;
                    Print(snapshot);
                    return 0;
                }

                Console.Error.WriteLine("render did not finish");
                return 1;
            }

            case "job":
            {
                var parameters = new JsonObject();
                if (rest.FirstOrDefault() is { } id && !id.StartsWith("--")) parameters["jobId"] = id;
                if (rest.Contains("--terminate")) parameters["terminate"] = true;
                Print(await Call(service, "job", parameters));
                return 0;
            }

            case "status":
            case "scene":
            case "ping":
            case "documentation":
                Print(await Call(service, command, null));
                return 0;

            default:
                Usage();
                return 2;
        }
    }

    private static Task<JsonObject> Call(BlenderAttachService service, string command, JsonObject? parameters) =>
        service.CallAsync(Thread, command, parameters, CancellationToken.None);

    private static int Size(string[] args) =>
        TryOption(args, "--max", out var raw) && int.TryParse(raw, out var value) ? value : 800;

    private static bool TryOption(string[] args, string name, out string? value)
    {
        var index = Array.IndexOf(args, name);
        value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        return value is not null;
    }

    private static void Print(JsonNode? node) => Console.WriteLine(BridgeClient.Describe(node));

    private static string FindBridgeRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "bridge");
            if (File.Exists(Path.Combine(candidate, "bootstrap.py")))
                return candidate;
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(AppContext.BaseDirectory, "bridge");
    }

    private static void Usage() => Console.Error.WriteLine(
        """
        dcblender <command> [options]

          installs                          List Blender executables found on this machine
          list                              List listening Blender sessions
          connect [--pid N] [--launch] [--file x.blend] [--wait SECONDS]
          disconnect
          ping | status | scene | documentation
          object <name>
          exec "<python>" | exec @script.py
          view [--max N] [--save out.png]
          image --file <path> [--max N] [--save out.png]
          render [--engine E] [--width W --height H] [--wait]
          job [<id>] [--terminate]
        """);
}
