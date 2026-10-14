using System.Diagnostics;
using System.IO;
using ScreenRecorder.Helpers;
using ScreenRecorder.Models;
using ScreenRecorder.Services.Diagnostics;
using ScreenRecorder.Services.GpuEncoding;
using ScreenRecorder.Services.Muxing;

namespace ScreenRecorder.Services.Recording;

internal sealed class GpuDirectSegmentRunner
{
    public const string EnvVarName = "FORECORD_GPU_DIRECT_MUX";

    public bool IsRequested { get; private set; }
    public bool IsActive { get; private set; }
    public string? BackendName { get; private set; }
    public string? SegmentPath { get; private set; }
    public string? LastError { get; private set; }

    public void Reset(AppSettings settings)
    {
        IsRequested = IsEnvironmentEnabled(EnvVarName) || settings.EnableGpuDirectPipeline;
        IsActive = false;
        BackendName = null;
        SegmentPath = null;
        LastError = null;
    }

    public bool TryBeginSegment(
        string segPath,
        bool micEnabled,
        bool sysEnabled,
        int screenW,
        int screenH,
        int outW,
        int outH)
    {
        IsActive = false;
        BackendName = null;
        SegmentPath = null;

        if (!IsRequested)
            return false;

        var forcedByEnv = IsEnvironmentEnabled(EnvVarName);
        if (micEnabled || sysEnabled)
        {
            if (!forcedByEnv)
            {
                RecordingDiagnostics.Info(
                    DiagnosticStage.GpuProbe,
                    "GPU direct own mux requested, but audio is enabled; using FFmpeg path.");
                return false;
            }

            RecordingDiagnostics.Info(
                DiagnosticStage.GpuProbe,
                "GPU direct own mux env is set; audio is bypassed for this video-only mux test.");
        }

        if (outW != screenW || outH != screenH)
        {
            RecordingDiagnostics.Info(
                DiagnosticStage.GpuProbe,
                "GPU direct own mux requested, but output scaling is enabled; using FFmpeg path.");
            return false;
        }

        DesktopAdapterInfo adapterInfo;
        try
        {
            adapterInfo = DesktopCapture.GetDefaultAdapterInfo();
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.GpuProbe,
                "Could not inspect capture adapter for GPU direct own mux; falling back to FFmpeg.",
                ex);
            return false;
        }

        RecordingDiagnostics.Info(
            DiagnosticStage.GpuProbe,
            $"GPU direct own mux candidate adapter={adapterInfo.ToDebugString()}");

        if (!adapterInfo.IsNvidia)
        {
            if (adapterInfo.IsAmd)
            {
                var amfProbe = AmfSessionProbe.ProbeDefaultD3D11Device(screenW, screenH);
                RecordingDiagnostics.Info(DiagnosticStage.GpuProbe, amfProbe.ToDebugString());
                if (!amfProbe.CanInitializeAvc)
                {
                    RecordingDiagnostics.Info(
                        DiagnosticStage.GpuProbe,
                        "AMF AVC backend probe did not pass. Using FFmpeg path.");
                    return false;
                }

                IsActive = true;
                BackendName = "amf-direct";
                SegmentPath = segPath;
                LastError = null;
                RecordingDiagnostics.Info(
                    DiagnosticStage.EncoderInit,
                    "ACTIVE=GPU_DIRECT_AMF_OWN_MUX videoOnly=true container=mp4");
                return true;
            }

            RecordingDiagnostics.Info(
                DiagnosticStage.GpuProbe,
                $"GPU direct own mux supports NVENC/AMF; capture adapter is {adapterInfo.VendorName}. Using FFmpeg path.");
            return false;
        }

        var nvencProbe = NvencSessionProbe.ProbeDefaultD3D11Device();
        RecordingDiagnostics.Info(DiagnosticStage.GpuProbe, nvencProbe.ToDebugString());
        if (!nvencProbe.CanOpenD3D11Session ||
            !nvencProbe.SupportsH264 ||
            !nvencProbe.SupportsArgbInput ||
            !nvencProbe.CanInitializeEncoder)
        {
            RecordingDiagnostics.Info(
                DiagnosticStage.GpuProbe,
                "NVENC own mux probe did not pass; using FFmpeg path.");
            return false;
        }

        IsActive = true;
        BackendName = "nvenc-direct";
        SegmentPath = segPath;
        LastError = null;
        RecordingDiagnostics.Info(
            DiagnosticStage.EncoderInit,
            "ACTIVE=GPU_DIRECT_NVENC_OWN_MUX videoOnly=true container=mp4");
        return true;
    }

    public void RunCaptureLoop(
        DesktopCapture cap,
        string segmentPath,
        AppSettings settings,
        int fpsCap,
        Func<bool> isStopSignaled,
        Action<AdaptiveThrottleAction, int> onAdaptiveThrottleChanged)
    {
        var adaptive = new AdaptivePerformanceController(
            settings.Fps,
            settings.EnablePerformanceAdaptiveScaling,
            fpsCap);
        var sw = Stopwatch.StartNew();
        long acquiredFrames = 0;
        long skippedFrames = 0;
        long encodedCallbacks = 0;

        try
        {
            using var muxer = new H264Mp4Muxer(segmentPath, cap.Width, cap.Height, settings.Fps);
            using var encoder = CreateEncoder();

            void AddEncodedPacket(byte[]? bitstream, long outputTimeStamp, long outputDuration, string pictureType)
            {
                if (bitstream is not { Length: > 0 })
                    return;

                var pts = outputTimeStamp >= 0 && outputTimeStamp <= long.MaxValue
                    ? TimeSpan.FromTicks(outputTimeStamp)
                    : sw.Elapsed;
                var duration = outputDuration > 0 && outputDuration <= long.MaxValue
                    ? TimeSpan.FromTicks(outputDuration)
                    : TimeSpan.Zero;

                muxer.AddAnnexBPacket(bitstream, pts, duration, pictureType);
                Interlocked.Increment(ref encodedCallbacks);
            }

            if (encoder is NvencGpuEncoder nvencEncoder)
            {
                nvencEncoder.PacketEncoded += packet =>
                    AddEncodedPacket(
                        packet.Bitstream,
                        packet.OutputTimeStamp <= long.MaxValue ? (long)packet.OutputTimeStamp : -1,
                        packet.OutputDuration <= long.MaxValue ? (long)packet.OutputDuration : 0,
                        packet.PictureType);
            }
            else if (encoder is AmfGpuEncoder amfEncoder)
            {
                amfEncoder.PacketEncoded += packet =>
                    AddEncodedPacket(
                        packet.Bitstream,
                        packet.OutputTimeStamp,
                        packet.OutputDuration,
                        packet.PictureType);
            }

            encoder.Initialize(
                cap.Device,
                new GpuEncoderSettings(
                    cap.Width,
                    cap.Height,
                    settings.Fps,
                    settings.Quality,
                    segmentPath));

            var encodedAny = false;
            while (!isStopSignaled())
            {
                var t0 = sw.Elapsed;
                var wroteFrame = false;

                var acquiredGpuFrame = cap.TryAcquireGpuFrame(out var gpuFrame, timeoutMs: 0);
                if (acquiredGpuFrame && gpuFrame != null)
                {
                    using (gpuFrame)
                    {
                        acquiredFrames++;
                        var hasImageUpdate = gpuFrame.Metadata.LastPresentTime != 0;
                        var primeEncoder = muxer.SampleCount == 0 && acquiredFrames <= 30;
                        if (!encodedAny || hasImageUpdate || primeEncoder)
                        {
                            encoder.EncodeTexture(gpuFrame.Texture, sw.Elapsed);
                            encodedAny = true;
                            wroteFrame = true;
                        }
                        else
                        {
                            skippedFrames++;
                        }
                    }
                }

                var elapsed = sw.Elapsed - t0;
                var action = adaptive.Observe(sw.Elapsed, elapsed, wroteFrame, TimeSpan.Zero, out _);
                if (action != AdaptiveThrottleAction.None)
                    onAdaptiveThrottleChanged(action, adaptive.CurrentFps);

                var remaining = adaptive.FrameInterval - elapsed;
                if (remaining > TimeSpan.FromMilliseconds(1))
                    Thread.Sleep((int)remaining.TotalMilliseconds);
            }

            encoder.FinishAsync().GetAwaiter().GetResult();
            muxer.Finish();
            RecordingDiagnostics.Info(
                DiagnosticStage.MuxFinish,
                $"OwnMux segment finished acquired={acquiredFrames} encodedPackets={Interlocked.Read(ref encodedCallbacks)} skippedIdle={skippedFrames} path={segmentPath}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RecordingDiagnostics.ReportError(
                DiagnosticStage.EncodeFrame,
                $"GPU direct mux failed for segment {segmentPath}",
                ex);
            try { File.WriteAllText(segmentPath + ".ownmux.error.txt", ex.ToString()); } catch { }
            try { if (File.Exists(segmentPath)) File.Delete(segmentPath); } catch { }
        }
    }

    public static bool TryProbeCaptureContract(DesktopCapture cap, AppSettings settings)
    {
        try
        {
            if (!cap.TryAcquireGpuFrame(out var gpuFrame, timeoutMs: 0) ||
                gpuFrame == null)
            {
                Debug.WriteLine("[GpuDirect] Phase 2A probe did not acquire a GPU frame yet.");
                return false;
            }

            using (gpuFrame)
            {
                var desc = gpuFrame.Texture.Description;
                Debug.WriteLine(
                    "[GpuDirect] Phase 2A GPU frame contract ok " +
                    $"size={gpuFrame.Width}x{gpuFrame.Height} " +
                    $"format={desc.Format} " +
                    $"lastPresent={gpuFrame.Metadata.LastPresentTime} " +
                    $"dirtyRects={gpuFrame.Metadata.DirtyRectCount} " +
                    $"moveRects={gpuFrame.Metadata.MoveRectCount}");

                using var encoder = new NvencGpuEncoder();
                if (!encoder.Capability.IsAvailable)
                {
                    Debug.WriteLine($"[GpuDirect] Phase 2B NVENC unavailable: {encoder.Capability.Message}");
                    return false;
                }

                encoder.Initialize(
                    cap.Device,
                    new GpuEncoderSettings(
                        gpuFrame.Width,
                        gpuFrame.Height,
                        settings.Fps,
                        settings.Quality,
                        ""));
                encoder.EncodeTexture(gpuFrame.Texture, TimeSpan.Zero);
                Debug.WriteLine("[GpuDirect] Phase 2B NVENC live desktop texture register/map smoke succeeded.");
                return true;
            }
        }
        catch (DeviceLostException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.GpuProbe,
                "GPU direct capture contract probe failed.",
                ex);
            return false;
        }
    }

    public static bool IsEnvironmentEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value != null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private IGpuEncoder CreateEncoder() =>
        string.Equals(BackendName, "amf-direct", StringComparison.OrdinalIgnoreCase)
            ? new AmfGpuEncoder()
            : new NvencGpuEncoder();
}
