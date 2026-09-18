using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotCraft.Blender.Attach;

/// <summary>A null descriptor means the session is still coming up.</summary>
public sealed record LaunchOutcome(int Pid, BridgeDescriptor? Descriptor, string Output);

public sealed class BlenderLauncher(string bridgeRoot)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);

    /// <summary>Installed Blender executables, newest version first.</summary>
    public static IReadOnlyList<string> Installations()
    {
        var found = new List<string>();

        var configured = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            found.Add(configured);

        foreach (var directory in SearchRoots())
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var candidate in Directory.EnumerateDirectories(directory, "Blender*"))
            {
                var executable = Path.Combine(candidate, ExecutableName);
                if (File.Exists(executable))
                    found.Add(executable);
            }

            var direct = Path.Combine(directory, ExecutableName);
            if (File.Exists(direct))
                found.Add(direct);
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var executable = Path.Combine(entry.Trim(), ExecutableName);
            if (File.Exists(executable))
                found.Add(executable);
        }

        return found
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(VersionOf)
            .ToList();
    }

    public async Task<LaunchOutcome> LaunchAsync(
        string? executable = null,
        string? blendFile = null,
        TimeSpan? wait = null,
        CancellationToken cancellationToken = default)
    {
        executable ??= Installations().FirstOrDefault()
                       ?? throw new BlenderTargetException(
                           "BlenderNotFound",
                           "No Blender installation was found. Set BLENDER_PATH to the executable.");

        var bootstrap = Path.Combine(bridgeRoot, "bootstrap.py");
        if (!File.Exists(bootstrap))
            throw new BlenderTargetException("BridgeMissing", $"No bootstrap script at {bootstrap}.");

        // Blender must not hold this process's handles: inside DotCraft they include the AppServer's
        // JSON-RPC stdio channel. Redirection alone is not enough on Windows, where Process.Start
        // passes bInheritHandles=TRUE without a handle list, so ShellExecuteEx is used there and the
        // bridge log replaces the output lost with it.
        //
        // blender.exe is a console program, so Windows also goes through blender-launcher.exe, which
        // starts it windowless and then exits. The started process is therefore not Blender itself,
        // and the session is identified by the descriptor and the process that appeared with it.
        var launcher = ResolveLauncher(executable);
        var shellExecute = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(launcher ?? executable)
        {
            UseShellExecute = shellExecute,
            RedirectStandardInput = !shellExecute,
            RedirectStandardOutput = !shellExecute,
            RedirectStandardError = !shellExecute,
            CreateNoWindow = !shellExecute,
        };
        if (!string.IsNullOrWhiteSpace(blendFile))
            start.ArgumentList.Add(Path.GetFullPath(blendFile));
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(bootstrap);

        var knownSessions = BridgeDiscovery.List().Select(d => d.Pid).ToHashSet();
        var knownProcesses = RunningBlenderIds();
        var process = Process.Start(start)
                      ?? throw new BlenderTargetException("LaunchFailed", $"Could not start {executable}.");

        var transcript = new Transcript();
        if (!shellExecute)
        {
            process.StandardInput.Close();
            _ = transcript.DrainAsync(process.StandardOutput);
            _ = transcript.DrainAsync(process.StandardError);
        }

        var pid = launcher is null ? process.Id : 0;
        var deadline = DateTime.UtcNow + (wait ?? DefaultWait);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (launcher is null && process.HasExited)
                throw new BlenderTargetException(
                    "LaunchFailed",
                    $"Blender exited with code {process.ExitCode} before the bridge started.\n{Diagnostics(pid, transcript)}");

            var descriptor = BridgeDiscovery.List().FirstOrDefault(d => !knownSessions.Contains(d.Pid));
            if (descriptor is not null)
                return new LaunchOutcome(descriptor.Pid, descriptor, Diagnostics(descriptor.Pid, transcript));

            if (pid == 0)
                pid = RunningBlenderIds().Except(knownProcesses).FirstOrDefault();

            await Task.Delay(200, cancellationToken);
        }

        return new LaunchOutcome(pid, null, Diagnostics(pid, transcript));
    }

    /// <summary>The windowless launcher beside a Windows blender.exe, when there is one.</summary>
    private static string? ResolveLauncher(string executable)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var launcher = Path.Combine(
            Path.GetDirectoryName(executable) ?? string.Empty,
            "blender-launcher.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    private static HashSet<int> RunningBlenderIds() =>
        Process.GetProcessesByName("blender").Select(p => p.Id).ToHashSet();

    private static string Diagnostics(int pid, Transcript transcript)
    {
        var captured = transcript.ToString();
        return captured.Length > 0 ? captured : BridgeDiscovery.ReadLog(pid);
    }

    private sealed class Transcript
    {
        private const int MaxLines = 40;
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public async Task DrainAsync(StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    lock (_gate)
                    {
                        _lines.Enqueue(line);
                        while (_lines.Count > MaxLines)
                            _lines.Dequeue();
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        }

        public override string ToString()
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }
    }

    private static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "blender.exe" : "blender";

    private static IEnumerable<string> SearchRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
            {
                var root = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "Blender Foundation");
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Blender.app/Contents/MacOS";
            yield break;
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/snap/blender/current";
    }

    private static Version VersionOf(string executable)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(executable)) ?? string.Empty;
        var digits = new string(name.Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var version)
            ? version
            : new Version(0, 0);
    }
}
