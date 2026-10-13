using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace ScreenRecorder.Services.GpuEncoding;

public sealed record NvencSessionProbeResult(
    bool CanCreateFunctionTable,
    bool CanOpenD3D11Session,
    bool SupportsH264,
    bool SupportsArgbInput,
    string Message,
    int EncodeGuidCount = 0,
    string InputFormats = "",
    bool CanUsePacketApi = false,
    bool CanInitializeEncoder = false,
    bool CanRegisterTexture = false,
    bool CanMapTexture = false,
    string TextureInteropMessage = "")
{
    public string ToDebugString()
    {
        return
            "[GPU encoder] nvenc-direct session: " +
            $"functionTable={CanCreateFunctionTable}; " +
            $"d3d11Session={CanOpenD3D11Session}; " +
            $"h264={SupportsH264}; " +
            $"argbInput={SupportsArgbInput}; " +
            $"packetApi={CanUsePacketApi}; " +
            $"initializeEncoder={CanInitializeEncoder}; " +
            $"registerTexture={CanRegisterTexture}; " +
            $"mapTexture={CanMapTexture}; " +
            $"encodeGuidCount={EncodeGuidCount}; " +
            $"inputFormats=[{InputFormats}]; {Message} {TextureInteropMessage}".TrimEnd();
    }
}

public static class NvencSessionProbe
{
    public static NvencSessionProbeResult ProbeDefaultD3D11Device()
    {
        if (!NvencNative.TryCreate(out var api, out var message) || api == null)
            return new NvencSessionProbeResult(false, false, false, false, message);

        using (api)
        using (var device = new Device(
                   DriverType.Hardware,
                   DeviceCreationFlags.BgraSupport,
                   new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }))
        {
            var encoder = IntPtr.Zero;
            try
            {
                encoder = api.OpenD3D11Session(device.NativePointer);
                var encodeGuids = api.GetEncodeGuids(encoder);
                var supportsH264 = encodeGuids.Contains(NvencNative.H264CodecGuid);
                var inputFormats = supportsH264
                    ? api.GetInputFormats(encoder, NvencNative.H264CodecGuid)
                    : Array.Empty<uint>();
                var supportsArgb = inputFormats.Contains(NvencNative.BufferFormatArgb);
                var inputFormatText = string.Join(", ", inputFormats.Select(NvencNative.BufferFormatName));
                api.AssertPacketApiReady();
                if (supportsH264)
                    api.InitializeH264Encoder(encoder, 64, 64, 30);
                var textureProbe = supportsArgb
                    ? ProbeTextureInterop(api, encoder, device)
                    : new TextureProbe(
                        false,
                        false,
                        "Texture interop skipped because ARGB input is not advertised.");

                return new NvencSessionProbeResult(
                    true,
                    true,
                    supportsH264,
                    supportsArgb,
                    supportsH264
                        ? "D3D11 NVENC session opens and H.264 capability was queried."
                        : "D3D11 NVENC session opens, but H.264 GUID is not advertised.",
                    encodeGuids.Count,
                    inputFormatText,
                    true,
                    supportsH264,
                    textureProbe.CanRegister,
                    textureProbe.CanMap,
                    textureProbe.Message);
            }
            catch (Exception ex)
            {
                return new NvencSessionProbeResult(
                    true,
                    false,
                    false,
                    false,
                    ex.Message);
            }
            finally
            {
                api.DestroyEncoder(encoder);
            }
        }
    }

    private static TextureProbe ProbeTextureInterop(NvencNative api, IntPtr encoder, Device device)
    {
        using var texture = new Texture2D(device, new Texture2DDescription
        {
            Width = 64,
            Height = 64,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        });

        NvencRegisteredResource registered = default;
        NvencMappedResource mapped = default;

        try
        {
            registered = api.RegisterD3D11Texture(
                encoder,
                texture.NativePointer,
                texture.Description.Width,
                texture.Description.Height);

            mapped = api.MapInputResource(encoder, registered);
            var formatName = NvencNative.BufferFormatName(mapped.BufferFormat);
            return new TextureProbe(true, true, $"D3D11 Texture2D registration and map succeeded as {formatName}.");
        }
        catch (NvencApiException ex)
            when (NvencNative.RequiresInitializedEncoderForResourceRegistration(ex.Status))
        {
            return new TextureProbe(
                false,
                false,
                "D3D11 Texture2D registration entrypoints are wired; driver requires NvEncInitializeEncoder before registering input resources.");
        }
        catch (Exception ex)
        {
            return new TextureProbe(false, false, $"D3D11 Texture2D registration probe failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (mapped.Handle != IntPtr.Zero)
                    api.UnmapInputResource(encoder, mapped);
            }
            catch
            {
            }

            try
            {
                if (registered.Handle != IntPtr.Zero)
                    api.UnregisterResource(encoder, registered);
            }
            catch
            {
            }
        }
    }

    private sealed record TextureProbe(bool CanRegister, bool CanMap, string Message);
}
