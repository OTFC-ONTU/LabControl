using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace LabControl.Shared.Video.H264;

/// <summary>
/// What the Media Foundation encoder and decoder share (D-91 item 1): one <c>MFStartup</c> per
/// process, COM on the calling thread, the synchronous in-box MFT for a category found with
/// <c>MFTEnumEx</c>, and the HRESULTs the transform loop reads.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class MediaFoundation
{
    public const int NeedMoreInput = unchecked((int)0xC00D6D72);   // MF_E_TRANSFORM_NEED_MORE_INPUT
    public const int StreamChange = unchecked((int)0xC00D6D61);    // MF_E_TRANSFORM_STREAM_CHANGE

    /// <summary>MF_LOW_LATENCY / CODECAPI_AVLowLatencyMode: one picture in, one out, no reordering buffer.</summary>
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame.</summary>
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>CODECAPI_AVEncMPVGOPSize.</summary>
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private static readonly Guid CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private const uint MfVersion = 0x00020070;   // MF_SDK_VERSION << 16 | MF_API_VERSION

    private static readonly Lazy<int> Started = new(() => MFStartup(MfVersion, 0));

    [ThreadStatic]
    private static bool _comReady;

    /// <summary>MFStartup once and COM (multithreaded) on this thread — the producer's loop moves between pool threads.</summary>
    public static void Ready()
    {
        if (!_comReady)
        {
            // S_OK, S_FALSE (already) and RPC_E_CHANGED_MODE (an STA thread) all leave COM usable here.
            _ = CoInitializeEx(IntPtr.Zero, 0);
            _comReady = true;
        }

        var started = Started.Value;
        if (started < 0)
        {
            throw new H264Exception($"MFStartup failed (0x{started:X8})");
        }
    }

    /// <summary>The first synchronous, local MFT of the category that turns <paramref name="input"/> into <paramref name="output"/>.</summary>
    public static IMFTransform Find(Guid category, Guid input, Guid? output)
    {
        Ready();
        var inputType = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = input };
        RegisterTypeInfo? outputType = output is { } o ? new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = o } : null;
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter);
        using var found = MediaFactory.MFTEnumEx(category, flags, inputType, outputType);
        foreach (var activate in found)
        {
            try
            {
                return activate.ActivateObject<IMFTransform>();
            }
            catch (Exception)
            {
                // The next one, if there is one.
            }
            finally
            {
                activate.Dispose();
            }
        }

        throw new H264Exception("Windows has no H.264 codec here (an N edition needs the Media Feature Pack)");
    }

    /// <summary>
    /// <c>ICodecAPI::SetValue</c> with a <c>VT_UI4</c>, through the interface's vtable (Vortice
    /// does not wrap it). <c>false</c> when the codec has no <c>ICodecAPI</c> or refused.
    /// </summary>
    public static bool SetCodecValue(IMFTransform transform, Guid api, uint value)
    {
        var iid = CodecApi;
        if (Marshal.QueryInterface(transform.NativePointer, in iid, out var codecApi) < 0 || codecApi == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var variant = stackalloc byte[24];   // VARIANT on a 64-bit process
            new Span<byte>(variant, 24).Clear();
            *(ushort*)variant = 19;               // VT_UI4
            *(uint*)(variant + 8) = value;
            var vtable = *(IntPtr**)codecApi;
            // IUnknown (3), IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue, SetValue.
            var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, void*, int>)vtable[9];
            return setValue(codecApi, &api, variant) >= 0;
        }
        finally
        {
            Marshal.Release(codecApi);
        }
    }

    public static IMFSample Sample(int capacity, out IMFMediaBuffer buffer)
    {
        var sample = MediaFactory.MFCreateSample();
        buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(1, capacity));
        sample.AddBuffer(buffer);
        return sample;
    }

    public static void Write(IMFMediaBuffer buffer, ReadOnlySpan<byte> data)
    {
        buffer.Lock(out var pointer, out var max, out _);
        try
        {
            if (data.Length > max)
            {
                throw new H264Exception("the sample buffer is smaller than the data");
            }

            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = data.Length;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);
}

/// <summary>
/// H.264 encoding with Microsoft's in-box software encoder MFT (D-91 item 1) on a PC's full
/// view and on a Windows console's broadcast: NV12 converted from BGRA in C#, baseline
/// profile, low latency, the target bit rate fixed at creation and IDRs forced through
/// <c>ICodecAPI</c> when asked for (a fresh encoder starts with one anyway).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationH264Encoder : IH264Encoder
{
    private const uint ProfileBaseline = 66;       // eAVEncH264VProfile_Base
    private const uint InterlaceProgressive = 2;   // MFVideoInterlace_Progressive

    private readonly IMFTransform _transform;
    private readonly int _frameRate;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private long _frame;
    private bool _first = true;

    public MediaFoundationH264Encoder(int width, int height, double framesPerSecond, int bitsPerSecond)
    {
        if ((width & 1) != 0 || (height & 1) != 0 || width <= 0 || height <= 0)
        {
            throw new H264Exception($"the encoder needs an even size, not {width}×{height}");
        }

        Width = width;
        Height = height;
        _frameRate = Math.Max(1, (int)Math.Round(framesPerSecond));
        _transform = MediaFoundation.Find(TransformCategoryGuids.VideoEncoder, VideoFormatGuids.NV12, VideoFormatGuids.H264);
        try
        {
            _transform.Attributes?.Set(MediaFoundation.LowLatency, 1u);
            MediaFoundation.SetCodecValue(_transform, MediaFoundation.LowLatency, 1);

            // The encoder wants its output type before its input type.
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Max(64 * 1024, bitsPerSecond));
                output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
                output.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(_frameRate, 1));
                output.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
                output.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileBaseline);
                _transform.SetOutputType(0, output, 0);
            }

            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                input.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
                input.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(_frameRate, 1));
                input.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
                input.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
                _transform.SetInputType(0, input, 0);
            }

            // Every IDR is one the producer asked for.
            MediaFoundation.SetCodecValue(_transform, MediaFoundation.GopSize, 0x7FFFFFFF);

            var info = _transform.GetOutputStreamInfo(0);
            _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
            _outputSize = Math.Max(info.Size, width * height);
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            ReadSequenceHeader();
        }
        catch (Exception ex)
        {
            _transform.Dispose();
            throw ex as H264Exception ?? new H264Exception($"the Media Foundation encoder could not be set up: {ex.Message}", ex);
        }
    }

    public string Kind => "mediafoundation";

    public int Width { get; }

    public int Height { get; }

    public byte[]? Encode(ReadOnlySpan<byte> bgra, int rowBytes, bool keyframe, out bool isKeyframe)
    {
        isKeyframe = false;
        MediaFoundation.Ready();
        try
        {
            if (keyframe && !_first)
            {
                MediaFoundation.SetCodecValue(_transform, MediaFoundation.ForceKeyFrame, 1);
            }

            _first = false;
            var size = Nv12.Size(Width, Height);
            using var sample = MediaFoundation.Sample(size, out var buffer);
            using (buffer)
            {
                buffer.Lock(out var pointer, out _, out _);
                try
                {
                    Nv12.FromBgra(bgra, rowBytes, Width, Height, new Span<byte>((void*)pointer, size), Width, Height);
                }
                finally
                {
                    buffer.Unlock();
                }

                buffer.CurrentLength = size;
            }

            var duration = 10_000_000L / _frameRate;
            sample.SampleTime = _frame * duration;
            sample.SampleDuration = duration;
            _frame++;
            _transform.ProcessInput(0, sample, 0);

            var output = Drain();
            if (output is null)
            {
                return null;
            }

            isKeyframe = AnnexB.IsIdr(output);
            if (isKeyframe && !AnnexB.Contains(output, AnnexB.NalSps))
            {
                // A PC joins on an IDR: it must carry the SPS and PPS itself.
                ReadSequenceHeader();
                if (_sequenceHeader is { Length: > 0 } header)
                {
                    output = [.. header, .. output];
                }
            }

            return output;
        }
        catch (Exception ex) when (ex is not H264Exception and not ArgumentException)
        {
            throw new H264Exception($"the Media Foundation encoder failed: {ex.Message}", ex);
        }
    }

    /// <summary>Everything the encoder has for the picture just given, as one Annex B access unit; <c>null</c> when it wants more input first.</summary>
    private byte[]? Drain()
    {
        List<byte>? collected = null;
        while (true)
        {
            IMFSample? own = null;
            IMFMediaBuffer? ownBuffer = null;
            if (!_providesSamples)
            {
                own = MediaFoundation.Sample(_outputSize, out ownBuffer);
            }

            try
            {
                var data = new OutputDataBuffer { StreamID = 0, Sample = own! };
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                data.Events?.Dispose();
                if (result.Code == MediaFoundation.NeedMoreInput)
                {
                    return collected?.ToArray();
                }

                if (result.Code == MediaFoundation.StreamChange)
                {
                    continue;
                }

                result.CheckError();
                var produced = data.Sample;
                if (produced is null)
                {
                    return collected?.ToArray();
                }

                try
                {
                    using var contiguous = produced.ConvertToContiguousBuffer();
                    contiguous.Lock(out var pointer, out _, out var length);
                    try
                    {
                        collected ??= new List<byte>(length);
                        collected.AddRange(new ReadOnlySpan<byte>((void*)pointer, length));
                    }
                    finally
                    {
                        contiguous.Unlock();
                    }
                }
                finally
                {
                    if (!ReferenceEquals(produced, own))
                    {
                        produced.Dispose();
                    }
                }
            }
            finally
            {
                ownBuffer?.Dispose();
                own?.Dispose();
            }
        }
    }

    private void ReadSequenceHeader()
    {
        try
        {
            using var type = _transform.GetOutputCurrentType(0);
            var header = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (header is { Length: > 0 })
            {
                _sequenceHeader = header;
            }
        }
        catch (Exception)
        {
            // Not every encoder publishes it before the first picture; the IDR then carries it.
        }
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (Exception)
        {
        }

        _transform.Dispose();
    }
}

/// <summary>
/// H.264 decoding with Microsoft's in-box decoder MFT (D-91 item 1) for the broadcast on a PC
/// and a full view on a Windows console: Annex B in, NV12 out (re-negotiated on a stream
/// change), converted into one reused BGRA bitmap cropped to the picture's display area.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationH264Decoder : IH264Decoder
{
    private readonly IMFTransform _transform;
    private bool _providesSamples;
    private int _outputSize;
    private int _stride;
    private int _planeHeight;
    private int _width;
    private int _height;
    private SKBitmap? _picture;
    private long _frame;

    public MediaFoundationH264Decoder()
    {
        _transform = MediaFoundation.Find(TransformCategoryGuids.VideoDecoder, VideoFormatGuids.H264, null);
        try
        {
            _transform.Attributes?.Set(MediaFoundation.LowLatency, 1u);
            MediaFoundation.SetCodecValue(_transform, MediaFoundation.LowLatency, 1);
            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                _transform.SetInputType(0, input, 0);
            }

            ChooseOutput();
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            _transform.Dispose();
            throw ex as H264Exception ?? new H264Exception($"the Media Foundation decoder could not be set up: {ex.Message}", ex);
        }
    }

    public string Kind => "mediafoundation";

    public SKBitmap? Decode(ReadOnlySpan<byte> accessUnit)
    {
        MediaFoundation.Ready();
        try
        {
            using (var sample = MediaFoundation.Sample(accessUnit.Length, out var buffer))
            {
                using (buffer)
                {
                    MediaFoundation.Write(buffer, accessUnit);
                }

                sample.SampleTime = _frame++ * 333_333;
                _transform.ProcessInput(0, sample, 0);
            }

            SKBitmap? picture = null;
            while (Output() is { } decoded)
            {
                picture = decoded;
            }

            return picture;
        }
        catch (Exception ex) when (ex is not H264Exception)
        {
            throw new H264Exception($"the Media Foundation decoder failed: {ex.Message}", ex);
        }
    }

    /// <summary>One picture the decoder has ready, or <c>null</c> when it needs more input.</summary>
    private SKBitmap? Output()
    {
        while (true)
        {
            IMFSample? own = null;
            IMFMediaBuffer? ownBuffer = null;
            if (!_providesSamples)
            {
                own = MediaFoundation.Sample(_outputSize, out ownBuffer);
            }

            try
            {
                var data = new OutputDataBuffer { StreamID = 0, Sample = own! };
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                data.Events?.Dispose();
                if (result.Code == MediaFoundation.NeedMoreInput)
                {
                    return null;
                }

                if (result.Code == MediaFoundation.StreamChange)
                {
                    // The first IDR (or a new size) tells the decoder what it is decoding.
                    ChooseOutput();
                    continue;
                }

                result.CheckError();
                var produced = data.Sample;
                if (produced is null)
                {
                    return null;
                }

                try
                {
                    return Convert(produced);
                }
                finally
                {
                    if (!ReferenceEquals(produced, own))
                    {
                        produced.Dispose();
                    }
                }
            }
            finally
            {
                ownBuffer?.Dispose();
                own?.Dispose();
            }
        }
    }

    private SKBitmap? Convert(IMFSample sample)
    {
        if (_width <= 0 || _height <= 0)
        {
            return null;
        }

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var pointer, out _, out var length);
        try
        {
            if (length < Nv12.Size(_stride, _planeHeight))
            {
                return null;
            }

            if (_picture is null || _picture.Width != _width || _picture.Height != _height)
            {
                _picture?.Dispose();
                _picture = new SKBitmap(new SKImageInfo(_width, _height, JpegCodec.PixelFormat, SKAlphaType.Premul));
            }

            Nv12.ToBgra(new ReadOnlySpan<byte>((void*)pointer, length), _stride, _planeHeight, _width, _height,
                new Span<byte>((void*)_picture.GetPixels(), _picture.RowBytes * _height), _picture.RowBytes);
            return _picture;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>Picks NV12 among the decoder's output types and reads its size, stride and display area.</summary>
    private void ChooseOutput()
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (Exception)
            {
                throw new H264Exception("the decoder offers no NV12 output");
            }

            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                {
                    continue;
                }

                _transform.SetOutputType(0, type, 0);
                break;
            }
        }

        using var current = _transform.GetOutputCurrentType(0);
        if (current.GetUInt64(MediaTypeAttributeKeys.FrameSize, out var packed).Success)
        {
            MediaFactory.UnpackSize(packed, out var w, out var h);
            _stride = (int)w;
            _planeHeight = (int)h;
            _width = (int)w & ~1;
            _height = (int)h & ~1;
        }

        if (current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success && (int)stride >= _width)
        {
            _stride = (int)stride;
        }

        // MFVideoArea: OffsetX, OffsetY (MFOffset, 4 bytes each), then the SIZE of the picture.
        var area = new byte[16];
        if (current.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area).Success)
        {
            var cx = BitConverter.ToInt32(area, 8);
            var cy = BitConverter.ToInt32(area, 12);
            if (cx > 0 && cy > 0 && cx <= _stride && cy <= _planeHeight)
            {
                _width = cx & ~1;
                _height = cy & ~1;
            }
        }

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, Math.Max(1, Nv12.Size(Math.Max(_stride, 16), Math.Max(_planeHeight, 16))));
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (Exception)
        {
        }

        _transform.Dispose();
        _picture?.Dispose();
        _picture = null;
    }
}
