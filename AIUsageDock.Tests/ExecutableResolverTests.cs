using System.Runtime.InteropServices;
using AIUsageDock.Providers;
using Xunit;

namespace AIUsageDock.Tests;

public sealed class ExecutableResolverTests
{
    [Theory]
    [InlineData(Architecture.X64, "codex-win32-x64", "x86_64-pc-windows-msvc")]
    [InlineData(Architecture.Arm64, "codex-win32-arm64", "aarch64-pc-windows-msvc")]
    public void CodexNpmShimResolvesToNativeExecutable(Architecture architecture, string package, string target)
    {
        WithTemporaryDirectory(root =>
        {
            var npm = Path.Combine(root, "npm");
            var shim = CreateFile(npm, "codex.cmd");
            var native = CreateFile(npm, "node_modules", "@openai", "codex", "node_modules", "@openai",
                package, "vendor", target, "bin", "codex.exe");

            var resolved = ExecutableResolver.ResolveCodex(npm, root, root, architecture);

            Assert.Equal(native, resolved);
            Assert.NotEqual(shim, resolved);
            var startInfo = ProcessHelpers.CreateStartInfo(resolved!, "app-server", "--stdio");
            Assert.Equal(native, startInfo.FileName);
            Assert.Equal(["app-server", "--stdio"], startInfo.ArgumentList);
            Assert.True(startInfo.CreateNoWindow);
            Assert.False(startInfo.UseShellExecute);
        });
    }

    [Fact]
    public void CodexFindsFlatNpmPackageLayout()
    {
        WithTemporaryDirectory(root =>
        {
            var npm = Path.Combine(root, "npm");
            CreateFile(npm, "codex.cmd");
            var native = CreateFile(npm, "node_modules", "@openai", "codex-win32-x64",
                "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");

            Assert.Equal(native, ExecutableResolver.ResolveCodex(npm, root, root, Architecture.X64));
        });
    }

    [Fact]
    public void CodexUsesShimWhenNativePackageIsMissing()
    {
        WithTemporaryDirectory(root =>
        {
            var npm = Path.Combine(root, "npm");
            var shim = CreateFile(npm, "codex.cmd");

            Assert.Equal(shim, ExecutableResolver.ResolveCodex(npm, root, root, Architecture.Arm64));
        });
    }

    [Fact]
    public void CodexCanResolveItsShimWhenNativePackageExists()
    {
        WithTemporaryDirectory(root =>
        {
            var npm = Path.Combine(root, "npm");
            var shim = CreateFile(npm, "codex.cmd");
            CreateFile(npm, "node_modules", "@openai", "codex", "node_modules", "@openai",
                "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe");

            Assert.Equal(shim, ExecutableResolver.ResolveCodexShim(npm, root));
        });
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
