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
            RunCase("3. GPU Encoder Live Frame Encode (NVENC/AMF)", TestGpuEncoder)
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
        var mp4Path = Path.Combine(tempDir, $"mux_test_{Guid.NewGuid():N}.mp4");

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

            using (var muxer = new H264Mp4Muxer(mp4Path, 1920, 1080, 60))
            {
                muxer.AddAnnexBPacket(annexBIFrame, TimeSpan.Zero, TimeSpan.FromMilliseconds(16.67), "IDR");
                muxer.AddAnnexBPacket(annexBPFrame, TimeSpan.FromMilliseconds(16.67), TimeSpan.FromMilliseconds(16.67), "P");
                muxer.Finish();
            }

            if (!File.Exists(mp4Path))
                throw new InvalidOperationException("MP4 output file was not created.");

            var bytes = File.ReadAllBytes(mp4Path);
            if (bytes.Length < 128)
                throw new InvalidOperationException($"MP4 output is unexpectedly small ({bytes.Length} bytes).");

            var ftypTag = Encoding.ASCII.GetString(bytes, 4, 4);
            if (ftypTag != "ftyp")
                throw new InvalidOperationException($"Expected 'ftyp' box at offset 4, found '{ftypTag}'.");

            var ascii = Encoding.ASCII.GetString(bytes);
            if (!ascii.Contains("mdat", StringComparison.Ordinal) ||
                !ascii.Contains("moov", StringComparison.Ordinal) ||
                !ascii.Contains("avcC", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MP4 is missing required mdat/moov/avcC boxes.");
            }

            return $"Valid MP4 structure verified ({bytes.Length} bytes, ftyp+mdat+moov+avcC).";
        }
        finally
        {
            try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
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
                    for (var i = 0; i < 6; i++)
                        encoder.EncodeTexture(synthTex, TimeSpan.FromMilliseconds(i * 16.67));
                    encoder.FinishAsync().GetAwaiter().GetResult();
                    muxer.Finish();
                }

                var mp4Size = new FileInfo(tempMp4).Length;
                if (packets == 0 || mp4Size < 256)
                    throw new InvalidOperationException($"NVENC own-mux produced insufficient output (packets={packets}, mp4Bytes={mp4Size}).");

                var encoderSvc = new EncoderService();
                encoderSvc.FindFFmpeg();
                var decodable = encoderSvc.ProbeSegmentAsync(tempMp4, minBytes: 256).GetAwaiter().GetResult();
                if (!decodable)
                    throw new InvalidOperationException("NVENC own-mux produced an MP4 file, but FFmpeg probe could not decode any video frame from it.");

                return $"NVENC direct -> H264Mp4Muxer encoded {packets} packet(s), {totalBytes} bitstream bytes, FFmpeg-verified decodable MP4 ({mp4Size} bytes) at {cap.Width}x{cap.Height}.";
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
                    for (var i = 0; i < 6; i++)
                        encoder.EncodeTexture(synthTex, TimeSpan.FromMilliseconds(i * 16.67));
                    encoder.FinishAsync().GetAwaiter().GetResult();
                    muxer.Finish();
                }

                var mp4Size = new FileInfo(tempMp4).Length;
                if (packets == 0 || mp4Size < 256)
                    throw new InvalidOperationException($"AMF own-mux produced insufficient output (packets={packets}, mp4Bytes={mp4Size}).");

                var encoderSvc = new EncoderService();
                encoderSvc.FindFFmpeg();
                var decodable = encoderSvc.ProbeSegmentAsync(tempMp4, minBytes: 256).GetAwaiter().GetResult();
                if (!decodable)
                    throw new InvalidOperationException("AMF own-mux produced an MP4 file, but FFmpeg probe could not decode any video frame from it.");

                return $"AMF direct -> H264Mp4Muxer encoded {packets} packet(s), {totalBytes} bitstream bytes, FFmpeg-verified decodable MP4 ({mp4Size} bytes) at {cap.Width}x{cap.Height}.";
            }
            finally
            {
                try { if (File.Exists(tempMp4)) File.Delete(tempMp4); } catch { }
            }
        }

        return $"Skipped direct GPU encode test (adapter vendor is {adapter.VendorName}; FFmpeg fallback applies).";
    }
}
