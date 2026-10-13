using SharpDX.Direct3D11;

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
    private IntPtr _encoder;
    private long _textureSmokeFrames;
    private TimeSpan _frameDuration = TimeSpan.FromMilliseconds(16.6667);

    public NvencGpuEncoder()
        : this(NvencCapabilityProbe.Probe())
    {
    }

    internal NvencGpuEncoder(GpuEncoderCapability capability)
    {
        Capability = capability;
    }

    public void Initialize(Device device, GpuEncoderSettings settings)
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

            // Phase 2D initializes NVENC and performs packet smoke tests while
            // the production recording hot path remains on the FFmpeg fallback.
            System.Diagnostics.Debug.WriteLine("[NVENC direct] H.264 encoder initialized for packet smoke.");
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

        if (!IsInitialized)
            throw new InvalidOperationException("NVENC direct encoder is not initialized.");

        if (_api == null || _encoder == IntPtr.Zero)
            throw new InvalidOperationException("NVENC direct API/session is not available.");

        var desc = texture.Description;
        NvencRegisteredResource registered = default;
        NvencMappedResource mapped = default;
        NvencBitstreamBuffer bitstream = default;

        try
        {
            bitstream = _api.CreateBitstreamBuffer(_encoder);
            registered = _api.RegisterD3D11Texture(
                _encoder,
                texture.NativePointer,
                desc.Width,
                desc.Height);

            mapped = _api.MapInputResource(_encoder, registered);
            var frameIndex = (uint)(Interlocked.Increment(ref _textureSmokeFrames) - 1);
            var packet = _api.EncodeMappedTexture(
                _encoder,
                mapped,
                bitstream,
                desc.Width,
                desc.Height,
                frameIndex,
                presentationTime,
                _frameDuration);

            if (packet.HasBitstream)
            {
                PacketEncoded?.Invoke(packet);
                System.Diagnostics.Debug.WriteLine(
                    "[NVENC direct] encoded packet smoke " +
                    $"#{packet.FrameIndex} pts={presentationTime.TotalMilliseconds:F2}ms " +
                    $"bytes={packet.SizeBytes} type={packet.PictureType} " +
                    $"format={NvencNative.BufferFormatName(mapped.BufferFormat)}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[NVENC direct] texture accepted without packet yet " +
                    $"#{frameIndex} pts={presentationTime.TotalMilliseconds:F2}ms " +
                    packet.Message);
            }
        }
        finally
        {
            try
            {
                if (mapped.Handle != IntPtr.Zero)
                    _api.UnmapInputResource(_encoder, mapped);
            }
            finally
            {
                try
                {
                    if (registered.Handle != IntPtr.Zero)
                        _api.UnregisterResource(_encoder, registered);
                }
                finally
                {
                    if (bitstream.Handle != IntPtr.Zero)
                        _api.DestroyBitstreamBuffer(_encoder, bitstream);
                }
            }
        }
    }

    public Task FinishAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Dispose()
    {
        _api?.DestroyEncoder(_encoder);
        _encoder = IntPtr.Zero;
        _api?.Dispose();
        _api = null;
        IsInitialized = false;
        SupportsH264 = false;
        SupportsArgbInput = false;
        _textureSmokeFrames = 0;
        _frameDuration = TimeSpan.FromMilliseconds(16.6667);
    }
}
