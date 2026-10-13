using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace ScreenRecorder.Services.GpuEncoding;

internal sealed class AmfNative : IDisposable
{
    private const string LibraryName = "amfrt64.dll";
    private const string InitExportName = "AMFInit";
    private const string QueryVersionExportName = "AMFQueryVersion";

    internal const string AvcComponentId = "AMFVideoEncoderVCE_AVC";

    internal const int AmfOk = 0;
    internal const int AmfEof = 23;
    internal const int AmfRepeat = 24;
    internal const int AmfInputFull = 25;
    internal const int AmfNeedMoreInput = 44;
    internal const int AmfDx11_1 = 111;
    internal const int AmfMemoryDx11 = 3;
    internal const int AmfDataBuffer = 0;
    internal const int AmfSurfaceBgra = 3;

    private readonly IntPtr _library;
    private readonly IntPtr _factory;
    private bool _disposed;

    private AmfNative(IntPtr library, IntPtr factory, ulong runtimeVersion)
    {
        _library = library;
        _factory = factory;
        RuntimeVersion = runtimeVersion;
    }

    public ulong RuntimeVersion { get; }
    internal IntPtr Factory
    {
        get
        {
            ThrowIfDisposed();
            return _factory;
        }
    }

    public static bool TryCreate(out AmfNative? api, out string message)
    {
        api = null;

        var capability = AmfCapabilityProbe.Probe();
        if (!capability.IsAvailable)
        {
            message = capability.Message;
            return false;
        }

        if (!NativeLibrary.TryLoad(LibraryName, out var library))
        {
            message = $"{LibraryName} was not found.";
            return false;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, QueryVersionExportName, out var queryExport) ||
                !NativeLibrary.TryGetExport(library, InitExportName, out var initExport))
            {
                message = "AMF runtime exports were not found.";
                NativeLibrary.Free(library);
                return false;
            }

            var queryVersion =
                Marshal.GetDelegateForFunctionPointer<AmfQueryVersionDelegate>(queryExport);
            var status = queryVersion(out var runtimeVersion);
            if (status != AmfOk)
            {
                message = $"AMFQueryVersion failed: {StatusName(status)} ({status}).";
                NativeLibrary.Free(library);
                return false;
            }

            var init = Marshal.GetDelegateForFunctionPointer<AmfInitDelegate>(initExport);
            status = init(runtimeVersion, out var factory);
            if (status != AmfOk || factory == IntPtr.Zero)
            {
                message = $"AMFInit failed: {StatusName(status)} ({status}).";
                NativeLibrary.Free(library);
                return false;
            }

            api = new AmfNative(library, factory, runtimeVersion);
            message = $"AMF runtime initialized ({FormatVersion(runtimeVersion)}).";
            return true;
        }
        catch (Exception ex)
        {
            message = $"AMF setup failed: {ex.Message}";
            try { NativeLibrary.Free(library); } catch { }
            return false;
        }
    }

    public AmfDx11Probe ProbeD3D11Avc(Device device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        var context = IntPtr.Zero;
        var component = IntPtr.Zero;

        try
        {
            var createContext = GetDelegate<CreateContextDelegate>(_factory, 0);
            var status = createContext(_factory, out context);
            if (status != AmfOk || context == IntPtr.Zero)
                return AmfDx11Probe.Fail("CreateContext", status);

            var initDx11 = GetDelegate<InitDx11Delegate>(context, 18);
            status = initDx11(context, device.NativePointer, AmfDx11_1);
            if (status != AmfOk)
                return AmfDx11Probe.Fail("InitDX11", status);

            var createComponent = GetDelegate<CreateComponentDelegate>(_factory, 1);
            status = createComponent(_factory, context, AvcComponentId, out component);
            if (status != AmfOk || component == IntPtr.Zero)
                return AmfDx11Probe.Fail("CreateComponent(AMFVideoEncoderVCE_AVC)", status);

            var initComponent = GetDelegate<ComponentInitDelegate>(component, 17);
            status = initComponent(component, AmfSurfaceBgra, width, height);
            if (status != AmfOk)
                return AmfDx11Probe.Fail("Component.Init(BGRA)", status);

            return new AmfDx11Probe(true, "AMF DX11 AVC component initializes with BGRA input.");
        }
        finally
        {
            if (component != IntPtr.Zero)
            {
                try { GetDelegate<ComponentTerminateDelegate>(component, 19)(component); } catch { }
                ReleaseInterface(component);
            }

            if (context != IntPtr.Zero)
            {
                try { GetDelegate<ContextTerminateDelegate>(context, 13)(context); } catch { }
                ReleaseInterface(context);
            }
        }
    }

    internal void ThrowIfFailed(int status, string apiName)
    {
        if (status == AmfOk)
            return;

        throw new AmfApiException(apiName, status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_library);
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AmfNative));
    }

    internal static void ReleaseInterface(IntPtr amfInterface)
    {
        if (amfInterface == IntPtr.Zero) return;
        try { GetDelegate<ReleaseDelegate>(amfInterface, 1)(amfInterface); } catch { }
    }

    internal static T GetDelegate<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        if (instance == IntPtr.Zero)
            throw new ArgumentException("AMF interface pointer is null.", nameof(instance));

        var vtbl = Marshal.ReadIntPtr(instance);
        var function = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        if (function == IntPtr.Zero)
            throw new InvalidOperationException($"AMF vtable slot {slot} is null.");

        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    public static string FormatVersion(ulong version)
    {
        var major = (version >> 48) & 0xFFFF;
        var minor = (version >> 32) & 0xFFFF;
        var release = (version >> 16) & 0xFFFF;
        var build = version & 0xFFFF;
        return $"{major}.{minor}.{release}.{build}";
    }

    public static string StatusName(int status) => status switch
    {
        0 => "AMF_OK",
        1 => "AMF_FAIL",
        2 => "AMF_UNEXPECTED",
        3 => "AMF_ACCESS_DENIED",
        4 => "AMF_INVALID_ARG",
        5 => "AMF_OUT_OF_RANGE",
        6 => "AMF_OUT_OF_MEMORY",
        7 => "AMF_INVALID_POINTER",
        8 => "AMF_NO_INTERFACE",
        9 => "AMF_NOT_IMPLEMENTED",
        10 => "AMF_NOT_SUPPORTED",
        11 => "AMF_NOT_FOUND",
        12 => "AMF_ALREADY_INITIALIZED",
        13 => "AMF_NOT_INITIALIZED",
        14 => "AMF_INVALID_FORMAT",
        15 => "AMF_WRONG_STATE",
        16 => "AMF_FILE_NOT_OPEN",
        17 => "AMF_NO_DEVICE",
        18 => "AMF_DIRECTX_FAILED",
        19 => "AMF_OPENCL_FAILED",
        20 => "AMF_GLX_FAILED",
        21 => "AMF_XV_FAILED",
        22 => "AMF_ALSA_FAILED",
        23 => "AMF_EOF",
        24 => "AMF_REPEAT",
        25 => "AMF_INPUT_FULL",
        26 => "AMF_RESOLUTION_CHANGED",
        27 => "AMF_RESOLUTION_UPDATED",
        28 => "AMF_INVALID_DATA_TYPE",
        29 => "AMF_INVALID_RESOLUTION",
        30 => "AMF_CODEC_NOT_SUPPORTED",
        31 => "AMF_SURFACE_FORMAT_NOT_SUPPORTED",
        32 => "AMF_SURFACE_MUST_BE_SHARED",
        33 => "AMF_DECODER_NOT_PRESENT",
        34 => "AMF_DECODER_SURFACE_ALLOCATION_FAILED",
        35 => "AMF_DECODER_NO_FREE_SURFACES",
        36 => "AMF_ENCODER_NOT_PRESENT",
        37 => "AMF_DEM_ERROR",
        38 => "AMF_DEM_PROPERTY_READONLY",
        39 => "AMF_DEM_REMOTE_DISPLAY_CREATE_FAILED",
        40 => "AMF_DEM_START_ENCODING_FAILED",
        41 => "AMF_DEM_QUERY_OUTPUT_FAILED",
        42 => "AMF_TAN_CLIPPING_WAS_REQUIRED",
        43 => "AMF_TAN_UNSUPPORTED_VERSION",
        44 => "AMF_NEED_MORE_INPUT",
        _ => "AMF_UNKNOWN"
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AmfQueryVersionDelegate(out ulong version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AmfInitDelegate(ulong version, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long ReleaseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateContextDelegate(IntPtr factory, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateComponentDelegate(
        IntPtr factory,
        IntPtr context,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        out IntPtr component);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitDx11Delegate(IntPtr context, IntPtr d3d11Device, int dxVersionRequired);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ContextTerminateDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentInitDelegate(IntPtr component, int format, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentTerminateDelegate(IntPtr component);
}

internal readonly record struct AmfDx11Probe(bool CanInitializeAvc, string Message)
{
    public static AmfDx11Probe Fail(string apiName, int status) =>
        new(false, $"{apiName} failed: {AmfNative.StatusName(status)} ({status}).");
}

internal sealed class AmfApiException : InvalidOperationException
{
    public AmfApiException(string apiName, int status)
        : base($"{apiName} failed: {AmfNative.StatusName(status)} ({status}).")
    {
        ApiName = apiName;
        Status = status;
    }

    public string ApiName { get; }
    public int Status { get; }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct AmfVariant
{
    [FieldOffset(0)] public int Type;
    [FieldOffset(8)] public byte BoolValue;
    [FieldOffset(8)] public long Int64Value;
    [FieldOffset(8)] public AmfSize SizeValue;
    [FieldOffset(8)] public AmfRate RateValue;
    [FieldOffset(8)] public IntPtr InterfaceValue;

    public static AmfVariant Bool(bool value) => new()
    {
        Type = 1,
        BoolValue = value ? (byte)1 : (byte)0
    };

    public static AmfVariant Int64(long value) => new()
    {
        Type = 2,
        Int64Value = value
    };

    public static AmfVariant Size(int width, int height) => new()
    {
        Type = 5,
        SizeValue = new AmfSize(width, height)
    };

    public static AmfVariant Rate(uint numerator, uint denominator) => new()
    {
        Type = 7,
        RateValue = new AmfRate(numerator, denominator)
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct AmfSize
{
    public AmfSize(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width;
    public int Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AmfRate
{
    public AmfRate(uint numerator, uint denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public uint Numerator;
    public uint Denominator;
}
