using System.Runtime.InteropServices;

namespace ScreenRecorder.Services.GpuEncoding;

internal sealed unsafe class NvencNative : IDisposable
{
    private const string LibraryName = "nvEncodeAPI64.dll";
    private const string CreateInstanceExportName = "NvEncodeAPICreateInstance";

    private const uint ApiMajor = 12;
    private const uint ApiMinor = 2;
    private const uint ApiVersion = ApiMajor | (ApiMinor << 24);
    private const uint ApiMagic = 0x7u << 28;
    private const uint CurrentStructVersionFlag = 1u << 31;
    private const uint DeviceTypeDirectX = 0;
    private const uint InputResourceTypeDirectX = 0;
    private const uint BufferUsageInputImage = 0;
    private const uint PicStructFrame = 0x01;
    private const uint PicTypeUnknown = 0xFF;
    private const uint TuningInfoLowLatency = 2;
    private const int StatusInvalidCall = 9;
    private const int StatusEncoderNotInitialized = 11;
    private const int StatusNeedMoreInput = 17;

    internal static readonly Guid H264CodecGuid =
        new("6bc82762-4e63-4ca4-aa85-1e50f321f6bf");

    private static readonly Guid PresetP5Guid =
        new("21c6e6b4-297a-4cba-998f-b6cbde72ade3");

    internal const uint BufferFormatArgb = 0x01000000;

    private readonly IntPtr _library;
    private readonly NvEncOpenEncodeSessionExDelegate _openEncodeSessionEx;
    private readonly NvEncGetEncodeGuidCountDelegate _getEncodeGuidCount;
    private readonly NvEncGetEncodeGuidsDelegate _getEncodeGuids;
    private readonly NvEncGetInputFormatCountDelegate _getInputFormatCount;
    private readonly NvEncGetInputFormatsDelegate _getInputFormats;
    private readonly NvEncInitializeEncoderDelegate _initializeEncoder;
    private readonly NvEncDestroyEncoderDelegate _destroyEncoder;
    private readonly NvEncCreateBitstreamBufferDelegate _createBitstreamBuffer;
    private readonly NvEncDestroyBitstreamBufferDelegate _destroyBitstreamBuffer;
    private readonly NvEncEncodePictureDelegate _encodePicture;
    private readonly NvEncLockBitstreamDelegate _lockBitstream;
    private readonly NvEncUnlockBitstreamDelegate _unlockBitstream;
    private readonly NvEncRegisterResourceDelegate _registerResource;
    private readonly NvEncUnregisterResourceDelegate _unregisterResource;
    private readonly NvEncMapInputResourceDelegate _mapInputResource;
    private readonly NvEncUnmapInputResourceDelegate _unmapInputResource;
    private readonly NvEncGetLastErrorStringDelegate? _getLastErrorString;

    private bool _disposed;

    private NvencNative(IntPtr library, NvEncodeApiFunctionList functionList)
    {
        _library = library;
        _openEncodeSessionEx = GetDelegate<NvEncOpenEncodeSessionExDelegate>(
            functionList.NvEncOpenEncodeSessionEx,
            nameof(functionList.NvEncOpenEncodeSessionEx));
        _getEncodeGuidCount = GetDelegate<NvEncGetEncodeGuidCountDelegate>(
            functionList.NvEncGetEncodeGuidCount,
            nameof(functionList.NvEncGetEncodeGuidCount));
        _getEncodeGuids = GetDelegate<NvEncGetEncodeGuidsDelegate>(
            functionList.NvEncGetEncodeGuids,
            nameof(functionList.NvEncGetEncodeGuids));
        _getInputFormatCount = GetDelegate<NvEncGetInputFormatCountDelegate>(
            functionList.NvEncGetInputFormatCount,
            nameof(functionList.NvEncGetInputFormatCount));
        _getInputFormats = GetDelegate<NvEncGetInputFormatsDelegate>(
            functionList.NvEncGetInputFormats,
            nameof(functionList.NvEncGetInputFormats));
        _initializeEncoder = GetDelegate<NvEncInitializeEncoderDelegate>(
            functionList.NvEncInitializeEncoder,
            nameof(functionList.NvEncInitializeEncoder));
        _destroyEncoder = GetDelegate<NvEncDestroyEncoderDelegate>(
            functionList.NvEncDestroyEncoder,
            nameof(functionList.NvEncDestroyEncoder));
        _createBitstreamBuffer = GetDelegate<NvEncCreateBitstreamBufferDelegate>(
            functionList.NvEncCreateBitstreamBuffer,
            nameof(functionList.NvEncCreateBitstreamBuffer));
        _destroyBitstreamBuffer = GetDelegate<NvEncDestroyBitstreamBufferDelegate>(
            functionList.NvEncDestroyBitstreamBuffer,
            nameof(functionList.NvEncDestroyBitstreamBuffer));
        _encodePicture = GetDelegate<NvEncEncodePictureDelegate>(
            functionList.NvEncEncodePicture,
            nameof(functionList.NvEncEncodePicture));
        _lockBitstream = GetDelegate<NvEncLockBitstreamDelegate>(
            functionList.NvEncLockBitstream,
            nameof(functionList.NvEncLockBitstream));
        _unlockBitstream = GetDelegate<NvEncUnlockBitstreamDelegate>(
            functionList.NvEncUnlockBitstream,
            nameof(functionList.NvEncUnlockBitstream));
        _registerResource = GetDelegate<NvEncRegisterResourceDelegate>(
            functionList.NvEncRegisterResource,
            nameof(functionList.NvEncRegisterResource));
        _unregisterResource = GetDelegate<NvEncUnregisterResourceDelegate>(
            functionList.NvEncUnregisterResource,
            nameof(functionList.NvEncUnregisterResource));
        _mapInputResource = GetDelegate<NvEncMapInputResourceDelegate>(
            functionList.NvEncMapInputResource,
            nameof(functionList.NvEncMapInputResource));
        _unmapInputResource = GetDelegate<NvEncUnmapInputResourceDelegate>(
            functionList.NvEncUnmapInputResource,
            nameof(functionList.NvEncUnmapInputResource));

        if (functionList.NvEncGetLastErrorString != IntPtr.Zero)
            _getLastErrorString =
                Marshal.GetDelegateForFunctionPointer<NvEncGetLastErrorStringDelegate>(
                    functionList.NvEncGetLastErrorString);
    }

    public static bool TryCreate(out NvencNative? api, out string message)
    {
        api = null;
        var capability = NvencCapabilityProbe.Probe();
        if (!capability.IsAvailable)
        {
            message = capability.Message;
            return false;
        }

        if (!SupportsCompileTimeApi(capability))
        {
            message =
                $"Driver reports NVENC API {capability.ApiVersionText}; " +
                $"this direct path is compiled for {ApiMajor}.{ApiMinor}.";
            return false;
        }

        if (!NativeLibrary.TryLoad(LibraryName, out var library))
        {
            message = $"{LibraryName} was not found.";
            return false;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, CreateInstanceExportName, out var createExport))
            {
                message = $"{CreateInstanceExportName} export was not found.";
                NativeLibrary.Free(library);
                return false;
            }

            var createInstance =
                Marshal.GetDelegateForFunctionPointer<NvEncodeApiCreateInstanceDelegate>(createExport);
            var functionList = new NvEncodeApiFunctionList
            {
                Version = StructVersion(2)
            };

            var status = createInstance(&functionList);
            if (status != 0)
            {
                message = $"{CreateInstanceExportName} failed: {StatusName(status)} ({status}).";
                NativeLibrary.Free(library);
                return false;
            }

            api = new NvencNative(library, functionList);
            message = "NvEncodeAPI function table is ready.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"NvEncodeAPI function table setup failed: {ex.Message}";
            try { NativeLibrary.Free(library); } catch { }
            return false;
        }
    }

    public IntPtr OpenD3D11Session(IntPtr d3d11Device)
    {
        ThrowIfDisposed();
        if (d3d11Device == IntPtr.Zero)
            throw new ArgumentException("D3D11 device pointer is null.", nameof(d3d11Device));

        var openParams = new NvEncOpenEncodeSessionExParams
        {
            Version = StructVersion(1),
            DeviceType = DeviceTypeDirectX,
            Device = d3d11Device,
            ApiVersion = ApiVersion
        };

        var status = _openEncodeSessionEx(&openParams, out var encoder);
        if (status != 0)
            throw new InvalidOperationException($"NvEncOpenEncodeSessionEx failed: {StatusName(status)} ({status}).");

        if (encoder == IntPtr.Zero)
            throw new InvalidOperationException("NvEncOpenEncodeSessionEx returned a null encoder handle.");

        return encoder;
    }

    public IReadOnlyList<Guid> GetEncodeGuids(IntPtr encoder)
    {
        ThrowIfDisposed();
        var status = _getEncodeGuidCount(encoder, out var count);
        ThrowIfFailed(status, encoder, "NvEncGetEncodeGUIDCount");
        if (count == 0) return Array.Empty<Guid>();

        var guids = new Guid[count];
        fixed (Guid* guidsPtr = guids)
        {
            status = _getEncodeGuids(encoder, (IntPtr)guidsPtr, count, out var written);
            ThrowIfFailed(status, encoder, "NvEncGetEncodeGUIDs");
            if (written < count)
                Array.Resize(ref guids, checked((int)written));
        }

        return guids;
    }

    public IReadOnlyList<uint> GetInputFormats(IntPtr encoder, Guid codecGuid)
    {
        ThrowIfDisposed();
        var status = _getInputFormatCount(encoder, codecGuid, out var count);
        ThrowIfFailed(status, encoder, "NvEncGetInputFormatCount");
        if (count == 0) return Array.Empty<uint>();

        var formats = new uint[count];
        fixed (uint* formatsPtr = formats)
        {
            status = _getInputFormats(encoder, codecGuid, (IntPtr)formatsPtr, count, out var written);
            ThrowIfFailed(status, encoder, "NvEncGetInputFormats");
            if (written < count)
                Array.Resize(ref formats, checked((int)written));
        }

        return formats;
    }

    public void InitializeH264Encoder(IntPtr encoder, int width, int height, int fps)
    {
        ThrowIfDisposed();
        if (encoder == IntPtr.Zero)
            throw new ArgumentException("NVENC encoder handle is null.", nameof(encoder));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Encode width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Encode height must be positive.");

        var initialize = new NvEncInitializeParams
        {
            Version = CurrentStructVersion(7),
            EncodeGuid = H264CodecGuid,
            PresetGuid = PresetP5Guid,
            EncodeWidth = checked((uint)width),
            EncodeHeight = checked((uint)height),
            DarWidth = checked((uint)width),
            DarHeight = checked((uint)height),
            FrameRateNum = checked((uint)Math.Max(1, fps)),
            FrameRateDen = 1,
            EnableEncodeAsync = 0,
            EnablePtd = 1,
            MaxEncodeWidth = checked((uint)width),
            MaxEncodeHeight = checked((uint)height),
            TuningInfo = TuningInfoLowLatency,
            BufferFormat = BufferFormatArgb
        };

        var status = _initializeEncoder(encoder, &initialize);
        ThrowIfFailed(status, encoder, "NvEncInitializeEncoder");
    }

    public NvencRegisteredResource RegisterD3D11Texture(
        IntPtr encoder,
        IntPtr texture,
        int width,
        int height,
        uint bufferFormat = BufferFormatArgb)
    {
        ThrowIfDisposed();
        if (encoder == IntPtr.Zero)
            throw new ArgumentException("NVENC encoder handle is null.", nameof(encoder));
        if (texture == IntPtr.Zero)
            throw new ArgumentException("D3D11 texture pointer is null.", nameof(texture));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Texture height must be positive.");

        var register = new NvEncRegisterResourceParams
        {
            Version = StructVersion(5),
            ResourceType = InputResourceTypeDirectX,
            Width = checked((uint)width),
            Height = checked((uint)height),
            Pitch = 0,
            SubResourceIndex = 0,
            ResourceToRegister = texture,
            BufferFormat = bufferFormat,
            BufferUsage = BufferUsageInputImage
        };

        var status = _registerResource(encoder, &register);
        ThrowIfFailed(status, encoder, "NvEncRegisterResource");

        if (register.RegisteredResource == IntPtr.Zero)
            throw new InvalidOperationException("NvEncRegisterResource returned a null registered resource.");

        return new NvencRegisteredResource(register.RegisteredResource, width, height, bufferFormat);
    }

    public NvencMappedResource MapInputResource(IntPtr encoder, NvencRegisteredResource registeredResource)
    {
        ThrowIfDisposed();
        if (encoder == IntPtr.Zero)
            throw new ArgumentException("NVENC encoder handle is null.", nameof(encoder));
        if (registeredResource.Handle == IntPtr.Zero)
            throw new ArgumentException("Registered resource handle is null.", nameof(registeredResource));

        var map = new NvEncMapInputResourceParams
        {
            Version = StructVersion(4),
            RegisteredResource = registeredResource.Handle
        };

        var status = _mapInputResource(encoder, &map);
        ThrowIfFailed(status, encoder, "NvEncMapInputResource");

        if (map.MappedResource == IntPtr.Zero)
            throw new InvalidOperationException("NvEncMapInputResource returned a null mapped resource.");

        return new NvencMappedResource(map.MappedResource, map.MappedBufferFormat);
    }

    public void UnmapInputResource(IntPtr encoder, NvencMappedResource mappedResource)
    {
        if (mappedResource.Handle == IntPtr.Zero || _disposed) return;

        var status = _unmapInputResource(encoder, mappedResource.Handle);
        ThrowIfFailed(status, encoder, "NvEncUnmapInputResource");
    }

    public void UnregisterResource(IntPtr encoder, NvencRegisteredResource registeredResource)
    {
        if (registeredResource.Handle == IntPtr.Zero || _disposed) return;

        var status = _unregisterResource(encoder, registeredResource.Handle);
        if (status != 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NVENC direct] NvEncUnregisterResource: {StatusName(status)} ({status})");
        }
    }

    public NvencBitstreamBuffer CreateBitstreamBuffer(IntPtr encoder, int sizeBytes = 0)
    {
        ThrowIfDisposed();
        if (encoder == IntPtr.Zero)
            throw new ArgumentException("NVENC encoder handle is null.", nameof(encoder));
        if (sizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Bitstream buffer size cannot be negative.");

        var create = new NvEncCreateBitstreamBufferParams
        {
            Version = StructVersion(1),
            Size = checked((uint)sizeBytes)
        };

        var status = _createBitstreamBuffer(encoder, &create);
        ThrowIfFailed(status, encoder, "NvEncCreateBitstreamBuffer");

        if (create.BitstreamBuffer == IntPtr.Zero)
            throw new InvalidOperationException("NvEncCreateBitstreamBuffer returned a null bitstream buffer.");

        return new NvencBitstreamBuffer(create.BitstreamBuffer, create.BitstreamBufferPtr);
    }

    public void DestroyBitstreamBuffer(IntPtr encoder, NvencBitstreamBuffer buffer)
    {
        if (buffer.Handle == IntPtr.Zero || _disposed) return;

        var status = _destroyBitstreamBuffer(encoder, buffer.Handle);
        if (status != 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NVENC direct] NvEncDestroyBitstreamBuffer: {StatusName(status)} ({status})");
        }
    }

    public NvencEncodedPacketInfo EncodeMappedTexture(
        IntPtr encoder,
        NvencMappedResource mappedResource,
        NvencBitstreamBuffer bitstreamBuffer,
        int width,
        int height,
        uint frameIndex,
        TimeSpan presentationTime,
        TimeSpan duration)
    {
        ThrowIfDisposed();
        if (encoder == IntPtr.Zero)
            throw new ArgumentException("NVENC encoder handle is null.", nameof(encoder));
        if (mappedResource.Handle == IntPtr.Zero)
            throw new ArgumentException("Mapped input resource handle is null.", nameof(mappedResource));
        if (bitstreamBuffer.Handle == IntPtr.Zero)
            throw new ArgumentException("Bitstream buffer handle is null.", nameof(bitstreamBuffer));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Input width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Input height must be positive.");

        var pic = new NvEncPicParams
        {
            Version = CurrentStructVersion(7),
            InputWidth = checked((uint)width),
            InputHeight = checked((uint)height),
            InputPitch = checked((uint)width),
            FrameIdx = frameIndex,
            InputTimeStamp = checked((ulong)Math.Max(0, presentationTime.Ticks)),
            InputDuration = checked((ulong)Math.Max(0, duration.Ticks)),
            InputBuffer = mappedResource.Handle,
            OutputBitstream = bitstreamBuffer.Handle,
            BufferFmt = mappedResource.BufferFormat,
            PictureStruct = PicStructFrame,
            PictureType = PicTypeUnknown
        };

        var status = _encodePicture(encoder, &pic);
        if (status == StatusNeedMoreInput)
            return NvencEncodedPacketInfo.NoPacket(frameIndex, presentationTime, "NVENC accepted the frame but needs more input before emitting a packet.");

        ThrowIfFailed(status, encoder, "NvEncEncodePicture");

        var lockParams = new NvEncLockBitstreamParams
        {
            Version = CurrentStructVersion(2),
            OutputBitstream = bitstreamBuffer.Handle
        };

        var locked = false;
        try
        {
            status = _lockBitstream(encoder, &lockParams);
            ThrowIfFailed(status, encoder, "NvEncLockBitstream");
            locked = true;

            var sizeBytes = checked((int)Math.Min(lockParams.BitstreamSizeInBytes, int.MaxValue));
            byte[]? bitstream = null;
            if (sizeBytes > 0 && lockParams.BitstreamBufferPtr != IntPtr.Zero)
            {
                bitstream = new byte[sizeBytes];
                Marshal.Copy(lockParams.BitstreamBufferPtr, bitstream, 0, sizeBytes);
            }

            return new NvencEncodedPacketInfo(
                true,
                sizeBytes,
                lockParams.BitstreamBufferPtr,
                bitstream,
                lockParams.OutputTimeStamp,
                lockParams.OutputDuration,
                lockParams.FrameIdx,
                lockParams.HwEncodeStatus,
                NvencPicTypeName(lockParams.PictureType),
                null);
        }
        finally
        {
            if (locked)
            {
                var unlockStatus = _unlockBitstream(encoder, bitstreamBuffer.Handle);
                ThrowIfFailed(unlockStatus, encoder, "NvEncUnlockBitstream");
            }
        }
    }

    public void AssertPacketApiReady()
    {
        ThrowIfDisposed();
        _ = _encodePicture;
        _ = _lockBitstream;
        _ = _unlockBitstream;
    }

    public void DestroyEncoder(IntPtr encoder)
    {
        if (encoder == IntPtr.Zero || _disposed) return;
        var status = _destroyEncoder(encoder);
        if (status != 0)
            System.Diagnostics.Debug.WriteLine($"[NVENC direct] NvEncDestroyEncoder: {StatusName(status)} ({status})");
    }

    public static string BufferFormatName(uint format) => format switch
    {
        0x00000001 => "NV12",
        0x00000010 => "YV12",
        0x00000100 => "IYUV",
        0x00001000 => "YUV444",
        0x00010000 => "YUV420_10BIT",
        0x00100000 => "YUV444_10BIT",
        0x01000000 => "ARGB",
        0x02000000 => "ARGB10",
        0x04000000 => "AYUV",
        0x10000000 => "ABGR",
        0x20000000 => "ABGR10",
        0x40000000 => "U8",
        _ => $"0x{format:X8}"
    };

    public static string NvencPicTypeName(uint pictureType) => pictureType switch
    {
        0x0 => "P",
        0x1 => "B",
        0x2 => "I",
        0x3 => "IDR",
        0x4 => "BI",
        0x5 => "SKIPPED",
        0x6 => "INTRA_REFRESH",
        0x7 => "NONREF_P",
        0x8 => "SWITCH",
        0xFF => "UNKNOWN",
        _ => $"0x{pictureType:X}"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_library);
    }

    private void ThrowIfFailed(int status, IntPtr encoder, string apiName)
    {
        if (status == 0) return;

        var lastError = GetLastErrorString(encoder);
        throw new NvencApiException(apiName, status, StatusName(status), lastError);
    }

    private string? GetLastErrorString(IntPtr encoder)
    {
        if (_getLastErrorString == null || encoder == IntPtr.Zero) return null;
        try
        {
            var ptr = _getLastErrorString(encoder);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NvencNative));
    }

    private static bool SupportsCompileTimeApi(GpuEncoderCapability capability)
    {
        var major = capability.MaxSupportedApiMajor;
        var minor = capability.MaxSupportedApiMinor;
        return major > ApiMajor || (major == ApiMajor && minor >= ApiMinor);
    }

    private static uint StructVersion(uint version) =>
        ApiVersion | (version << 16) | ApiMagic;

    private static uint CurrentStructVersion(uint version) =>
        StructVersion(version) | CurrentStructVersionFlag;

    private static T GetDelegate<T>(IntPtr pointer, string name)
        where T : Delegate
    {
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException($"NvEncodeAPI function pointer is null: {name}.");

        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    internal static string StatusName(int status) => status switch
    {
        0 => "NV_ENC_SUCCESS",
        1 => "NV_ENC_ERR_NO_ENCODE_DEVICE",
        2 => "NV_ENC_ERR_UNSUPPORTED_DEVICE",
        3 => "NV_ENC_ERR_INVALID_ENCODERDEVICE",
        4 => "NV_ENC_ERR_INVALID_DEVICE",
        5 => "NV_ENC_ERR_DEVICE_NOT_EXIST",
        6 => "NV_ENC_ERR_INVALID_PTR",
        7 => "NV_ENC_ERR_INVALID_EVENT",
        8 => "NV_ENC_ERR_INVALID_PARAM",
        9 => "NV_ENC_ERR_INVALID_CALL",
        10 => "NV_ENC_ERR_OUT_OF_MEMORY",
        11 => "NV_ENC_ERR_ENCODER_NOT_INITIALIZED",
        12 => "NV_ENC_ERR_UNSUPPORTED_PARAM",
        13 => "NV_ENC_ERR_LOCK_BUSY",
        14 => "NV_ENC_ERR_NOT_ENOUGH_BUFFER",
        15 => "NV_ENC_ERR_INVALID_VERSION",
        16 => "NV_ENC_ERR_MAP_FAILED",
        17 => "NV_ENC_ERR_NEED_MORE_INPUT",
        18 => "NV_ENC_ERR_ENCODER_BUSY",
        19 => "NV_ENC_ERR_EVENT_NOT_REGISTERD",
        20 => "NV_ENC_ERR_GENERIC",
        21 => "NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY",
        22 => "NV_ENC_ERR_UNIMPLEMENTED",
        23 => "NV_ENC_ERR_RESOURCE_REGISTER_FAILED",
        24 => "NV_ENC_ERR_RESOURCE_NOT_REGISTERED",
        25 => "NV_ENC_ERR_RESOURCE_NOT_MAPPED",
        26 => "NV_ENC_ERR_NEED_MORE_OUTPUT",
        _ => "NV_ENC_ERR_UNKNOWN"
    };

    internal static bool RequiresInitializedEncoderForResourceRegistration(int status) =>
        status is StatusInvalidCall or StatusEncoderNotInitialized;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncodeApiCreateInstanceDelegate(NvEncodeApiFunctionList* functionList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncOpenEncodeSessionExDelegate(
        NvEncOpenEncodeSessionExParams* openSessionExParams,
        out IntPtr encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncGetEncodeGuidCountDelegate(IntPtr encoder, out uint encodeGuidCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncGetEncodeGuidsDelegate(
        IntPtr encoder,
        IntPtr guids,
        uint guidArraySize,
        out uint guidCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncGetInputFormatCountDelegate(
        IntPtr encoder,
        Guid encodeGuid,
        out uint inputFormatCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncGetInputFormatsDelegate(
        IntPtr encoder,
        Guid encodeGuid,
        IntPtr inputFormats,
        uint inputFormatArraySize,
        out uint inputFormatCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncInitializeEncoderDelegate(
        IntPtr encoder,
        NvEncInitializeParams* initializeParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncRegisterResourceDelegate(
        IntPtr encoder,
        NvEncRegisterResourceParams* registerResourceParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncUnregisterResourceDelegate(
        IntPtr encoder,
        IntPtr registeredResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncMapInputResourceDelegate(
        IntPtr encoder,
        NvEncMapInputResourceParams* mapInputResourceParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncUnmapInputResourceDelegate(
        IntPtr encoder,
        IntPtr mappedInputBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncCreateBitstreamBufferDelegate(
        IntPtr encoder,
        NvEncCreateBitstreamBufferParams* createBitstreamBufferParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncDestroyBitstreamBufferDelegate(
        IntPtr encoder,
        IntPtr bitstreamBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncEncodePictureDelegate(
        IntPtr encoder,
        NvEncPicParams* encodePicParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncLockBitstreamDelegate(
        IntPtr encoder,
        NvEncLockBitstreamParams* lockBitstreamBufferParams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncUnlockBitstreamDelegate(
        IntPtr encoder,
        IntPtr bitstreamBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncDestroyEncoderDelegate(IntPtr encoder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr NvEncGetLastErrorStringDelegate(IntPtr encoder);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncOpenEncodeSessionExParams
    {
        public uint Version;
        public uint DeviceType;
        public IntPtr Device;
        public IntPtr Reserved;
        public uint ApiVersion;
        public fixed uint Reserved1[253];
        public fixed long Reserved2[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncInitializeParams
    {
        public uint Version;
        public Guid EncodeGuid;
        public Guid PresetGuid;
        public uint EncodeWidth;
        public uint EncodeHeight;
        public uint DarWidth;
        public uint DarHeight;
        public uint FrameRateNum;
        public uint FrameRateDen;
        public uint EnableEncodeAsync;
        public uint EnablePtd;
        public uint InitFlags;
        public uint PrivDataSize;
        public uint Reserved;
        public IntPtr PrivData;
        public IntPtr EncodeConfig;
        public uint MaxEncodeWidth;
        public uint MaxEncodeHeight;
        public fixed uint MaxMeHintCountsPerBlock[8];
        public uint TuningInfo;
        public uint BufferFormat;
        public uint NumStateBuffers;
        public uint OutputStatsLevel;
        public fixed uint Reserved1[284];
        public fixed long Reserved2[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncRegisterResourceParams
    {
        public uint Version;
        public uint ResourceType;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public uint SubResourceIndex;
        public IntPtr ResourceToRegister;
        public IntPtr RegisteredResource;
        public uint BufferFormat;
        public uint BufferUsage;
        public IntPtr InputFencePoint;
        public fixed uint ChromaOffset[2];
        public fixed uint Reserved1[246];
        public fixed long Reserved2[61];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncMapInputResourceParams
    {
        public uint Version;
        public uint SubResourceIndex;
        public IntPtr InputResource;
        public IntPtr RegisteredResource;
        public IntPtr MappedResource;
        public uint MappedBufferFormat;
        public fixed uint Reserved1[251];
        public fixed long Reserved2[63];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncCreateBitstreamBufferParams
    {
        public uint Version;
        public uint Size;
        public uint MemoryHeap;
        public uint Reserved;
        public IntPtr BitstreamBuffer;
        public IntPtr BitstreamBufferPtr;
        public fixed uint Reserved1[58];
        public fixed long Reserved2[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncPicParams
    {
        public uint Version;
        public uint InputWidth;
        public uint InputHeight;
        public uint InputPitch;
        public uint EncodePicFlags;
        public uint FrameIdx;
        public ulong InputTimeStamp;
        public ulong InputDuration;
        public IntPtr InputBuffer;
        public IntPtr OutputBitstream;
        public IntPtr CompletionEvent;
        public uint BufferFmt;
        public uint PictureStruct;
        public uint PictureType;
        public fixed uint CodecPicParams[256];
        public fixed uint MeHintCountsPerBlock[8];
        public IntPtr MeExternalHints;
        public fixed uint Reserved2[7];
        public fixed long Reserved5[2];
        public IntPtr QpDeltaMap;
        public uint QpDeltaMapSize;
        public uint ReservedBitFields;
        public fixed ushort MeHintRefPicDist[2];
        public uint Reserved4;
        public IntPtr AlphaBuffer;
        public IntPtr MeExternalSbHints;
        public uint MeSbHintsCount;
        public uint StateBufferIdx;
        public IntPtr OutputReconBuffer;
        public fixed uint Reserved3[284];
        public fixed long Reserved6[57];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncLockBitstreamParams
    {
        public uint Version;
        public uint LockFlags;
        public IntPtr OutputBitstream;
        public IntPtr SliceOffsets;
        public uint FrameIdx;
        public uint HwEncodeStatus;
        public uint NumSlices;
        public uint BitstreamSizeInBytes;
        public ulong OutputTimeStamp;
        public ulong OutputDuration;
        public IntPtr BitstreamBufferPtr;
        public uint PictureType;
        public uint PictureStruct;
        public uint FrameAvgQp;
        public uint FrameSatd;
        public uint LtrFrameIdx;
        public uint LtrFrameBitmap;
        public uint TemporalId;
        public uint IntraMbCount;
        public uint InterMbCount;
        public int AverageMvx;
        public int AverageMvy;
        public uint AlphaLayerSizeInBytes;
        public uint OutputStatsPtrSize;
        public uint Reserved;
        public IntPtr OutputStatsPtr;
        public uint FrameIdxDisplay;
        public fixed uint Reserved1[219];
        public fixed long Reserved2[63];
        public fixed uint ReservedInternal[8];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NvEncodeApiFunctionList
    {
        public uint Version;
        public uint Reserved;
        public IntPtr NvEncOpenEncodeSession;
        public IntPtr NvEncGetEncodeGuidCount;
        public IntPtr NvEncGetEncodeProfileGuidCount;
        public IntPtr NvEncGetEncodeProfileGuids;
        public IntPtr NvEncGetEncodeGuids;
        public IntPtr NvEncGetInputFormatCount;
        public IntPtr NvEncGetInputFormats;
        public IntPtr NvEncGetEncodeCaps;
        public IntPtr NvEncGetEncodePresetCount;
        public IntPtr NvEncGetEncodePresetGuids;
        public IntPtr NvEncGetEncodePresetConfig;
        public IntPtr NvEncInitializeEncoder;
        public IntPtr NvEncCreateInputBuffer;
        public IntPtr NvEncDestroyInputBuffer;
        public IntPtr NvEncCreateBitstreamBuffer;
        public IntPtr NvEncDestroyBitstreamBuffer;
        public IntPtr NvEncEncodePicture;
        public IntPtr NvEncLockBitstream;
        public IntPtr NvEncUnlockBitstream;
        public IntPtr NvEncLockInputBuffer;
        public IntPtr NvEncUnlockInputBuffer;
        public IntPtr NvEncGetEncodeStats;
        public IntPtr NvEncGetSequenceParams;
        public IntPtr NvEncRegisterAsyncEvent;
        public IntPtr NvEncUnregisterAsyncEvent;
        public IntPtr NvEncMapInputResource;
        public IntPtr NvEncUnmapInputResource;
        public IntPtr NvEncDestroyEncoder;
        public IntPtr NvEncInvalidateRefFrames;
        public IntPtr NvEncOpenEncodeSessionEx;
        public IntPtr NvEncRegisterResource;
        public IntPtr NvEncUnregisterResource;
        public IntPtr NvEncReconfigureEncoder;
        public IntPtr Reserved1;
        public IntPtr NvEncCreateMvBuffer;
        public IntPtr NvEncDestroyMvBuffer;
        public IntPtr NvEncRunMotionEstimationOnly;
        public IntPtr NvEncGetLastErrorString;
        public IntPtr NvEncSetIoCudaStreams;
        public IntPtr NvEncGetEncodePresetConfigEx;
        public IntPtr NvEncGetSequenceParamEx;
        public IntPtr NvEncRestoreEncoderState;
        public IntPtr NvEncLookaheadPicture;
        public fixed long Reserved2[275];
    }
}

internal readonly record struct NvencRegisteredResource(
    IntPtr Handle,
    int Width,
    int Height,
    uint BufferFormat);

internal readonly record struct NvencMappedResource(
    IntPtr Handle,
    uint BufferFormat);

internal readonly record struct NvencBitstreamBuffer(
    IntPtr Handle,
    IntPtr HostPointer);

internal readonly record struct NvencEncodedPacketInfo(
    bool HasBitstream,
    int SizeBytes,
    IntPtr DataPointer,
    byte[]? Bitstream,
    ulong OutputTimeStamp,
    ulong OutputDuration,
    uint FrameIndex,
    uint HardwareStatus,
    string PictureType,
    string? Message)
{
    public static NvencEncodedPacketInfo NoPacket(
        uint frameIndex,
        TimeSpan presentationTime,
        string message) =>
        new(
            false,
            0,
            IntPtr.Zero,
            null,
            checked((ulong)Math.Max(0, presentationTime.Ticks)),
            0,
            frameIndex,
            0,
            "",
            message);
}
