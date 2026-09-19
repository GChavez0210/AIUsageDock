using System.Diagnostics;
using System.Text;

namespace AIUsageDock.Providers;

internal static class ProcessHelpers
{
    public static ProcessStartInfo CreateStartInfo(string executable, params string[] arguments) =>
        CreateStartInfo(executable, arguments, environment: null);

    /// <param name="environment">
    /// Variables to set for the child on top of the inherited environment. Providers use
    /// this to switch off CLI side effects that have nothing to do with reading usage,
    /// such as background self-updaters.
    /// </param>
    public static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        ProcessStartInfo startInfo;
        var extension = Path.GetExtension(executable);

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe is a console subsystem process, so Windows gives it a console even
            // under CreateNoWindow and the terminal host can flash a window around it.
            // An npm launcher only exists to find node.exe and a script, so do that here
            // and skip the shell entirely.
            if (NodeShimResolver.TryExpand(executable, out var interpreter, out var leadingArguments))
            {
                startInfo = NewBase(interpreter);
                foreach (var argument in leadingArguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }
            else
            {
                startInfo = NewBase(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe");
                // ArgumentList escapes embedded quotes for CreateProcess. cmd.exe needs the
                // command following /c as one raw, outer-quoted string instead.
                startInfo.Arguments = $"/d /s /c \"{BuildCmdCommand(executable, arguments)}\"";
            }
        }
        else
        {
            startInfo = NewBase(executable);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.WorkingDirectory = Path.GetTempPath();
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, environment) };
        process.StartInfo.RedirectStandardInput = false;
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static ProcessStartInfo NewBase(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static string BuildCmdCommand(string executable, IEnumerable<string> arguments)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        return string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    }
}
