using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LabControl.Shared.Video.H264;

/// <summary>
/// The few CoreFoundation, CoreMedia, CoreVideo and VideoToolbox calls the macOS console's
/// H.264 needs (D-91 item 1), through <c>DllImport</c> like <see cref="CoreGraphicsScreenSource"/>
/// — no NuGet package. Every <c>CFTypeRef</c> is an <see cref="IntPtr"/> the caller releases.
/// </summary>
internal static unsafe class VideoToolbox
{
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreMediaPath = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideoPath = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string VideoToolboxPath = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";

    public const uint CodecH264 = 0x61766331;            // 'avc1'
    public const uint PixelFormatBgra = 0x42475241;      // kCVPixelFormatType_32BGRA
    public const int NumberSInt32 = 3;                   // kCFNumberSInt32Type
    public const int NumberFloat64 = 6;                  // kCFNumberFloat64Type
    public const ulong LockReadOnly = 1;                 // kCVPixelBufferLock_ReadOnly
    public const uint BlockBufferAssureMemoryNow = 1;    // kCMBlockBufferAssureMemoryNowFlag

    [StructLayout(LayoutKind.Sequential)]
    public struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;

        public static CMTime Invalid => default;

        public static CMTime Make(long value, int timescale) => new() { Value = value, Timescale = timescale, Flags = 1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DecompressionOutputCallbackRecord
    {
        public IntPtr Callback;
        public IntPtr RefCon;
    }

    // ------------------------------------------------------------------ constants exported as globals

    private static readonly Lazy<IntPtr> CoreFoundationHandle = new(() => NativeLibrary.Load(CoreFoundationPath));
    private static readonly Lazy<IntPtr> CoreVideoHandle = new(() => NativeLibrary.Load(CoreVideoPath));
    private static readonly Lazy<IntPtr> VideoToolboxHandle = new(() => NativeLibrary.Load(VideoToolboxPath));

    /// <summary>The value of a <c>const CFStringRef</c> (or other pointer) a framework exports.</summary>
    private static IntPtr Global(Lazy<IntPtr> library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library.Value, name));

    /// <summary>The address of a struct a framework exports (the CF callback tables).</summary>
    private static IntPtr Address(Lazy<IntPtr> library, string name) => NativeLibrary.GetExport(library.Value, name);

    public static IntPtr VT(string name) => Global(VideoToolboxHandle, name);

    public static IntPtr CV(string name) => Global(CoreVideoHandle, name);

    public static IntPtr BooleanTrue => Global(CoreFoundationHandle, "kCFBooleanTrue");

    public static IntPtr BooleanFalse => Global(CoreFoundationHandle, "kCFBooleanFalse");

    public static IntPtr DictionaryKeyCallBacks => Address(CoreFoundationHandle, "kCFTypeDictionaryKeyCallBacks");

    public static IntPtr DictionaryValueCallBacks => Address(CoreFoundationHandle, "kCFTypeDictionaryValueCallBacks");

    public static IntPtr ArrayCallBacks => Address(CoreFoundationHandle, "kCFTypeArrayCallBacks");

    /// <summary>Whether the frameworks are there at all (macOS only; a test on another OS skips).</summary>
    public static bool Available
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            try
            {
                return VideoToolboxHandle.Value != IntPtr.Zero && CoreFoundationHandle.Value != IntPtr.Zero && CoreVideoHandle.Value != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ CoreFoundation helpers

    public static IntPtr Int32(int value) => CFNumberCreate(IntPtr.Zero, NumberSInt32, &value);

    public static IntPtr Float64(double value) => CFNumberCreate(IntPtr.Zero, NumberFloat64, &value);

    /// <summary>A dictionary of the pairs; the values the caller created are released here, the keys are framework constants.</summary>
    public static IntPtr Dictionary(params (IntPtr Key, IntPtr Value, bool Owned)[] pairs)
    {
        var keys = stackalloc IntPtr[pairs.Length];
        var values = stackalloc IntPtr[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
        {
            keys[i] = pairs[i].Key;
            values[i] = pairs[i].Value;
        }

        var dictionary = CFDictionaryCreate(IntPtr.Zero, keys, values, pairs.Length, DictionaryKeyCallBacks, DictionaryValueCallBacks);
        foreach (var pair in pairs)
        {
            if (pair.Owned)
            {
                Release(pair.Value);
            }
        }

        return dictionary;
    }

    public static IntPtr Array(params IntPtr[] owned)
    {
        IntPtr array;
        fixed (IntPtr* values = owned)
        {
            array = CFArrayCreate(IntPtr.Zero, values, owned.Length, ArrayCallBacks);
        }

        foreach (var value in owned)
        {
            Release(value);
        }

        return array;
    }

    public static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            CFRelease(value);
        }
    }

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, int type, void* value);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationPath)]
    private static extern IntPtr CFArrayCreate(IntPtr allocator, IntPtr* values, nint count, IntPtr callBacks);

    [DllImport(CoreFoundationPath)]
    private static extern void CFRelease(IntPtr value);

    // ------------------------------------------------------------------ VideoToolbox

    [DllImport(VideoToolboxPath)]
    public static extern int VTCompressionSessionCreate(IntPtr allocator, int width, int height, uint codecType, IntPtr encoderSpecification,
        IntPtr sourceImageBufferAttributes, IntPtr compressedDataAllocator,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, uint, IntPtr, void> outputCallback, IntPtr outputCallbackRefCon, out IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);

    [DllImport(VideoToolboxPath)]
    public static extern int VTCompressionSessionPrepareToEncodeFrames(IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern IntPtr VTCompressionSessionGetPixelBufferPool(IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern int VTCompressionSessionEncodeFrame(IntPtr session, IntPtr imageBuffer, CMTime presentationTimeStamp, CMTime duration,
        IntPtr frameProperties, IntPtr sourceFrameRefCon, out uint infoFlags);

    [DllImport(VideoToolboxPath)]
    public static extern int VTCompressionSessionCompleteFrames(IntPtr session, CMTime completeUntilPresentationTimeStamp);

    [DllImport(VideoToolboxPath)]
    public static extern void VTCompressionSessionInvalidate(IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern int VTDecompressionSessionCreate(IntPtr allocator, IntPtr videoFormatDescription, IntPtr videoDecoderSpecification,
        IntPtr destinationImageBufferAttributes, DecompressionOutputCallbackRecord* outputCallback, out IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern int VTDecompressionSessionDecodeFrame(IntPtr session, IntPtr sampleBuffer, uint decodeFlags, IntPtr sourceFrameRefCon, out uint infoFlags);

    [DllImport(VideoToolboxPath)]
    public static extern int VTDecompressionSessionWaitForAsynchronousFrames(IntPtr session);

    [DllImport(VideoToolboxPath)]
    public static extern void VTDecompressionSessionInvalidate(IntPtr session);

    // ------------------------------------------------------------------ CoreMedia

    [DllImport(CoreMediaPath)]
    public static extern IntPtr CMSampleBufferGetDataBuffer(IntPtr sampleBuffer);

    [DllImport(CoreMediaPath)]
    public static extern IntPtr CMSampleBufferGetFormatDescription(IntPtr sampleBuffer);

    [DllImport(CoreMediaPath)]
    public static extern nuint CMBlockBufferGetDataLength(IntPtr blockBuffer);

    [DllImport(CoreMediaPath)]
    public static extern int CMBlockBufferCopyDataBytes(IntPtr blockBuffer, nuint offsetToData, nuint dataLength, void* destination);

    [DllImport(CoreMediaPath)]
    public static extern int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(IntPtr videoDescription, nuint parameterSetIndex,
        out IntPtr parameterSetPointer, out nuint parameterSetSize, out nuint parameterSetCount, out int nalUnitHeaderLength);

    [DllImport(CoreMediaPath)]
    public static extern int CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr allocator, nuint parameterSetCount,
        IntPtr* parameterSetPointers, nuint* parameterSetSizes, int nalUnitHeaderLength, out IntPtr formatDescription);

    [DllImport(CoreMediaPath)]
    public static extern int CMBlockBufferCreateWithMemoryBlock(IntPtr structureAllocator, IntPtr memoryBlock, nuint blockLength,
        IntPtr blockAllocator, IntPtr customBlockSource, nuint offsetToData, nuint dataLength, uint flags, out IntPtr blockBuffer);

    [DllImport(CoreMediaPath)]
    public static extern int CMBlockBufferReplaceDataBytes(void* sourceBytes, IntPtr destinationBuffer, nuint offsetIntoDestination, nuint dataLength);

    [DllImport(CoreMediaPath)]
    public static extern int CMSampleBufferCreateReady(IntPtr allocator, IntPtr dataBuffer, IntPtr formatDescription, nint numSamples,
        nint numSampleTimingEntries, IntPtr sampleTimingArray, nint numSampleSizeEntries, nuint* sampleSizeArray, out IntPtr sampleBuffer);

    // ------------------------------------------------------------------ CoreVideo

    [DllImport(CoreVideoPath)]
    public static extern int CVPixelBufferCreate(IntPtr allocator, nuint width, nuint height, uint pixelFormatType, IntPtr pixelBufferAttributes, out IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern int CVPixelBufferPoolCreatePixelBuffer(IntPtr allocator, IntPtr pixelBufferPool, out IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern int CVPixelBufferLockBaseAddress(IntPtr pixelBuffer, ulong lockFlags);

    [DllImport(CoreVideoPath)]
    public static extern int CVPixelBufferUnlockBaseAddress(IntPtr pixelBuffer, ulong unlockFlags);

    [DllImport(CoreVideoPath)]
    public static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

    [DllImport(CoreVideoPath)]
    public static extern uint CVPixelBufferGetPixelFormatType(IntPtr pixelBuffer);
}
