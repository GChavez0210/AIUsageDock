using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class NodeShimResolverTests
{
    [Fact]
    public void ModernNpmShimResolvesToBundledNode()
    {
        WithTemporaryDirectory(root =>
        {
            var node = CreateFile(root, "node.exe");
            var script = CreateFile(root, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            var shim = CreateShim(root, "claude.cmd", ModernNpmShim("node_modules", "@anthropic-ai", "claude-code", "cli.js"));

            Assert.True(NodeShimResolver.TryExpand(shim, out var interpreter, out var leadingArguments));
            Assert.Equal(node, interpreter);
            Assert.Equal([script], leadingArguments);
        });
    }

    [Fact]
    public void LegacyNpmShimResolvesToBundledNode()
    {
        WithTemporaryDirectory(root =>
        {
            var node = CreateFile(root, "node.exe");
            var script = CreateFile(root, "node_modules", "agy", "bin", "agy.js");
            var shim = CreateShim(root, "agy.cmd", LegacyNpmShim("node_modules", "agy", "bin", "agy.js"));

            Assert.True(NodeShimResolver.TryExpand(shim, out var interpreter, out var leadingArguments));
            Assert.Equal(node, interpreter);
            Assert.Equal([script], leadingArguments);
        });
    }

    [Fact]
    public void ShimKeepsInterpreterAndScriptOptions()
    {
        WithTemporaryDirectory(root =>
        {
            CreateFile(root, "node.exe");
            var script = CreateFile(root, "node_modules", "tool", "cli.js");
            var relative = Relative("node_modules", "tool", "cli.js");
            var shim = CreateShim(root, "tool.cmd",
                "@ECHO off",
                $"\"%dp0%\\node.exe\" --max-old-space-size=4096 \"%dp0%\\{relative}\" --quiet %*");

            Assert.True(NodeShimResolver.TryExpand(shim, out _, out var leadingArguments));
            Assert.Equal(["--max-old-space-size=4096", script, "--quiet"], leadingArguments);
        });
    }

    [Fact]
    public void ShimIsNotExpandedWhenTheScriptIsMissing()
    {
        WithTemporaryDirectory(root =>
        {
            CreateFile(root, "node.exe");
            var shim = CreateShim(root, "claude.cmd", ModernNpmShim("node_modules", "gone", "cli.js"));

            Assert.False(NodeShimResolver.TryExpand(shim, out _, out _));
        });
    }

    [Fact]
    public void ShimIsNotExpandedWhenNodeCannotBeFound()
    {
        WithTemporaryDirectory(root =>
        {
            CreateFile(root, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            var shim = CreateShim(root, "claude.cmd", ModernNpmShim("node_modules", "@anthropic-ai", "claude-code", "cli.js"));

            var found = NodeShimResolver.TryExpand(
                shim,
                File.ReadAllLines,
                File.Exists,
                _ => null,
                out _,
                out _);

            Assert.False(found);
        });
    }

    [Fact]
    public void ExpandedShimLaunchesNodeWithoutAShell()
    {
        WithTemporaryDirectory(root =>
        {
            var node = CreateFile(root, "node.exe");
            var script = CreateFile(root, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            var shim = CreateShim(root, "claude.cmd", ModernNpmShim("node_modules", "@anthropic-ai", "claude-code", "cli.js"));

            var startInfo = ProcessHelpers.CreateStartInfo(shim, "-p", "/usage");

            Assert.Equal(node, startInfo.FileName);
            Assert.Equal([script, "-p", "/usage"], startInfo.ArgumentList);
            Assert.True(startInfo.CreateNoWindow);
            Assert.False(startInfo.UseShellExecute);
        });
    }

    [Fact]
    public void UnrecognisedShimStillFallsBackToTheShell()
    {
        WithTemporaryDirectory(root =>
        {
            var shim = CreateShim(root, "legacy.cmd", "@ECHO off", "legacy-native.exe %*");

            var startInfo = ProcessHelpers.CreateStartInfo(shim, "--version");

            Assert.Contains("cmd", Path.GetFileName(startInfo.FileName), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(shim, startInfo.Arguments);
        });
    }

    private static string[] ModernNpmShim(params string[] scriptParts) =>
    [
        "@ECHO off",
        "GOTO start",
        ":find_dp0",
        "SET dp0=%~dp0",
        "EXIT /b",
        ":start",
        "SETLOCAL",
        "CALL :find_dp0",
        "",
        "IF EXIST \"%dp0%\\node.exe\" (",
        "  SET \"_prog=%dp0%\\node.exe\"",
        ") ELSE (",
        "  SET \"_prog=node\"",
        "  SET PATHEXT=%PATHEXT:;.JS;=;%",
        ")",
        "",
        $"endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\{Relative(scriptParts)}\" %*",
    ];

    private static string[] LegacyNpmShim(params string[] scriptParts) =>
    [
        "@IF EXIST \"%~dp0\\node.exe\" (",
        $"  \"%~dp0\\node.exe\"  \"%~dp0\\{Relative(scriptParts)}\" %*",
        ") ELSE (",
        "  @SETLOCAL",
        "  @SET PATHEXT=%PATHEXT:;.JS;=;%",
        $"  node  \"%~dp0\\{Relative(scriptParts)}\" %*",
        ")",
    ];

    private static string Relative(params string[] parts) => Path.Combine(parts);

    private static string CreateShim(string directory, string name, params string[] lines)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string CreateFile(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static void WithTemporaryDirectory(Action<string> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "AIUsageDock.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            run(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
