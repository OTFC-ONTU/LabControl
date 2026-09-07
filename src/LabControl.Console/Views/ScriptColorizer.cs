using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LabControl.Console.Services;

namespace LabControl.Console.Views;

internal sealed class ScriptColorizer : DocumentColorizingTransformer
{
    public IReadOnlyList<ScriptToken> Tokens { get; set; } = [];

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var token in Tokens)
        {
            var start = Math.Max(line.Offset, token.Start);
            var end = Math.Min(line.EndOffset, token.End);
            if (start >= end) continue;
            var brush = token.Kind switch
            {
                "Comment" => Brushes.SeaGreen,
                "StringLiteral" or "StringExpandable" or "HereStringLiteral" or "HereStringExpandable" => Brushes.Peru,
                "Variable" or "SplattedVariable" => Brushes.DodgerBlue,
                "Number" => Brushes.MediumOrchid,
                "Keyword" => Brushes.MediumPurple,
                "Command" => Brushes.Teal,
                "Parameter" => Brushes.SlateGray,
                _ => null,
            };
            if (brush is not null)
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
