namespace LabControl.Shared.Video.H264;

/// <summary>
/// The platform's H.264 (D-91 item 1): VideoToolbox on macOS, Media Foundation on Windows,
/// nothing on Linux. Whether this computer can encode or decode is found once, by making a
/// small encoder and decoder, so a Windows N edition without the Media Feature Pack or a
/// broken codec reports "no" instead of failing in the middle of a lesson.
/// <c>LABCONTROL_NO_H264=1</c> turns H.264 off — the JPEG fallback drill;
/// <c>LABCONTROL_H264_DECODE_FAIL_AFTER=N</c> makes every decoder fail N seconds after the
/// process first decoded a picture — the drill of a decoder that breaks mid-broadcast (D-91 item 11).
/// </summary>
public static class H264Codecs
{
    private static volatile string? _decoderBroken;
    private static long _firstDecodeAt;

    private static readonly Lazy<string?> EncodeProblem = new(() => Probe(() => CreateEncoder(64, 64, 10, 256 * 1024)));
    private static readonly Lazy<string?> DecodeProblem = new(() => Probe(CreateDecoder));

    /// <summary>Off by the environment, for a drill of the JPEG fallback.</summary>
    public static bool Disabled => Environment.GetEnvironmentVariable("LABCONTROL_NO_H264") is "1" or "true";

    public static bool CanEncode => EncodeProblem.Value is null;

    public static bool CanDecode => DecodeProblem.Value is null && _decoderBroken is null;

    /// <summary>
    /// The decoder broke while it was in use (D-91 item 11): from now on this process says it
    /// decodes nothing but JPEG. A new helper probes again.
    /// </summary>
    public static void MarkDecoderBroken(string why) => _decoderBroken = why;

    /// <summary>Why this computer cannot encode, for a log line; <c>null</c> when it can.</summary>
    public static string? WhyNoEncoder => EncodeProblem.Value;

    public static string? WhyNoDecoder => _decoderBroken ?? DecodeProblem.Value;

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

        IH264Decoder? decoder = null;
        if (OperatingSystem.IsMacOS())
        {
            decoder = new VideoToolboxH264Decoder();
        }
        else if (OperatingSystem.IsWindows())
        {
            decoder = new MediaFoundationH264Decoder();
        }

        if (decoder is not null)
        {
            if (Environment.GetEnvironmentVariable("LABCONTROL_H264_DECODE_FAIL_AFTER") is { } after
                && int.TryParse(after, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            {
                return new FailingDecoder(decoder, seconds * 1000L);
            }

            return decoder;
        }

        throw new H264Exception("this platform has no H.264 decoder LabControl can use");
    }

    /// <summary>The drill's decoder: the real one until the deadline, then nothing but failures.</summary>
    private sealed class FailingDecoder(IH264Decoder inner, long afterMilliseconds) : IH264Decoder
    {
        public string Kind => inner.Kind + "+failing";

        public SkiaSharp.SKBitmap? Decode(ReadOnlySpan<byte> accessUnit)
        {
            Interlocked.CompareExchange(ref _firstDecodeAt, Environment.TickCount64, 0);
            return Environment.TickCount64 >= Interlocked.Read(ref _firstDecodeAt) + afterMilliseconds
                ? throw new H264Exception("the decoder was told to fail (LABCONTROL_H264_DECODE_FAIL_AFTER)")
                : inner.Decode(accessUnit);
        }

        public void Dispose() => inner.Dispose();
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
