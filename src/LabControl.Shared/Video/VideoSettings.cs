using LabControl.Shared.Protocol;

namespace LabControl.Shared.Video;

/// <summary>
/// A <c>VideoControl</c> with the console's blanks filled from <see cref="Defaults"/>: what
/// a producer actually runs with. Immutable; a new control makes a new one. <see cref="Codec"/>
/// is <see cref="Defaults.VideoCodecH264"/> only in full mode when the console asked for it
/// (D-91); everything else is JPEG.
/// </summary>
public sealed record VideoSettings(VideoMode Mode, double FramesPerSecond, int Quality, int MaxBitsPerSecond, string Codec = Defaults.VideoCodecJpeg)
{
    public static VideoSettings From(VideoControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var mode = control.Mode == VideoMode.Full ? VideoMode.Full : VideoMode.Thumbnail;
        var fps = control.FramesPerSecond > 0
            ? control.FramesPerSecond
            : mode == VideoMode.Full ? Defaults.FullFramesPerSecond : Defaults.ThumbnailFramesPerSecond;
        // Full mode: 0 is "auto" and stays 0 — the producer picks (D-37). Thumbnails have no auto.
        var quality = control.Quality > 0
            ? Math.Clamp(control.Quality, 1, 100)
            : mode == VideoMode.Full ? Defaults.VideoQualityAuto : Defaults.ThumbnailJpegQuality;
        var bits = control.MaxBitsPerSecond > 0
            ? control.MaxBitsPerSecond
            : mode == VideoMode.Full ? Defaults.FullModeBitsPerSecond : Defaults.ThumbnailModeBitsPerSecond;
        var codec = mode == VideoMode.Full && string.Equals(control.Codec, Defaults.VideoCodecH264, StringComparison.OrdinalIgnoreCase)
            ? Defaults.VideoCodecH264
            : Defaults.VideoCodecJpeg;
        return new VideoSettings(mode, Math.Clamp(fps, 0.1, Defaults.VideoMaxFramesPerSecond), quality, bits, codec);
    }

    /// <summary>The console asked for H.264 (the teacher's *Video*, D-91).</summary>
    public bool WantsH264 => Codec == Defaults.VideoCodecH264;

    /// <summary>Full mode with the quality left to the producer (D-37).</summary>
    public bool IsAdaptive => Mode == VideoMode.Full && Quality == Defaults.VideoQualityAuto;

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

    /// <summary>
    /// The full-view control: native resolution, a keyframe first. *Text* is JPEG with the
    /// quality auto unless the teacher chose one; *Video* (<paramref name="video"/>) asks for
    /// H.264 at <see cref="Defaults.FullVideoFramesPerSecond"/> (D-91) — a PC that cannot
    /// encode it answers in JPEG.
    /// </summary>
    public static VideoControl FullControl(bool requestKeyframe = true, int quality = Defaults.VideoQualityAuto, bool video = false) => video
        ? new VideoControl
        {
            Active = true,
            Mode = VideoMode.Full,
            FramesPerSecond = Defaults.FullVideoFramesPerSecond,
            Quality = quality,
            MaxBitsPerSecond = Defaults.FullVideoBitsPerSecond,
            RequestKeyframe = requestKeyframe,
            Codec = Defaults.VideoCodecH264,
        }
        : new VideoControl
        {
            Active = true,
            Mode = VideoMode.Full,
            FramesPerSecond = Defaults.FullFramesPerSecond,
            Quality = quality,
            MaxBitsPerSecond = Defaults.FullModeBitsPerSecond,
            RequestKeyframe = requestKeyframe,
        };

    public static VideoControl StopControl() => new() { Active = false };
}
