using LabControl.Shared.Protocol;

namespace LabControl.Shared.Video;

/// <summary>
/// A <c>VideoControl</c> with the console's blanks filled from <see cref="Defaults"/>: what
/// a producer actually runs with. Immutable; a new control makes a new one.
/// </summary>
public sealed record VideoSettings(VideoMode Mode, double FramesPerSecond, int Quality, int MaxBitsPerSecond)
{
    public static VideoSettings From(VideoControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var mode = control.Mode == VideoMode.Full ? VideoMode.Full : VideoMode.Thumbnail;
        var fps = control.FramesPerSecond > 0
            ? control.FramesPerSecond
            : mode == VideoMode.Full ? Defaults.FullFramesPerSecond : Defaults.ThumbnailFramesPerSecond;
        var quality = control.Quality > 0
            ? Math.Clamp(control.Quality, 1, 100)
            : mode == VideoMode.Full ? Defaults.FullJpegQuality : Defaults.ThumbnailJpegQuality;
        var bits = control.MaxBitsPerSecond > 0
            ? control.MaxBitsPerSecond
            : mode == VideoMode.Full ? Defaults.FullModeBitsPerSecond : Defaults.ThumbnailModeBitsPerSecond;
        return new VideoSettings(mode, Math.Clamp(fps, 0.1, Defaults.VideoMaxFramesPerSecond), quality, bits);
    }

    /// <summary>The pause between two captures at the configured rate.</summary>
    public TimeSpan FrameInterval => TimeSpan.FromSeconds(1 / FramesPerSecond);

    /// <summary>The thumbnail control the console sends to every linked PC.</summary>
    public static VideoControl ThumbnailControl() => new()
    {
        Active = true,
        Mode = VideoMode.Thumbnail,
        FramesPerSecond = Defaults.ThumbnailFramesPerSecond,
        Quality = Defaults.ThumbnailJpegQuality,
        MaxBitsPerSecond = Defaults.ThumbnailModeBitsPerSecond,
    };

    /// <summary>The full-view control: native resolution, a keyframe first.</summary>
    public static VideoControl FullControl(bool requestKeyframe = true) => new()
    {
        Active = true,
        Mode = VideoMode.Full,
        FramesPerSecond = Defaults.FullFramesPerSecond,
        Quality = Defaults.FullJpegQuality,
        MaxBitsPerSecond = Defaults.FullModeBitsPerSecond,
        RequestKeyframe = requestKeyframe,
    };

    public static VideoControl StopControl() => new() { Active = false };
}
