using System.Diagnostics;
using System.Text.Json;

namespace DotCraft.Blender.Attach;

public static class BridgeDiscovery
{
    public static string Directory => Path.Combine(StateRoot(), "DotCraft", "blender-bridge");

    /// <summary>Live sessions, newest first. Descriptors whose process is gone are removed.</summary>
    public static IReadOnlyList<BridgeDescriptor> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];

        var found = new List<BridgeDescriptor>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "bridge-*.json"))
        {
            var descriptor = TryRead(file);
            if (descriptor is null)
            {
                Delete(file);
                continue;
            }

            if (!IsAlive(descriptor.Pid))
            {
                Delete(file);
                continue;
            }

            found.Add(descriptor);
        }

        return found.OrderByDescending(d => d.StartedAt).ToList();
    }

    public static BridgeDescriptor? Find(int pid) => List().FirstOrDefault(d => d.Pid == pid);

    public static BridgeDescriptor? Newest() => List().FirstOrDefault();

    /// <summary>The tail of a session's own log, which is what explains a failed or slow start.</summary>
    public static string ReadLog(int pid, int maxLines = 20)
    {
        var path = Path.Combine(Directory, $"bridge-{pid}.log");
        try
        {
            return File.Exists(path)
                ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(maxLines))
                : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static BridgeDescriptor? TryRead(string file)
    {
        try
        {
            var descriptor = JsonSerializer.Deserialize<BridgeDescriptor>(File.ReadAllText(file));
            return descriptor is null || descriptor.Port <= 0 || descriptor.Pid <= 0
                ? null
                : descriptor with { Path = file };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
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

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string StateRoot() =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "state");
}
