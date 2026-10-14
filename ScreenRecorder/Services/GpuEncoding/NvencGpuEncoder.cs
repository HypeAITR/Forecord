using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder.Services.GpuEncoding;

public sealed class NvencGpuEncoder : IGpuEncoder
{
    public string BackendName => "nvenc-direct";
    public GpuEncoderCapability Capability { get; }
    public bool IsInitialized { get; private set; }
    public bool SupportsH264 { get; private set; }
    public bool SupportsArgbInput { get; private set; }
    internal event Action<NvencEncodedPacketInfo>? PacketEncoded;

    private NvencNative? _api;
    private D3D11Device? _device;
    private IntPtr _encoder;
    private Texture2D? _inputTexture;
    private NvencRegisteredResource _registeredResource;
    private NvencBitstreamBuffer _bitstreamBuffer;
    private int _width;
    private int _height;
    private int _keyframeInterval = 120;
    private long _frameIndex;
    private TimeSpan _frameDuration = TimeSpan.FromMilliseconds(16.6667);

    public NvencGpuEncoder()
        : this(NvencCapabilityProbe.Probe())
    {
    }

    internal NvencGpuEncoder(GpuEncoderCapability capability)
    {
        Capability = capability;
    }

    public void Initialize(D3D11Device device, GpuEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Capability.IsAvailable)
            throw new InvalidOperationException(Capability.Message);

        if (!NvencNative.TryCreate(out var api, out var message) || api == null)
            throw new InvalidOperationException(message);

        try
        {
            _api = api;
            _device = device;
            _width = settings.Width;
            _height = settings.Height;
            _keyframeInterval = Math.Max(60, settings.Fps * 2);
            _encoder = _api.OpenD3D11Session(device.NativePointer);

            var encodeGuids = _api.GetEncodeGuids(_encoder);
            SupportsH264 = encodeGuids.Contains(NvencNative.H264CodecGuid);
            if (!SupportsH264)
                throw new NotSupportedException("NVENC direct session opened, but H.264 is not advertised.");

            var inputFormats = _api.GetInputFormats(_encoder, NvencNative.H264CodecGuid);
            SupportsArgbInput = inputFormats.Contains(NvencNative.BufferFormatArgb);
            if (!SupportsArgbInput)
                throw new NotSupportedException("NVENC direct H.264 path does not advertise ARGB input.");

            _api.AssertPacketApiReady();
            _api.InitializeH264Encoder(_encoder, settings.Width, settings.Height, settings.Fps);
            _frameDuration = TimeSpan.FromSeconds(1.0 / Math.Max(1, settings.Fps));

            // Pre-allocate and register the input texture and output bitstream buffer once.
            _inputTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = settings.Width,
                Height = settings.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            });

            _registeredResource = _api.RegisterD3D11Texture(
                _encoder,
                _inputTexture.NativePointer,
                settings.Width,
                settings.Height);

            _bitstreamBuffer = _api.CreateBitstreamBuffer(_encoder);

            System.Diagnostics.Debug.WriteLine("[NVENC direct] H.264 encoder initialized with reusable GPU resources.");
            IsInitialized = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void EncodeTexture(Texture2D texture, TimeSpan presentationTime)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!IsInitialized || _api == null || _device == null || _inputTexture == null ||
            _encoder == IntPtr.Zero || _registeredResource.Handle == IntPtr.Zero || _bitstreamBuffer.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("NVENC direct encoder is not initialized.");
        }

        _device.ImmediateContext.CopyResource(texture, _inputTexture);

        NvencMappedResource mapped = default;
        try
        {
            mapped = _api.MapInputResource(_encoder, _registeredResource);
            var frameIndex = (uint)(Interlocked.Increment(ref _frameIndex) - 1);
            var forceIdrAndHeaders = (frameIndex % (uint)_keyframeInterval) == 0;
            var packet = _api.EncodeMappedTexture(
                _encoder,
                mapped,
                _bitstreamBuffer,
                _width,
                _height,
                frameIndex,
                presentationTime,
                _frameDuration,
                forceIdrAndHeaders);

            if (packet.HasBitstream)
            {
                PacketEncoded?.Invoke(packet);
            }
        }
        finally
        {
            if (mapped.Handle != IntPtr.Zero)
                _api.UnmapInputResource(_encoder, mapped);
        }
    }

    public Task FinishAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized && _api != null && _encoder != IntPtr.Zero)
            _api.SendEndOfStream(_encoder);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_api != null && _encoder != IntPtr.Zero)
        {
            try
            {
                if (_registeredResource.Handle != IntPtr.Zero)
                    _api.UnregisterResource(_encoder, _registeredResource);
            }
            catch { }

            try
            {
                if (_bitstreamBuffer.Handle != IntPtr.Zero)
                    _api.DestroyBitstreamBuffer(_encoder, _bitstreamBuffer);
            }
            catch { }

            _api.DestroyEncoder(_encoder);
        }

        _registeredResource = default;
        _bitstreamBuffer = default;
        _inputTexture?.Dispose();
        _inputTexture = null;
        _device = null;
        _encoder = IntPtr.Zero;
        _api?.Dispose();
        _api = null;
        _width = 0;
        _height = 0;
        IsInitialized = false;
        SupportsH264 = false;
        SupportsArgbInput = false;
        _frameIndex = 0;
        _frameDuration = TimeSpan.FromMilliseconds(16.6667);
    }
}
