namespace AIUsageDock.Providers;

internal static class ExecutableResolver
{
    public static string? ResolveCodex()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Resolve(
            "codex",
            Path.Combine(appData, "npm", "codex.cmd"),
            Path.Combine(home, ".local", "bin", "codex.exe"),
            Path.Combine(home, "AppData", "Local", "Programs", "codex", "codex.exe"));
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
