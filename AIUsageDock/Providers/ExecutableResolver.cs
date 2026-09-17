using System.Runtime.InteropServices;

namespace AIUsageDock.Providers;

internal static class ExecutableResolver
{
    public static string? ResolveCodex()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        return ResolveCodex(pathValue, appData, home, RuntimeInformation.OSArchitecture);
    }

    public static string? ResolveCodexShim()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return ResolveCodexShim(pathValue, appData);
    }

    internal static string? ResolveCodexShim(string pathValue, string appData)
    {
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in new[] { ".cmd", ".bat" })
            {
                var command = Path.Combine(directory, "codex" + extension);
                if (File.Exists(command))
                {
                    return command;
                }
            }
        }

        var npmShim = Path.Combine(appData, "npm", "codex.cmd");
        return File.Exists(npmShim) ? npmShim : null;
    }

    internal static string? ResolveCodex(string pathValue, string appData, string home, Architecture architecture)
    {
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var executable = Path.Combine(directory, "codex.exe");
            if (File.Exists(executable))
            {
                return executable;
            }

            var command = Path.Combine(directory, "codex.cmd");
            if (File.Exists(command))
            {
                var native = ResolveNpmCodex(directory, architecture);
                if (native is not null)
                {
                    return native;
                }
            }
        }

        var npmDirectory = Path.Combine(appData, "npm");
        var npmNative = ResolveNpmCodex(npmDirectory, architecture);
        if (npmNative is not null)
        {
            return npmNative;
        }

        return new[]
        {
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(home, "AppData", "Local", "Programs", "codex", "codex.exe"),
        }.FirstOrDefault(File.Exists)
            ?? ResolveOnPath("codex", pathValue, Path.Combine(npmDirectory, "codex.cmd"));
    }

    private static string? ResolveNpmCodex(string npmDirectory, Architecture architecture)
    {
        var (packageSuffix, target) = architecture switch
        {
            Architecture.X64 => ("x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("arm64", "aarch64-pc-windows-msvc"),
            _ => (null, null),
        };
        if (packageSuffix is null || target is null)
        {
            return null;
        }

        var codexPackage = Path.Combine(npmDirectory, "node_modules", "@openai", "codex");
        var platformPackage = "codex-win32-" + packageSuffix;
        var relativeBinary = Path.Combine("vendor", target, "bin", "codex.exe");
        return new[]
        {
            Path.Combine(codexPackage, "node_modules", "@openai", platformPackage, relativeBinary),
            Path.Combine(npmDirectory, "node_modules", "@openai", platformPackage, relativeBinary),
            Path.Combine(codexPackage, relativeBinary),
        }.FirstOrDefault(File.Exists);
    }

    public static string? ResolveClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Resolve(
            "claude",
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, "AppData", "Local", "Programs", "claude", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.exe"));
    }

    public static string? ResolveAntigravity()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Resolve(
            "agy",
            Path.Combine(home, "AppData", "Local", "agy", "bin", "agy.exe"),
            Path.Combine(home, ".local", "bin", "agy.exe"));
    }

    private static string? Resolve(string command, params string[] candidates)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return ResolveOnPath(command, pathValue, candidates);
    }

    private static string? ResolveOnPath(string command, string pathValue, params string[] candidates)
    {
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", ".com", string.Empty }
            : new[] { string.Empty };

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var path = Path.Combine(directory, command + extension);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
