using System.Text;

namespace AIUsageDock.Providers;

/// <summary>
/// Expands an npm-style <c>.cmd</c>/<c>.bat</c> launcher into the Node interpreter and
/// script it would have run, so the CLI can be started without a cmd.exe hop.
/// </summary>
/// <remarks>
/// A batch launcher has to run under cmd.exe, which is a console subsystem process.
/// Windows gives it a console even when the caller asks for CreateNoWindow, and with
/// console handoff enabled the terminal host can flash a window before the command
/// exits. Launching node.exe directly removes the shell hop entirely.
/// </remarks>
internal static class NodeShimResolver
{
    private static readonly string[] ScriptExtensions = [".js", ".cjs", ".mjs"];
    private static readonly string[] ShimDirectoryVariables = ["%dp0%", "%~dp0"];
    private static readonly string[] InterpreterNames = ["node", "node.exe"];

    public static bool TryExpand(string shimPath, out string interpreter, out IReadOnlyList<string> leadingArguments) =>
        TryExpand(shimPath, ReadAllLinesOrNull, File.Exists, FindNodeOnPath, out interpreter, out leadingArguments);

    internal static bool TryExpand(
        string shimPath,
        Func<string, IReadOnlyList<string>?> readLines,
        Func<string, bool> fileExists,
        Func<Func<string, bool>, string?> findNodeOnPath,
        out string interpreter,
        out IReadOnlyList<string> leadingArguments)
    {
        interpreter = string.Empty;
        leadingArguments = [];

        var directory = Path.GetDirectoryName(shimPath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var lines = readLines(shimPath);
        if (lines is null)
        {
            return false;
        }

        foreach (var line in lines)
        {
            var tokens = Tokenize(line);
            var scriptIndex = IndexOfScript(tokens, directory, fileExists, out var script);
            if (scriptIndex < 0)
            {
                continue;
            }

            // A shim that ships its own Node next to itself wins over whatever is on PATH,
            // which is the same order the batch launcher uses.
            var node = Path.Combine(directory, "node.exe");
            if (!fileExists(node))
            {
                node = findNodeOnPath(fileExists);
                if (node is null)
                {
                    return false;
                }
            }

            // Everything the launcher puts between node and the script is an option for
            // node itself. Reading it forward from the interpreter keeps an option that
            // takes a separate value, such as -r esm, together with that value.
            var interpreterIndex = IndexOfInterpreter(tokens, scriptIndex, directory);
            if (interpreterIndex < 0)
            {
                continue;
            }

            var arguments = InterpreterOptions(tokens, interpreterIndex, scriptIndex);
            if (arguments is null)
            {
                continue;
            }

            arguments.Add(script!);
            arguments.AddRange(ScriptOptions(tokens, scriptIndex));

            interpreter = node;
            leadingArguments = arguments;
            return true;
        }

        return false;
    }

    private static int IndexOfScript(
        IReadOnlyList<string> tokens,
        string directory,
        Func<string, bool> fileExists,
        out string? script)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            var expanded = ExpandShimDirectory(tokens[index], directory);
            if (expanded is null ||
                !ScriptExtensions.Contains(Path.GetExtension(expanded), StringComparer.OrdinalIgnoreCase) ||
                !fileExists(expanded))
            {
                continue;
            }

            script = expanded;
            return index;
        }

        script = null;
        return -1;
    }

    /// <summary>Finds the node reference the launcher runs the script with, nearest to the script.</summary>
    private static int IndexOfInterpreter(IReadOnlyList<string> tokens, int scriptIndex, string directory)
    {
        for (var index = scriptIndex - 1; index >= 0; index--)
        {
            if (IsInterpreterToken(tokens[index], directory))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsInterpreterToken(string token, string directory)
    {
        // npm's launcher picks between a bundled node and one on PATH, then runs %_prog%.
        if (token.Equals("%_prog%", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(ExpandShimDirectory(token, directory) ?? token);
        return InterpreterNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Options the launcher passes to node itself, or null when one of them holds a
    /// variable this parser cannot expand.
    /// </summary>
    private static List<string>? InterpreterOptions(IReadOnlyList<string> tokens, int interpreterIndex, int scriptIndex)
    {
        var options = new List<string>();
        for (var index = interpreterIndex + 1; index < scriptIndex; index++)
        {
            // Leave the line to cmd.exe rather than launching node without an argument
            // it needs.
            if (tokens[index].Contains('%'))
            {
                return null;
            }

            options.Add(tokens[index]);
        }

        return options;
    }

    /// <summary>Fixed arguments the launcher appends before the caller's own, up to <c>%*</c>.</summary>
    private static IEnumerable<string> ScriptOptions(IReadOnlyList<string> tokens, int scriptIndex)
    {
        for (var index = scriptIndex + 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Contains('%'))
            {
                yield break;
            }

            yield return token;
        }
    }

    /// <summary>Resolves a <c>%dp0%</c> or <c>%~dp0</c> prefixed path against the launcher's own directory.</summary>
    private static string? ExpandShimDirectory(string token, string directory)
    {
        var prefix = ShimDirectoryVariables.FirstOrDefault(candidate => token.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return null;
        }

        var relative = token[prefix.Length..].TrimStart('\\', '/');
        if (relative.Length == 0 || relative.Contains('%'))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(character);
            started = true;
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string? FindNodeOnPath(Func<string, bool> fileExists)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var fileName = OperatingSystem.IsWindows() ? "node.exe" : "node";

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadAllLinesOrNull(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
