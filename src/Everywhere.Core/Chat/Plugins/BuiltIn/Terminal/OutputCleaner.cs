using System.Text;
using System.Text.RegularExpressions;

namespace Everywhere.Chat.Plugins.BuiltIn.Terminal;

/// <summary>
/// Provides output cleaning utilities for terminal command output.
/// Extracted from TerminalPlugin to be shared between Rich and None execute strategies.
/// Ported from VS Code's strategyHelpers.ts.
/// </summary>
internal static partial class OutputCleaner
{
    private const int MaxOutputLength = 60_000;

    /// <summary>
    /// Clean raw PTY output: strip command echo, trailing prompt, and truncate if needed.
    /// Used by both Rich and None strategies as a final cleaning step.
    /// </summary>
    public static string CleanOutput(string rawOutput, string script)
    {
        // Strip script echo + trailing prompt
        rawOutput = StripCommandEchoAndPrompt(rawOutput, script);

        // Truncate if too long (keep tail)
        if (rawOutput.Length > MaxOutputLength)
        {
            const string truncationMessage = "\n\n[... PREVIOUS OUTPUT TRUNCATED ...]\n\n";
            var availableLength = MaxOutputLength - truncationMessage.Length;
            rawOutput = truncationMessage + rawOutput[^availableLength..];
        }

        return rawOutput.Trim();
    }

    /// <summary>
    /// Check if a line looks like a shell prompt, indicating the command has finished.
    /// Used by NoneExecuteStrategy for idle detection.
    /// </summary>
    public static bool IsShellPrompt(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // PowerShell: PS C:\path>
        if (PowerShellPromptRegex().IsMatch(line)) return true;
        // Bash/Zsh: user@host:path$ or user@host:path#
        if (BashZshPromptRegex().IsMatch(line)) return true;
        // Cmd: C:\path>
        if (CmdPromptRegex().IsMatch(line)) return true;
        // Generic: ends with $ or > (short lines only, to avoid false positives)
        if (line.Length < 200 && (line.EndsWith('$') || line.EndsWith('>'))) return true;

        return false;
    }

    /// <summary>
    /// Whether a command spans multiple lines (heredoc, multi-statement block, etc.).
    /// Multi-line commands must be sent verbatim through bracketed paste mode so the
    /// shell treats them as a single paste instead of executing each line as it
    /// arrives.
    ///
    /// Bare line continuations (`\` immediately before a newline) are **not**
    /// considered multi-line because the shell joins them into a single logical
    /// line. Only newlines that are *not* preceded by a backslash count.
    /// </summary>
    public static bool IsMultilineCommand(string command)
    {
        // Normalize all line-ending variants to \n, then check for a newline
        // that is not preceded by a backslash (i.e. not a line continuation).
        var normalized = MultilineNormalizeRegex().Replace(command, "\n");
        return IsMultilineRegex().IsMatch(normalized);
    }

    /// <summary>
    /// Escape non-printable characters for debug logging.
    /// Replaces control characters with their ^X notation (e.g. ESC → ^[, CR → ^M).
    /// </summary>
    public static string EscapeForLog(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\e': sb.Append("^["); break;
                case '\r': sb.Append("^M"); break;
                case '\n': sb.Append("^J"); break;
                case '\t': sb.Append("^I"); break;
                case '\0': sb.Append("^@"); break;
                case <= '\x1f': sb.Append('^').Append((char)(c + '@')); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    #region VS Code-inspired output cleaning

    /// <summary>
    /// Strips the command echo and trailing prompt lines from terminal output.
    /// Ported from VS Code's strategyHelpers.ts stripCommandEchoAndPrompt.
    ///
    /// Without shell integration, PTY output captures:
    /// 1. The command echo line (what was sent via stdin, with prompt prefix)
    /// 2. The actual command output
    /// 3. The next shell prompt line(s)
    ///
    /// This function removes (1) and (3) to isolate the actual output.
    /// </summary>
    public static string StripCommandEchoAndPrompt(string output, string commandLine)
    {
        var result = StripCommandEchoAndPromptOnce(output, commandLine);

        // After stripping the first command echo and trailing prompt, the remaining
        // content may still contain the command re-echoed by the shell. If the command
        // appears again in the remaining text, strip it one more time.
        if (result.Trim().Length > 0 && FindCommandEcho(result, commandLine, allowSuffixMatch: false).HasValue)
        {
            result = StripCommandEchoAndPromptOnce(result, commandLine);
        }

        return result;
    }

    /// <summary>
    /// Single-pass strip of command echo and trailing prompt.
    /// </summary>
    /// <remarks>
    /// https://github.com/microsoft/vscode/blob/161a11c5/src/vs/workbench/contrib/terminalContrib/chatAgentTools/browser/executeStrategy/strategyHelpers.ts
    /// </remarks>
    public static string StripCommandEchoAndPromptOnce(string output, string commandLine)
    {
        // Strip leading lines that are part of the command echo
        var echoResult = FindCommandEcho(output, commandLine, allowSuffixMatch: true);
        string[] lines;
        const int startIndex = 0;

        // Use evidence from the prompt prefix to narrow down which trailing prompt patterns to check
        var promptBefore = echoResult?.ContentBefore ?? "";
        var isUnixAt = UnixAtRegex().IsMatch(promptBefore);
        var isUnixHost = !isUnixAt && UnixHostRegex().IsMatch(promptBefore);
        var isUnix = isUnixAt || isUnixHost;
        var isPowerShell = PowerShellRegex().IsMatch(promptBefore);
        var isCmd = !isPowerShell && CmdRegex().IsMatch(promptBefore);
        var isStarship = promptBefore.Contains('\u276f');
        var isPython = promptBefore.Contains(">>>");
        var knownPrompt = isUnix || isPowerShell || isCmd || isStarship || isPython;

        if (echoResult.HasValue)
        {
            lines = echoResult.Value.LinesAfter;
        }
        else
        {
            lines = output.Split('\n');
        }

        // Strip trailing lines that are part of the next shell prompt.
        // Prompts may span multiple lines due to terminal column wrapping.
        var endIndex = lines.Length;
        var trailingStrippedCount = 0;
        const int maxTrailingPromptLines = 2;

        while (endIndex > startIndex)
        {
            var line = lines[endIndex - 1].TrimEnd();
            if (line.Length == 0)
            {
                endIndex--;
                continue;
            }
            if (trailingStrippedCount >= maxTrailingPromptLines)
            {
                break;
            }

            // Complete (self-contained) prompt patterns
            var isCompletePrompt =
                // Bash/zsh: user@host:path ending with $ or #
                (!knownPrompt || isUnixAt) && BashZshPromptRegex().IsMatch(line) ||
                // hostname:path user$ or hostname:path user#
                (!knownPrompt || isUnixHost) && HostNamePathPromptRegex().IsMatch(line) ||
                // PowerShell: PS C:\path>
                (!knownPrompt || isPowerShell) && PowerShellPromptRegex().IsMatch(line) ||
                // Windows cmd: C:\path>
                (!knownPrompt || isCmd) && CmdPromptRegex().IsMatch(line) ||
                // Starship prompt character
                (!knownPrompt || isStarship) && line.EndsWith('\u276f') ||
                // Python REPL
                (!knownPrompt || isPython) && line.TrimEnd() == ">>>";

            // Fragment/partial prompt patterns (wrapped across terminal lines)
            var isPromptFragment =
                // Wrapped fragment ending with $ or # (e.g. "er$", "ts/testWorkspace$")
                (!knownPrompt || isUnix) && WrappedFragmentEndingPromptRegex().IsMatch(line) ||
                // Bracketed prompt start: [ hostname:/path or [ user@host:/path
                (!knownPrompt || isUnix) && BracketedPromptStartRegex().IsMatch(line) ||
                // Wrapped continuation (only after already stripping a fragment)
                (!knownPrompt || isUnix) && trailingStrippedCount > 0 && WrappedContinuationPromptRegex().IsMatch(line) ||
                // Bracketed prompt end: ...] $ or ...] #
                (!knownPrompt || isUnix) && BracketedPromptEndRegex().IsMatch(line);

            if (isCompletePrompt)
            {
                endIndex--;
                // trailingStrippedCount++;
                break; // Complete prompt = nothing above can be prompt wrap
            }
            if (isPromptFragment)
            {
                endIndex--;
                trailingStrippedCount++;
            }
            else
            {
                break;
            }
        }

        return string.Join('\n', lines[startIndex..endIndex]);
    }

    /// <summary>
    /// Finds the command echo in the output and returns the content before it (for prompt type detection)
    /// and the lines after the echo (the actual output).
    /// Ported from VS Code's strategyHelpers.ts findCommandEcho.
    ///
    /// The algorithm strips newlines from both output and command, does a substring search,
    /// then maps the match position back to the original line structure.
    /// This handles terminal wrapping that splits the command echo across multiple lines.
    /// </summary>
    public static (string ContentBefore, string[] LinesAfter)? FindCommandEcho(string output, string commandLine, bool allowSuffixMatch)
    {
        var trimmedCommand = commandLine.Trim();
        if (trimmedCommand.Length == 0) return null;

        // Strip newlines from the output so we can find the command as a
        // contiguous substring even when terminal wrapping splits it across lines.
        var (strippedOutput, indexMapping) = StripNewLinesAndBuildMapping(output);
        var matchIndex = strippedOutput.IndexOf(trimmedCommand, StringComparison.Ordinal);

        int matchEndInStripped;
        string contentBefore;

        if (matchIndex != -1)
        {
            // Full command found in the output
            contentBefore = strippedOutput[..matchIndex].Trim();
            matchEndInStripped = matchIndex + trimmedCommand.Length - 1;
        }
        else if (allowSuffixMatch)
        {
            // If the full command wasn't found, check if the output starts with a
            // suffix of the command. This happens when the prompt line is not included,
            // so only the wrapped continuation of the command echo appears at the beginning.
            var suffixLen = 0;
            for (var len = trimmedCommand.Length - 1; len >= 1; len--)
            {
                var suffix = trimmedCommand[^len..];
                if (strippedOutput.StartsWith(suffix, StringComparison.Ordinal))
                {
                    // Require the suffix to start mid-word in the command (not at a word boundary).
                    // A word-boundary match like "MARKER_123" matching the tail of "echo MARKER_123"
                    // is almost certainly actual output, not a wrapped command continuation.
                    var charBefore = trimmedCommand[trimmedCommand.Length - len - 1];
                    if (charBefore is not (' ' or '\t'))
                    {
                        suffixLen = len;
                    }
                    break;
                }
            }
            if (suffixLen == 0) return null;

            contentBefore = "";
            matchEndInStripped = suffixLen - 1;
        }
        else
        {
            return null;
        }

        // Map the match end back to the original output position and determine
        // which line it falls on to split linesAfter.
        var originalEnd = indexMapping[matchEndInStripped];

        var lines = output.Split('\n');
        var echoEndLine = 0;
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var lineEnd = offset + lines[i].Length; // excludes the \n
            if (offset <= originalEnd && originalEnd <= lineEnd)
            {
                echoEndLine = i + 1;
                break;
            }
            offset = lineEnd + 1; // +1 for the \n
        }

        return (contentBefore, lines[echoEndLine..]);
    }

    /// <summary>
    /// Strips newlines from the output and builds a mapping from stripped indices to original indices.
    /// Ported from VS Code's strategyHelpers.ts stripNewLinesAndBuildMapping.
    /// </summary>
    public static (string StrippedOutput, int[] IndexMapping) StripNewLinesAndBuildMapping(string output)
    {
        var indexMapping = new List<int>(output.Length);
        var strippedChars = new StringBuilder(output.Length);
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] != '\n')
            {
                strippedChars.Append(output[i]);
                indexMapping.Add(i);
            }
        }
        return (strippedChars.ToString(), indexMapping.ToArray());
    }

    #endregion

    #region Regex Patterns

    [GeneratedRegex(@"\r\n|\r")]
    private static partial Regex MultilineNormalizeRegex();

    [GeneratedRegex(@"(?<!\\)\n")]
    private static partial Regex IsMultilineRegex();

    [GeneratedRegex(@"\w+@[\w.-]+:")]
    private static partial Regex UnixAtRegex();

    [GeneratedRegex(@"[\w.-]+:\S")]
    private static partial Regex UnixHostRegex();

    [GeneratedRegex(@"^PS\s", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellRegex();

    [GeneratedRegex(@"^[A-Z]:\\")]
    private static partial Regex CmdRegex();

    [GeneratedRegex(@"^\s*\w+@[\w.-]+:.*[#$]\s*$")]
    private static partial Regex BashZshPromptRegex();

    [GeneratedRegex(@"^\s*[\w.-]+:\S.*\s\w+[#$]\s*$")]
    private static partial Regex HostNamePathPromptRegex();

    [GeneratedRegex(@"^PS\s+[A-Z]:\\.*>\s*$")]
    private static partial Regex PowerShellPromptRegex();

    [GeneratedRegex(@"^[A-Z]:\\.*>\s*$")]
    private static partial Regex CmdPromptRegex();

    [GeneratedRegex(@"^\s*[\w/.-]+[#$]\s*$")]
    private static partial Regex WrappedFragmentEndingPromptRegex();

    [GeneratedRegex(@"^\[\s*[\w.-]+(@[\w.-]+)?:[~/]")]
    private static partial Regex BracketedPromptStartRegex();

    [GeneratedRegex(@"^\s*[\w][-\w.]*(@[\w.-]+)?:\S")]
    private static partial Regex WrappedContinuationPromptRegex();

    [GeneratedRegex(@"\]\s*[#$]\s*$")]
    private static partial Regex BracketedPromptEndRegex();

    #endregion

}