using System.Management.Automation.Language;
using System.Text.RegularExpressions;
using LabControl.Shared.Jobs;
using LabControl.Console.Localization;

namespace LabControl.Console.Services;

public sealed record ScriptDiagnostic(int Line, int Column, string Message);
public sealed record ScriptToken(int Start, int End, string Kind);
public sealed record ScriptAnalysis(IReadOnlyList<ScriptToken> Tokens, IReadOnlyList<ScriptDiagnostic> Errors)
{
    /// <summary>Parses only. Never creates a runspace or executes the teacher's text.</summary>
    public static ScriptAnalysis Analyze(string text, ScriptShell shell)
    {
        if (shell == ScriptShell.Cmd)
        {
            var spans = Regex.Matches(text, @"(?im)^\s*@?(?:rem\b[^\r\n]*|::[^\r\n]*)|%[^%\r\n]+%|![^!\r\n]+!|""[^""\r\n]*""|\b(?:echo|set|if|else|for|do|call|exit|goto|start)\b")
                .Select(m => new ScriptToken(m.Index, m.Index + m.Length,
                    m.Value.TrimStart().StartsWith("rem", StringComparison.OrdinalIgnoreCase) || m.Value.TrimStart().StartsWith("::") ? "Comment" : "Keyword")).ToArray();
            return new(spans, text.Contains('\0') ? [new(1, 1, Strings.Get("Scripts.InvalidNul"))] : []);
        }

        Parser.ParseInput(text, out var tokens, out var errors);
        var diagnostics = errors.Select(e => new ScriptDiagnostic(e.Extent.StartLineNumber, e.Extent.StartColumnNumber, e.Message)).ToList();
        // The agent uses Windows PowerShell 5.1; these PS7 tokens must not silently pass.
        foreach (var t in tokens.Where(t => t.Kind.ToString() is "AndAnd" or "OrOr" or "QuestionQuestion" or "QuestionQuestionEquals" or "QuestionDot" or "QuestionLBracket" or "QuestionMark"))
            diagnostics.Add(new(t.Extent.StartLineNumber, t.Extent.StartColumnNumber, Strings.Get("Scripts.RequiresPowerShell7")));
        return new(tokens.Select(t => new ScriptToken(t.Extent.StartOffset, t.Extent.EndOffset,
            t.TokenFlags.HasFlag(TokenFlags.Keyword) ? "Keyword" : t.TokenFlags.HasFlag(TokenFlags.CommandName) ? "Command" : t.Kind.ToString())).ToArray(), diagnostics);
    }
}
