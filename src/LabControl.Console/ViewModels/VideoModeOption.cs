using LabControl.Console.Localization;

namespace LabControl.Console.ViewModels;

/// <summary>One entry of the *Text / Video* choice (M7 portion 6, D-91): its label and whether it is *Video*.</summary>
public sealed record VideoModeOption(string Label, bool Video)
{
    /// <summary>*Text*, and *Video* when this console can do it.</summary>
    public static IReadOnlyList<VideoModeOption> All(bool canVideo) => canVideo
        ? [new(Strings.Get("VideoMode.Text"), false), new(Strings.Get("VideoMode.Video"), true)]
        : [new(Strings.Get("VideoMode.Text"), false)];

    public override string ToString() => Label;
}
