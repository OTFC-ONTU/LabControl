namespace LabControl.Shared.Video.H264;

/// <summary>
/// The platform's H.264 (D-91 item 1): VideoToolbox on macOS, Media Foundation on Windows,
/// nothing on Linux. Whether this computer can encode or decode is found once, by making a
/// small encoder and decoder, so a Windows N edition without the Media Feature Pack or a
/// broken codec reports "no" instead of failing in the middle of a lesson.
/// <c>LABCONTROL_NO_H264=1</c> turns H.264 off — the JPEG fallback drill.
/// </summary>
public static class H264Codecs
{
    private static readonly Lazy<string?> EncodeProblem = new(() => Probe(() => CreateEncoder(64, 64, 10, 256 * 1024)));
    private static readonly Lazy<string?> DecodeProblem = new(() => Probe(CreateDecoder));

    /// <summary>Off by the environment, for a drill of the JPEG fallback.</summary>
    public static bool Disabled => Environment.GetEnvironmentVariable("LABCONTROL_NO_H264") is "1" or "true";

    public static bool CanEncode => EncodeProblem.Value is null;

    public static bool CanDecode => DecodeProblem.Value is null;

    /// <summary>Why this computer cannot encode, for a log line; <c>null</c> when it can.</summary>
    public static string? WhyNoEncoder => EncodeProblem.Value;

    public static string? WhyNoDecoder => DecodeProblem.Value;

    /// <summary>The codecs besides JPEG this computer decodes — <c>HelperHello.video_decoders</c>, <c>BroadcastRequest.codecs</c>.</summary>
    public static IReadOnlyList<string> Decoders => CanDecode ? [Defaults.VideoCodecH264] : [];

    /// <summary>The codecs besides JPEG this computer encodes — <c>HelperHello.video_encoders</c>.</summary>
    public static IReadOnlyList<string> Encoders => CanEncode ? [Defaults.VideoCodecH264] : [];

    /// <summary>A new encoder for one picture size; throws <see cref="H264Exception"/> where there is none.</summary>
    public static IH264Encoder CreateEncoder(int width, int height, double framesPerSecond, int bitsPerSecond)
    {
        if (Disabled)
        {
            throw new H264Exception("H.264 is turned off (LABCONTROL_NO_H264)");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new VideoToolboxH264Encoder(width, height, framesPerSecond, bitsPerSecond);
        }

        if (OperatingSystem.IsWindows())
        {
            return new MediaFoundationH264Encoder(width, height, framesPerSecond, bitsPerSecond);
        }

        throw new H264Exception("this platform has no H.264 encoder LabControl can use");
    }

    /// <summary>A new decoder for one stream; throws <see cref="H264Exception"/> where there is none.</summary>
    public static IH264Decoder CreateDecoder()
    {
        if (Disabled)
        {
            throw new H264Exception("H.264 is turned off (LABCONTROL_NO_H264)");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new VideoToolboxH264Decoder();
        }

        if (OperatingSystem.IsWindows())
        {
            return new MediaFoundationH264Decoder();
        }

        throw new H264Exception("this platform has no H.264 decoder LabControl can use");
    }

    private static string? Probe(Func<IDisposable> create)
    {
        try
        {
            create().Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
