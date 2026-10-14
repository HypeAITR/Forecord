using SharpDX.Direct3D11;

namespace ScreenRecorder.Services.GpuEncoding;

public interface IGpuEncoder : IDisposable
{
    string BackendName { get; }
    GpuEncoderCapability Capability { get; }
    bool IsInitialized { get; }

    void Initialize(Device device, GpuEncoderSettings settings);
    void EncodeTexture(Texture2D texture, TimeSpan presentationTime, Action<Texture2D>? postCopyOverlay = null);
    Task FinishAsync(CancellationToken cancellationToken = default);
}
