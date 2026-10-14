using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenRecorder.Helpers;
using ScreenRecorder.Services.GpuEncoding;
using ScreenRecorder.Services.Muxing;

namespace ScreenRecorder.Services.Diagnostics;

public sealed record SelfTestCaseResult(
    string Name,
    bool Passed,
    long ElapsedMs,
    string Detail);

public sealed record SelfTestReport(
    DateTime Timestamp,
    bool AllPassed,
    IReadOnlyList<SelfTestCaseResult> Results,
    string ReportFilePath)
{
    public string FormatSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Forecord Pipeline Self-Test ({Timestamp:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"Result: {(AllPassed ? "PASS" : "FAIL")}");
        foreach (var r in Results)
            sb.AppendLine($" - [{(r.Passed ? "PASS" : "FAIL")}] {r.Name} ({r.ElapsedMs} ms): {r.Detail}");
        sb.AppendLine($"Report saved to: {ReportFilePath}");
        return sb.ToString();
    }
}

public static class PipelineSelfTest
{
    public static SelfTestReport RunAll()
    {
        var results = new List<SelfTestCaseResult>
        {
            RunCase("1. H264Mp4Muxer Synthetic Annex-B -> MP4", TestMp4Muxer),
            RunCase("2. DXGI DesktopCapture Readback & GPU Frame", TestDesktopCapture),
            RunCase("3. GPU Encoder 180-Frame Dynamic Color Stress", TestGpuEncoder),
            RunCase("4. Full RecordingService E2E (Start -> Pause -> Resume -> Stop)", TestEndToEndRecordingLifecycle)
        };

        var allPassed = results.All(r => r.Passed);
        var reportPath = Path.Combine(RecordingDiagnostics.LogDirectory, "selftest_report.txt");
        var report = new SelfTestReport(DateTime.Now, allPassed, results, reportPath);

        try
        {
            Directory.CreateDirectory(RecordingDiagnostics.LogDirectory);
            File.WriteAllText(reportPath, report.FormatSummary(), Encoding.UTF8);
        }
        catch
        {
        }

        return report;
    }

    private static SelfTestCaseResult RunCase(string name, Func<string> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = action();
            sw.Stop();
            RecordingDiagnostics.Info(DiagnosticStage.SelfTest, $"[PASS] {name}: {detail}");
            return new SelfTestCaseResult(name, true, sw.ElapsedMilliseconds, detail);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordingDiagnostics.ReportError(DiagnosticStage.SelfTest, $"[FAIL] {name}", ex);
            return new SelfTestCaseResult(name, false, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string TestMp4Muxer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Forecord", "selftest");
        Directory.CreateDirectory(tempDir);
        var mp4Path1 = Path.Combine(tempDir, $"mux_test1_{Guid.NewGuid():N}.mp4");
        var mp4Path2 = Path.Combine(tempDir, $"mux_test2_{Guid.NewGuid():N}.mp4");
        var concatPath = Path.Combine(tempDir, $"mux_concat_{Guid.NewGuid():N}.mp4");

        try
        {
            // Minimal Annex-B packet containing SPS (NAL 7), PPS (NAL 8), and IDR slice (NAL 5)
            byte[] annexBIFrame =
            {
                0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0xC0, 0x1F, 0x8C, 0x8D, 0x40, 0x50, 0x1E, 0xD0, 0x80,
                0x00, 0x00, 0x00, 0x01, 0x68, 0xCE, 0x3C, 0x80,
                0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21, 0xA0
            };

            byte[] annexBPFrame =
            {
                0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x24, 0x6C
            };

            // Minimal valid raw AAC-LC silent frame (CPE stereo silence)
            byte[] silentAac = { 0x21, 0x10, 0x04, 0x60, 0x8C, 0x1C };

            foreach (var p in new[] { mp4Path1, mp4Path2 })
            {
                using var muxer = new H264Mp4Muxer(p, 1920, 1080, 60);
                muxer.ConfigureAudio(48000, 2);
                muxer.AddAnnexBPacket(annexBIFrame, TimeSpan.Zero, TimeSpan.FromMilliseconds(16.67), "IDR");
                muxer.AddAudioPacket(silentAac, 1024);
                muxer.AddAnnexBPacket(annexBPFrame, TimeSpan.FromMilliseconds(16.67), TimeSpan.FromMilliseconds(16.67), "P");
                muxer.AddAudioPacket(silentAac, 1024);
                muxer.Finish();
            }

            if (!H264Mp4Muxer.TryConcatenateSegments(new[] { mp4Path1, mp4Path2 }, concatPath))
                throw new InvalidOperationException("TryConcatenateSegments failed on dual-track (video+audio) segments.");

            var bytes = File.ReadAllBytes(concatPath);
            if (bytes.Length < 256)
                throw new InvalidOperationException($"Concatenated MP4 output is unexpectedly small ({bytes.Length} bytes).");

            var ascii = Encoding.ASCII.GetString(bytes);
            if (!ascii.Contains("mdat", StringComparison.Ordinal) ||
                !ascii.Contains("moov", StringComparison.Ordinal) ||
                !ascii.Contains("avcC", StringComparison.Ordinal) ||
                !ascii.Contains("mp4a", StringComparison.Ordinal) ||
                !ascii.Contains("esds", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MP4 is missing required mdat/moov/avcC/mp4a/esds boxes.");
            }

            return $"Valid dual-track (H.264+AAC) MP4 & native segment concat verified ({bytes.Length} bytes, avcC+mp4a+esds).";
        }
        finally
        {
            try { if (File.Exists(mp4Path1)) File.Delete(mp4Path1); } catch { }
            try { if (File.Exists(mp4Path2)) File.Delete(mp4Path2); } catch { }
            try { if (File.Exists(concatPath)) File.Delete(concatPath); } catch { }
        }
    }

    private static string TestDesktopCapture()
    {
        var adapter = DesktopCapture.GetDefaultAdapterInfo();
        using var cap = new DesktopCapture(0);
        if (cap.Width <= 0 || cap.Height <= 0)
            throw new InvalidOperationException($"Invalid screen dimensions: {cap.Width}x{cap.Height}");

        DesktopGpuFrame? gpuFrame = null;
        for (var attempt = 0; attempt < 10 && gpuFrame == null; attempt++)
        {
            if (cap.TryAcquireGpuFrame(out gpuFrame, timeoutMs: 50) && gpuFrame != null)
                break;
            Thread.Sleep(15);
        }

        if (gpuFrame != null)
        {
            using (gpuFrame)
            {
                var desc = gpuFrame.Texture.Description;
                return $"Captured GPU Texture2D {gpuFrame.Width}x{gpuFrame.Height} ({desc.Format}) on {adapter.ToDebugString()}.";
            }
        }

        var buffer = new byte[cap.Width * cap.Height * 4];
        if (cap.CaptureInitialFrameFallback(buffer))
            return $"Desktop idle; verified initial frame fallback {cap.Width}x{cap.Height} on {adapter.ToDebugString()}.";

        throw new InvalidOperationException("Could not acquire GPU frame or initial fallback frame.");
    }

    private static string TestGpuEncoder()
    {
        var adapter = DesktopCapture.GetDefaultAdapterInfo();
        using var cap = new DesktopCapture(0);

        using var synthTex = new SharpDX.Direct3D11.Texture2D(
            cap.Device,
            new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = cap.Width,
                Height = cap.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            });

        if (adapter.IsNvidia)
        {
            var probe = NvencSessionProbe.ProbeDefaultD3D11Device();
            if (!probe.CanInitializeEncoder)
                throw new InvalidOperationException($"NVENC probe failed: {probe.ToDebugString()}");

            var tempMp4 = Path.Combine(Path.GetTempPath(), "Forecord", "selftest", $"nvenc_live_{Guid.NewGuid():N}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(tempMp4)!);
            try
            {
                var packets = 0;
                var totalBytes = 0;
                using (var muxer = new H264Mp4Muxer(tempMp4, cap.Width, cap.Height, 60))
                using (var encoder = new NvencGpuEncoder())
                {
                    encoder.PacketEncoded += p =>
                    {
                        if (p.Bitstream is { Length: > 0 })
                        {
                            packets++;
                            totalBytes += p.SizeBytes;
                            muxer.AddAnnexBPacket(p.Bitstream, TimeSpan.FromMilliseconds(packets * 16.67), TimeSpan.FromMilliseconds(16.67), p.PictureType);
                        }
                    };

                    encoder.Initialize(cap.Device, new GpuEncoderSettings(cap.Width, cap.Height, 60, "Yüksek", tempMp4));
                    var rtv = new SharpDX.Direct3D11.RenderTargetView(cap.Device, synthTex);
                    try
                    {
                        for (var i = 0; i < 180; i++)
                        {
                            var r = (i * 5 % 255) / 255f;
                            var g = (i * 11 % 255) / 255f;
                            var b = (i * 17 % 255) / 255f;
                            cap.Device.ImmediateContext.ClearRenderTargetView(
                                rtv,
                                new SharpDX.Mathematics.Interop.RawColor4(r, g, b, 1f));
                            encoder.EncodeTexture(synthTex, TimeSpan.FromMilliseconds(i * 16.67));
                        }
                    }
                    finally
                    {
                        rtv.Dispose();
                    }
                    encoder.FinishAsync().GetAwaiter().GetResult();
                    muxer.Finish();
                }

                var mp4Size = new FileInfo(tempMp4).Length;
                if (packets < 100 || mp4Size < 2048)
                    throw new InvalidOperationException($"NVENC own-mux produced insufficient output (packets={packets}, mp4Bytes={mp4Size}).");

                if (!H264Mp4Muxer.TryProbeSegmentStructure(tempMp4))
                    throw new InvalidOperationException("NVENC own-mux produced an MP4 file, but structural probe failed.");

                return $"NVENC direct -> H264Mp4Muxer encoded {packets}/180 dynamic frame(s), {totalBytes} bitstream bytes, verified MP4 ({mp4Size} bytes) at {cap.Width}x{cap.Height}.";
            }
            finally
            {
                try { if (File.Exists(tempMp4)) File.Delete(tempMp4); } catch { }
            }
        }

        if (adapter.IsAmd)
        {
            var probe = AmfSessionProbe.ProbeDefaultD3D11Device(cap.Width, cap.Height);
            if (!probe.CanInitializeAvc)
                throw new InvalidOperationException($"AMF probe failed: {probe.ToDebugString()}");

            var tempMp4 = Path.Combine(Path.GetTempPath(), "Forecord", "selftest", $"amf_live_{Guid.NewGuid():N}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(tempMp4)!);
            try
            {
                var packets = 0;
                var totalBytes = 0;
                long finishElapsedMs = 0;
                using (var muxer = new H264Mp4Muxer(tempMp4, cap.Width, cap.Height, 60))
                using (var encoder = new AmfGpuEncoder())
                {
                    encoder.PacketEncoded += p =>
                    {
                        if (p.Bitstream is { Length: > 0 })
                        {
                            packets++;
                            totalBytes += p.SizeBytes;
                            muxer.AddAnnexBPacket(p.Bitstream, TimeSpan.FromMilliseconds(packets * 16.67), TimeSpan.FromMilliseconds(16.67), p.PictureType);
                        }
                    };

                    encoder.Initialize(cap.Device, new GpuEncoderSettings(cap.Width, cap.Height, 60, "Yüksek", tempMp4));
                    var rtv = new SharpDX.Direct3D11.RenderTargetView(cap.Device, synthTex);
                    try
                    {
                        for (var i = 0; i < 180; i++)
                        {
                            var r = (i * 5 % 255) / 255f;
                            var g = (i * 11 % 255) / 255f;
                            var b = (i * 17 % 255) / 255f;
                            cap.Device.ImmediateContext.ClearRenderTargetView(
                                rtv,
                                new SharpDX.Mathematics.Interop.RawColor4(r, g, b, 1f));
                            encoder.EncodeTexture(synthTex, TimeSpan.FromMilliseconds(i * 16.67));
                        }
                    }
                    finally
                    {
                        rtv.Dispose();
                    }
                    encoder.FinishAsync().GetAwaiter().GetResult();
                    var finishSw = Stopwatch.StartNew();
                    muxer.Finish();
                    finishSw.Stop();
                    finishElapsedMs = finishSw.ElapsedMilliseconds;
                }

                var mp4Size = new FileInfo(tempMp4).Length;
                if (packets < 100 || mp4Size < 2048)
                    throw new InvalidOperationException($"AMF own-mux produced insufficient output (packets={packets}, mp4Bytes={mp4Size}).");

                if (!H264Mp4Muxer.TryProbeSegmentStructure(tempMp4))
                    throw new InvalidOperationException("AMF own-mux produced an MP4 file, but structural probe failed.");

                return $"AMF direct -> H264Mp4Muxer encoded {packets}/180 dynamic frame(s), {totalBytes}B bitstream, Finish={finishElapsedMs}ms, verified MP4 ({mp4Size}B).";
            }
            finally
            {
                try { if (File.Exists(tempMp4)) File.Delete(tempMp4); } catch { }
            }
        }

        return $"Skipped direct GPU encode test (adapter vendor is {adapter.VendorName}).";
    }

    private static string TestEndToEndRecordingLifecycle()
    {
        var tempOutDir = Path.Combine(Path.GetTempPath(), "Forecord", "selftest_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempOutDir);

        var encoderSvc = new EncoderService();
        using var audioSvc = new AudioService();
        var fileSvc = new FileService();
        using var recSvc = new RecordingService(encoderSvc, audioSvc, fileSvc);

        var settings = new Models.AppSettings
        {
            OutputDirectory = tempOutDir,
            Fps = 60,
            Quality = "Yüksek",
            OutputResolution = "720p",
            MicrophoneEnabled = false,
            SystemAudioEnabled = true,
            EnableGpuDirectPipeline = true,
            MonitorIndex = 0
        };

        // Keep a small background cursor wiggle so DXGI Desktop Duplication
        // delivers live frames during automated testing even if the user isn't
        // touching the mouse.
        using var wiggleCts = new CancellationTokenSource();
        var wiggleTask = Task.Run(async () =>
        {
            var toggle = false;
            while (!wiggleCts.IsCancellationRequested)
            {
                var pt = System.Windows.Forms.Cursor.Position;
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(pt.X + (toggle ? 1 : -1), pt.Y);
                toggle = !toggle;
                await Task.Delay(30).ConfigureAwait(false);
            }
        });

        string? finalPath = null;
        var screenshotPath = Path.Combine(tempOutDir, "selftest_shot.png");
        var screenshotOk = false;
        long stopElapsedMs = 0;
        try
        {
            Task.Run(async () =>
            {
                await recSvc.StartAsync(settings).ConfigureAwait(false);
                await Task.Delay(1200).ConfigureAwait(false);
                screenshotOk = await recSvc.TrySaveScreenshotAsync(screenshotPath).ConfigureAwait(false);
                await Task.Delay(1300).ConfigureAwait(false);
                recSvc.Pause();
                await Task.Delay(400).ConfigureAwait(false);
                await recSvc.ResumeAsync().ConfigureAwait(false);
                await Task.Delay(2500).ConfigureAwait(false);
                var stopSw = Stopwatch.StartNew();
                finalPath = await recSvc.StopAsync().ConfigureAwait(false);
                stopSw.Stop();
                stopElapsedMs = stopSw.ElapsedMilliseconds;
            }).GetAwaiter().GetResult();

            wiggleCts.Cancel();
            try { wiggleTask.GetAwaiter().GetResult(); } catch { }

            if (!screenshotOk || !File.Exists(screenshotPath) || new FileInfo(screenshotPath).Length < 512)
                throw new InvalidOperationException("TrySaveScreenshotAsync failed during GPU-Direct recording.");

            if (string.IsNullOrWhiteSpace(finalPath) || !File.Exists(finalPath))
                throw new InvalidOperationException("StopAsync did not produce an output file.");

            var size = new FileInfo(finalPath).Length;
            var shotSize = new FileInfo(screenshotPath).Length;
            if (!H264Mp4Muxer.TryProbeSegmentStructure(finalPath))
                throw new InvalidOperationException($"Final E2E output ({size} bytes) failed MP4 structural verification.");

            var ascii = Encoding.ASCII.GetString(File.ReadAllBytes(finalPath));
            if (!ascii.Contains("mp4a", StringComparison.Ordinal) || !ascii.Contains("esds", StringComparison.Ordinal))
                throw new InvalidOperationException("Final E2E output does not contain native AAC (mp4a/esds) audio track.");

            return $"Full Start(2.5s @720p + LiveAAC + Screenshot {shotSize}B) -> Pause -> Resume(2.5s) -> Instant Stop ({stopElapsedMs}ms, 0 FFmpeg) succeeded: {Path.GetFileName(finalPath)} ({size}B, H.264+AAC verified).";
        }
        finally
        {
            wiggleCts.Cancel();
            try { if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true); } catch { }
        }
    }
}
