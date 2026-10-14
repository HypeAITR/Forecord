using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder.Services.GpuEncoding;

public sealed class AmfGpuEncoder : IGpuEncoder
{
    private const long UsageTranscoding = 0;
    private const long ProfileHigh = 100;
    private const long RateControlConstantQp = 0;
    private const long QualityPresetQuality = 2;
    private const long ForcePictureTypeIdr = 2;

    public string BackendName => "amf-direct";
    public GpuEncoderCapability Capability { get; }
    public bool IsInitialized { get; private set; }
    internal event Action<AmfEncodedPacketInfo>? PacketEncoded;

    private AmfNative? _api;
    private D3D11Device? _device;
    private IntPtr _context;
    private IntPtr _component;
    private Texture2D? _sharedInputTexture;
    private int _width;
    private int _height;
    private TimeSpan _frameDuration = TimeSpan.FromMilliseconds(16.6667);
    private long _frameIndex;
    private bool _forceHeadersOnNextFrame = true;
    private byte[]? _extraData;

    public AmfGpuEncoder()
        : this(AmfCapabilityProbe.Probe())
    {
    }

    internal AmfGpuEncoder(GpuEncoderCapability capability)
    {
        Capability = capability;
    }

    public void Initialize(D3D11Device device, GpuEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Capability.IsAvailable)
            throw new InvalidOperationException(Capability.Message);

        if (!AmfNative.TryCreate(out var api, out var message) || api == null)
            throw new InvalidOperationException(message);

        try
        {
            _api = api;
            _device = device;
            _width = settings.Width;
            _height = settings.Height;
            _context = CreateD3D11Context(api, device);
            _component = CreateAvcComponent(api, _context);

            ConfigureH264Encoder(_component, settings);

            var initComponent = AmfNative.GetDelegate<ComponentInitDelegate>(_component, 17);
            var status = initComponent(_component, AmfNative.AmfSurfaceBgra, settings.Width, settings.Height);
            api.ThrowIfFailed(status, "AMFComponent.Init(BGRA)");

            _sharedInputTexture = CreateSharedInputTexture(device, settings.Width, settings.Height);
            _frameDuration = TimeSpan.FromSeconds(1.0 / Math.Max(1, settings.Fps));
            _extraData = TryReadBufferProperty(_component, "ExtraData");
            if (_extraData is { Length: > 0 })
                Debug.WriteLine($"[AMF direct] captured AVC extradata bytes={_extraData.Length}");

            IsInitialized = true;
            Debug.WriteLine("[AMF direct] H.264 encoder initialized for own-mux packet path.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void EncodeTexture(Texture2D texture, TimeSpan presentationTime, Action<Texture2D>? postCopyOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!IsInitialized || _api == null || _device == null || _sharedInputTexture == null || _context == IntPtr.Zero || _component == IntPtr.Zero)
            throw new InvalidOperationException("AMF direct encoder is not initialized.");

        var frameIndex = Interlocked.Increment(ref _frameIndex) - 1;
        IntPtr surface = IntPtr.Zero;

        try
        {
            _device.ImmediateContext.CopyResource(texture, _sharedInputTexture);
            postCopyOverlay?.Invoke(_sharedInputTexture);
            surface = CreateSurfaceFromD3D11Texture(_sharedInputTexture);

            SetDataTiming(surface, presentationTime, _frameDuration);
            if (_forceHeadersOnNextFrame)
            {
                TrySetInt64(surface, "ForcePictureType", ForcePictureTypeIdr);
                TrySetBool(surface, "InsertSPS", true);
                TrySetBool(surface, "InsertPPS", true);
                _forceHeadersOnNextFrame = false;
            }

            var submit = AmfNative.GetDelegate<ComponentSubmitInputDelegate>(_component, 22);
            var status = submit(_component, surface);
            for (var retry = 0;
                 (status == AmfNative.AmfInputFull || status == AmfNative.AmfRepeat) && retry < 15;
                 retry++)
            {
                DrainAvailableOutput(waitForPacket: false, cancellationToken: default);
                if (retry > 0)
                    Thread.Sleep(1);
                status = submit(_component, surface);
            }

            if (status != AmfNative.AmfOk && status != AmfNative.AmfNeedMoreInput)
                _api.ThrowIfFailed(status, "AMFComponent.SubmitInput");

            DrainAvailableOutput(waitForPacket: false, cancellationToken: default);
        }
        finally
        {
            if (surface != IntPtr.Zero)
                AmfNative.ReleaseInterface(surface);
        }
    }

    public Task FinishAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInitialized || _api == null || _component == IntPtr.Zero)
            return Task.CompletedTask;

        var drain = AmfNative.GetDelegate<ComponentDrainDelegate>(_component, 20);
        var status = drain(_component);
        if (status != AmfNative.AmfOk && status != AmfNative.AmfEof)
            _api.ThrowIfFailed(status, "AMFComponent.Drain");

        DrainAvailableOutput(waitForPacket: true, cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _sharedInputTexture?.Dispose();
        _sharedInputTexture = null;

        if (_component != IntPtr.Zero)
        {
            try { AmfNative.GetDelegate<ComponentTerminateDelegate>(_component, 19)(_component); } catch { }
            AmfNative.ReleaseInterface(_component);
            _component = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            try { AmfNative.GetDelegate<ContextTerminateDelegate>(_context, 13)(_context); } catch { }
            AmfNative.ReleaseInterface(_context);
            _context = IntPtr.Zero;
        }

        _api?.Dispose();
        _api = null;
        _device = null;
        _width = 0;
        _height = 0;
        IsInitialized = false;
        _frameIndex = 0;
        _forceHeadersOnNextFrame = true;
        _frameDuration = TimeSpan.FromMilliseconds(16.6667);
    }

    private static IntPtr CreateD3D11Context(AmfNative api, D3D11Device device)
    {
        var createContext = AmfNative.GetDelegate<CreateContextDelegate>(api.Factory, 0);
        var status = createContext(api.Factory, out var context);
        api.ThrowIfFailed(status, "AMFFactory.CreateContext");
        if (context == IntPtr.Zero)
            throw new InvalidOperationException("AMFFactory.CreateContext returned a null context.");

        try
        {
            var initDx11 = AmfNative.GetDelegate<InitDx11Delegate>(context, 18);
            status = initDx11(context, device.NativePointer, AmfNative.AmfDx11_1);
            api.ThrowIfFailed(status, "AMFContext.InitDX11");
            return context;
        }
        catch
        {
            AmfNative.ReleaseInterface(context);
            throw;
        }
    }

    private static IntPtr CreateAvcComponent(AmfNative api, IntPtr context)
    {
        var createComponent = AmfNative.GetDelegate<CreateComponentDelegate>(api.Factory, 1);
        var status = createComponent(api.Factory, context, AmfNative.AvcComponentId, out var component);
        api.ThrowIfFailed(status, "AMFFactory.CreateComponent(AMFVideoEncoderVCE_AVC)");
        if (component == IntPtr.Zero)
            throw new InvalidOperationException("AMFFactory.CreateComponent returned a null AVC component.");

        return component;
    }

    private static void ConfigureH264Encoder(IntPtr component, GpuEncoderSettings settings)
    {
        var fps = Math.Max(1, settings.Fps);
        var keyint = Math.Max(60, fps * 2);
        var qp = QualityToQp(settings.Quality);

        SetInt64(component, "Usage", UsageTranscoding);
        SetInt64(component, "Profile", ProfileHigh);
        SetInt64(component, "RateControlMethod", RateControlConstantQp);
        SetInt64(component, "QualityPreset", QualityPresetQuality);
        SetInt64(component, "QPI", qp);
        SetInt64(component, "QPP", qp);
        SetInt64(component, "QPB", qp);
        SetInt64(component, "BPicturesPattern", 0);
        SetInt64(component, "IDRPeriod", keyint);
        TrySetInt64(component, "HeaderInsertionSpacing", 1);
        TrySetBool(component, "InsertSPS", true);
        TrySetBool(component, "InsertPPS", true);
        TrySetBool(component, "InsertAUD", false);
        SetRate(component, "FrameRate", (uint)fps, 1);
        SetSize(component, "FrameSize", settings.Width, settings.Height);
    }

    private static Texture2D CreateSharedInputTexture(D3D11Device device, int width, int height)
    {
        var sharedDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.Shared
        };

        return new Texture2D(device, sharedDesc);
    }

    private IntPtr CreateSurfaceFromD3D11Texture(Texture2D texture)
    {
        if (_api == null || _context == IntPtr.Zero)
            throw new InvalidOperationException("AMF direct context is not available.");

        var createSurface = AmfNative.GetDelegate<CreateSurfaceFromDx11NativeDelegate>(_context, 49);
        var status = createSurface(_context, texture.NativePointer, out var surface, IntPtr.Zero);
        _api.ThrowIfFailed(status, "AMFContext.CreateSurfaceFromDX11Native");
        if (surface == IntPtr.Zero)
            throw new InvalidOperationException("AMFContext.CreateSurfaceFromDX11Native returned a null surface.");

        return surface;
    }

    private void DrainAvailableOutput(bool waitForPacket, CancellationToken cancellationToken)
    {
        if (_api == null || _component == IntPtr.Zero)
            return;

        var query = AmfNative.GetDelegate<ComponentQueryOutputDelegate>(_component, 23);
        var emptyPolls = 0;
        var maxEmptyPolls = waitForPacket ? 250 : 1;

        while (emptyPolls < maxEmptyPolls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = IntPtr.Zero;
            var status = query(_component, out data);
            if (status == AmfNative.AmfOk && data != IntPtr.Zero)
            {
                try
                {
                    EmitOutputPacket(data);
                }
                finally
                {
                    AmfNative.ReleaseInterface(data);
                }

                emptyPolls = 0;
                continue;
            }

            if (status == AmfNative.AmfEof)
                break;

            if (status == AmfNative.AmfRepeat || status == AmfNative.AmfNeedMoreInput ||
                (status == AmfNative.AmfOk && data == IntPtr.Zero))
            {
                emptyPolls++;
                if (waitForPacket)
                    Thread.Sleep(2);
                continue;
            }

            _api.ThrowIfFailed(status, "AMFComponent.QueryOutput");
        }
    }

    private void EmitOutputPacket(IntPtr data)
    {
        var dataType = GetDataType(data);
        if (dataType != AmfNative.AmfDataBuffer)
        {
            Debug.WriteLine($"[AMF direct] ignoring non-buffer output type={dataType}");
            return;
        }

        var getSize = AmfNative.GetDelegate<BufferGetSizeDelegate>(data, 24);
        var getNative = AmfNative.GetDelegate<BufferGetNativeDelegate>(data, 25);
        var size = checked((int)Math.Min(getSize(data), int.MaxValue));
        var ptr = getNative(data);

        if (size <= 0 || ptr == IntPtr.Zero)
            return;

        var bitstream = new byte[size];
        Marshal.Copy(ptr, bitstream, 0, size);
        if (_extraData is { Length: > 0 } extraData)
        {
            var merged = new byte[extraData.Length + bitstream.Length];
            System.Buffer.BlockCopy(extraData, 0, merged, 0, extraData.Length);
            System.Buffer.BlockCopy(bitstream, 0, merged, extraData.Length, bitstream.Length);
            bitstream = merged;
            _extraData = null;
        }

        var pts = GetPts(data);
        var duration = GetDuration(data);
        var pictureType = TryGetInt64(data, "OutputDataType", out var outputType)
            ? OutputTypeName(outputType)
            : "UNKNOWN";

        PacketEncoded?.Invoke(new AmfEncodedPacketInfo(
            true,
            bitstream.Length,
            bitstream,
            pts,
            duration,
            Interlocked.Read(ref _frameIndex),
            pictureType,
            null));

        Debug.WriteLine(
            "[AMF direct] encoded packet " +
            $"pts={TimeSpan.FromTicks(Math.Max(0, pts)).TotalMilliseconds:F2}ms " +
            $"bytes={bitstream.Length} type={pictureType}");
    }

    private static int GetDataType(IntPtr data)
    {
        var getDataType = AmfNative.GetDelegate<DataGetDataTypeDelegate>(data, 17);
        return getDataType(data);
    }

    private static void SetDataTiming(IntPtr data, TimeSpan pts, TimeSpan duration)
    {
        var setPts = AmfNative.GetDelegate<DataSetPtsDelegate>(data, 19);
        var setDuration = AmfNative.GetDelegate<DataSetDurationDelegate>(data, 21);
        setPts(data, Math.Max(0, pts.Ticks));
        setDuration(data, Math.Max(1, duration.Ticks));
    }

    private static long GetPts(IntPtr data)
    {
        var getPts = AmfNative.GetDelegate<DataGetPtsDelegate>(data, 20);
        return getPts(data);
    }

    private static long GetDuration(IntPtr data)
    {
        var getDuration = AmfNative.GetDelegate<DataGetDurationDelegate>(data, 22);
        return getDuration(data);
    }

    private static void SetInt64(IntPtr storage, string name, long value) =>
        SetProperty(storage, name, AmfVariant.Int64(value));

    private static void SetBool(IntPtr storage, string name, bool value) =>
        SetProperty(storage, name, AmfVariant.Bool(value));

    private static void SetSize(IntPtr storage, string name, int width, int height) =>
        SetProperty(storage, name, AmfVariant.Size(width, height));

    private static void SetRate(IntPtr storage, string name, uint numerator, uint denominator) =>
        SetProperty(storage, name, AmfVariant.Rate(numerator, denominator));

    private static void SetProperty(IntPtr storage, string name, AmfVariant value)
    {
        var setProperty = AmfNative.GetDelegate<SetPropertyDelegate>(storage, 3);
        var status = setProperty(storage, name, value);
        if (status != AmfNative.AmfOk)
            throw new AmfApiException($"AMFPropertyStorage.SetProperty({name})", status);
    }

    private static void TrySetInt64(IntPtr storage, string name, long value) =>
        TrySetProperty(storage, name, AmfVariant.Int64(value));

    private static void TrySetBool(IntPtr storage, string name, bool value) =>
        TrySetProperty(storage, name, AmfVariant.Bool(value));

    private static void TrySetProperty(IntPtr storage, string name, AmfVariant value)
    {
        try
        {
            var setProperty = AmfNative.GetDelegate<SetPropertyDelegate>(storage, 3);
            var status = setProperty(storage, name, value);
            if (status != AmfNative.AmfOk)
            {
                Debug.WriteLine(
                    $"[AMF direct] optional property {name} ignored: " +
                    $"{AmfNative.StatusName(status)} ({status})");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AMF direct] optional property {name} ignored: {ex.Message}");
        }
    }

    private static bool TryGetInt64(IntPtr storage, string name, out long value)
    {
        value = 0;
        if (!TryGetProperty(storage, name, out var variant) || variant.Type != 2)
            return false;

        value = variant.Int64Value;
        return true;
    }

    private static bool TryGetProperty(IntPtr storage, string name, out AmfVariant variant)
    {
        variant = default;
        var getProperty = AmfNative.GetDelegate<GetPropertyDelegate>(storage, 4);
        var status = getProperty(storage, name, out variant);
        return status == AmfNative.AmfOk;
    }

    private static byte[]? TryReadBufferProperty(IntPtr storage, string name)
    {
        if (!TryGetProperty(storage, name, out var variant) ||
            variant.Type != 12 ||
            variant.InterfaceValue == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return CopyBufferBytes(variant.InterfaceValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AMF direct] could not read {name}: {ex.Message}");
            return null;
        }
        finally
        {
            AmfNative.ReleaseInterface(variant.InterfaceValue);
        }
    }

    private static byte[]? CopyBufferBytes(IntPtr buffer)
    {
        var getSize = AmfNative.GetDelegate<BufferGetSizeDelegate>(buffer, 24);
        var getNative = AmfNative.GetDelegate<BufferGetNativeDelegate>(buffer, 25);
        var rawSize = getSize(buffer);
        if (rawSize == 0 || rawSize > int.MaxValue)
            return null;

        var ptr = getNative(buffer);
        if (ptr == IntPtr.Zero)
            return null;

        var bytes = new byte[(int)rawSize];
        Marshal.Copy(ptr, bytes, 0, bytes.Length);
        return bytes;
    }

    private static long QualityToQp(string quality) => quality switch
    {
        "D\u00fc\u015f\u00fck" or "D\u00c3\u00bc\u00c5\u0178\u00c3\u00bck" => 28,
        "Y\u00fcksek" or "Y\u00c3\u00bcksek" => 16,
        _ => 20
    };

    private static string OutputTypeName(long outputType) => outputType switch
    {
        0 => "IDR",
        1 => "I",
        2 => "P",
        3 => "B",
        _ => $"0x{outputType:X}"
    };

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentSubmitInputDelegate(IntPtr component, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentQueryOutputDelegate(IntPtr component, out IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentDrainDelegate(IntPtr component);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSurfaceFromDx11NativeDelegate(
        IntPtr context,
        IntPtr dx11Surface,
        out IntPtr surface,
        IntPtr observer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetPropertyDelegate(
        IntPtr storage,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        AmfVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetPropertyDelegate(
        IntPtr storage,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out AmfVariant value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DataSetPtsDelegate(IntPtr data, long pts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long DataGetPtsDelegate(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DataSetDurationDelegate(IntPtr data, long duration);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long DataGetDurationDelegate(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DataGetDataTypeDelegate(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint BufferGetSizeDelegate(IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr BufferGetNativeDelegate(IntPtr buffer);
}

internal sealed record AmfEncodedPacketInfo(
    bool HasBitstream,
    int SizeBytes,
    byte[]? Bitstream,
    long OutputTimeStamp,
    long OutputDuration,
    long FrameIndex,
    string PictureType,
    string? Message);
