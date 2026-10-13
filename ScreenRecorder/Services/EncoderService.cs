using System.Diagnostics;
using System.IO;
using ScreenRecorder.Models;
using ScreenRecorder.Services.GpuEncoding;

namespace ScreenRecorder.Services;

/// <summary>
/// FFmpeg süreçlerini yönetir: kodlama, birleştirme, ses karıştırma.
/// </summary>
public class EncoderService
{
    private string? _ffmpegPath;
#if DEBUG
    private int _loggedDirectNvencProbe;
#endif

    // ─── FFmpeg konumunu bul ─────────────────────────────────────────────────
    public string? FindFFmpeg()
    {
        if (_ffmpegPath != null) return _ffmpegPath;

        // 1. Uygulama dizini
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        var local  = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(local)) { _ffmpegPath = local; return local; }

        // 2. PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) { _ffmpegPath = p; return p; }
        }

        return null;
    }

    // ─── Donanım encoder tespiti ─────────────────────────────────────────────
    private string? _cachedHardwareEncoder;

    /// <summary>
    /// İlk çağrıda 3 FFmpeg test süreci çalıştırır; sonuç önbelleğe alınır.
    /// Uygulama açılışında arka planda bir kere çağrılmalıdır.
    /// </summary>
    public string DetectBestEncoder(bool preferHardware)
    {
#if DEBUG
        if (preferHardware) LogDirectNvencProbeOnce();
#endif

        if (!preferHardware || _ffmpegPath == null) return "libx264";

        if (_cachedHardwareEncoder != null) return _cachedHardwareEncoder;

        if      (TestEncoder("h264_nvenc")) _cachedHardwareEncoder = "h264_nvenc";
        else if (TestEncoder("h264_amf"))   _cachedHardwareEncoder = "h264_amf";
        else if (TestEncoder("h264_qsv"))   _cachedHardwareEncoder = "h264_qsv";
        else                                _cachedHardwareEncoder = "libx264";

        return _cachedHardwareEncoder;
    }

    /// <summary>Encoder tespitini arka planda asenkron yapar (UI'yi bloklamadan).</summary>
    public Task PrewarmEncoderDetectionAsync(bool preferHardware) =>
        Task.Run(() => DetectBestEncoder(preferHardware));

#if DEBUG
    private void LogDirectNvencProbeOnce()
    {
        if (System.Threading.Interlocked.Exchange(ref _loggedDirectNvencProbe, 1) != 0)
            return;

        Debug.WriteLine(NvencCapabilityProbe.Probe().ToDebugString());
        Debug.WriteLine(NvencSessionProbe.ProbeDefaultD3D11Device().ToDebugString());
        Debug.WriteLine(AmfCapabilityProbe.Probe().ToDebugString());
        Debug.WriteLine(AmfSessionProbe.ProbeDefaultD3D11Device().ToDebugString());
    }
#endif

    private bool TestEncoder(string codec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName    = _ffmpegPath,
                Arguments   = $"-f lavfi -i nullsrc=s=64x64:r=1:d=1 -vcodec {codec} -f null -",
                UseShellExecute        = false,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(6000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ─── Video segmenti kodlama süreci ───────────────────────────────────────
    /// <summary>
    /// Stdin'den raw BGRA frame alıp <paramref name="outputPath"/>'e yazar.
    /// </summary>
    public Process? StartVideoEncoding(
        string encoder, int width, int height, int fps, string quality,
        int outWidth, int outHeight, string outputPath,
        IReadOnlyList<LiveAudioPipeInput>? liveAudioInputs = null,
        bool forceSilentAudio = false,
        bool useWallclockVideoTimestamps = true,
        bool usePerformancePreset = false)
    {
        if (_ffmpegPath == null) return null;

        var quality_ = QualityArg(encoder, quality);
        var preset_  = PresetArg(encoder, usePerformancePreset);
        var tune_    = TuneArg(encoder);
        var gop_     = GopArg(encoder, fps);

        // Add scale filter only when output dims differ from capture dims.
        var vf = (outWidth > 0 && outHeight > 0 && (outWidth != width || outHeight != height))
            ? $"-vf scale={outWidth}:{outHeight}:flags=lanczos "
            : "";

        var liveInputs = liveAudioInputs?.ToList() ?? new List<LiveAudioPipeInput>();
        var hasLiveAudioOutput = liveInputs.Count > 0 || forceSilentAudio;
        var inputArgs = "";
        var filterArgs = "";
        var mapArgs = "";
        var audioCodecArgs = "";
        var shortestArg = "";
        var audioInputIndexes = new List<int>();

        // -thread_queue_size 1024: bumped from the default of 8 so ffmpeg
        // doesn't drop audio packets when its decoder thread momentarily lags
        // behind a WASAPI burst. The "Thread message queue blocking" warning
        // in stderr corresponds to audible cuts in the muxed output.
        const string tqs = "-thread_queue_size 1024 ";

        var inputIndex = 0;
        foreach (var input in liveInputs)
        {
            inputArgs += $"{tqs}{AudioFormatArg(input)} -ar {input.SampleRate} -ac {input.Channels} -i \"{input.PipePath}\" ";
            audioInputIndexes.Add(inputIndex++);
        }

        if (audioInputIndexes.Count == 0 && forceSilentAudio)
        {
            inputArgs += "-f lavfi -i anullsrc=r=48000:cl=stereo ";
            audioInputIndexes.Add(inputIndex++);
            shortestArg = "-shortest ";
        }

        var videoInputIndex = inputIndex;
        var videoClockArgs = useWallclockVideoTimestamps
            ? "-fflags +genpts -use_wallclock_as_timestamps 1 "
            : "";
        var fpsModeArg = useWallclockVideoTimestamps ? "-bf 0 -fps_mode vfr " : "";
        inputArgs += $"{tqs}{videoClockArgs}-f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {fps} -i pipe:0 ";

        // Audio path: always run through aresample async + aformat normalize so
        // sample-clock drift between WASAPI capture and ffmpeg's video clock is
        // corrected on the fly. Before this fix the single-input path skipped
        // the filter and any WASAPI buffer discontinuity (overrun, late start)
        // showed up as cut/choppy playback audio.
        if (audioInputIndexes.Count >= 1)
        {
            var filter = new System.Text.StringBuilder();
            for (var i = 0; i < audioInputIndexes.Count; i++)
            {
                filter.Append($"[{audioInputIndexes[i]}:a]aresample=async=1000:first_pts=0,")
                      .Append("aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo")
                      .Append($"[a{i}];");
            }

            string finalLabel;
            if (audioInputIndexes.Count == 1)
            {
                finalLabel = "a0";
                filterArgs = $"-filter_complex \"{filter.ToString().TrimEnd(';')}\" ";
            }
            else
            {
                filter.Append(string.Concat(Enumerable.Range(0, audioInputIndexes.Count).Select(i => $"[a{i}]")))
                      .Append($"amix=inputs={audioInputIndexes.Count}:duration=longest:dropout_transition=0[aout]");
                finalLabel = "aout";
                filterArgs = $"-filter_complex \"{filter}\" ";
            }

            mapArgs = $"-map {videoInputIndex}:v -map \"[{finalLabel}]\" ";
            audioCodecArgs = "-c:a aac -b:a 320k -ar 48000 -ac 2 ";
        }

        var movFlags = hasLiveAudioOutput ? "" : "-movflags +faststart ";

        // -pix_fmt yuv420p: maksimum uyumluluk
        var args = $"-y " +
                   inputArgs +
                   filterArgs +
                   mapArgs +
                   $"{vf}" +
                   $"-vcodec {encoder} {quality_} {preset_} {tune_} {gop_} -pix_fmt yuv420p " +
                   fpsModeArg +
                   audioCodecArgs +
                   shortestArg +
                   movFlags +
                   $"\"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName               = _ffmpegPath,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        var p = Process.Start(psi);
        // Drain stderr asynchronously to prevent FFmpeg from blocking
        // when its internal stderr buffer fills up.
        if (p != null)
        {
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Debug.WriteLine("[FFmpeg segment] " + e.Data);
            };
            p.BeginErrorReadLine();
        }
        return p;
    }

    private static string AudioFormatArg(LiveAudioPipeInput input)
    {
        if (input.IsFloat) return "-f f32le";
        return input.BitsPerSample switch
        {
            24 => "-f s24le",
            32 => "-f s32le",
            _ => "-f s16le",
        };
    }

    // Probes a segment with ffmpeg itself — no ffprobe dependency. Returns true
    // only when the file has at least one decodable video packet. Catches the
    // "48-byte shell" failure mode where the live-mux ffmpeg exited before any
    // frame reached the muxer (file has ftyp+free atoms but no moov/mdat).
    public async Task<bool> ProbeSegmentAsync(string filePath)
    {
        try
        {
            if (_ffmpegPath == null || !File.Exists(filePath)) return false;
            // A valid MP4 with any encoded frame is at least a few KB even with
            // the highest QP. Anything below 4 KB is almost certainly a shell.
            if (new FileInfo(filePath).Length < 4096) return false;

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-v error -nostdin -i \"{filePath}\" -map 0:v:0 -t 0.5 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            // Drain stderr so the 4KB buffer can't deadlock us on broken files.
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await stderrTask;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Probe] {filePath}: {ex.Message}");
            return false;
        }
    }

    // ─── Video segmentleri birleştir ─────────────────────────────────────────
    public async Task ConcatenateVideoAsync(List<string> segments, string outputPath)
    {
        if (_ffmpegPath == null || segments.Count == 0) return;

        if (segments.Count == 1)
        {
            File.Copy(segments[0], outputPath, true);
            return;
        }

        var dir        = Path.GetDirectoryName(segments[0])!;
        var concatFile = Path.Combine(dir, "concat_video.txt");
        await File.WriteAllLinesAsync(concatFile,
            segments.Select(s => $"file '{s.Replace("\\", "/")}'"));

        await RunFFmpegAsync(
            $"-y -f concat -safe 0 -i \"{concatFile}\" -c copy \"{outputPath}\"");
    }

    // Stitches per-segment live-muxed MP4s into the final file. Tries the fast
    // concat-demuxer copy path first; if segments have mismatched parameter
    // sets (SPS/PPS drift can happen across encoder restarts) the copy concat
    // fails and we fall back to a re-encode with the chosen encoder.
    public async Task<bool> ConcatenateMuxedSegmentsAsync(
        List<string> segments, string outputPath, string? encoderHint = null)
    {
        if (_ffmpegPath == null || segments.Count == 0) return false;

        if (segments.Count == 1)
        {
            File.Copy(segments[0], outputPath, true);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
        }

        var dir = Path.GetDirectoryName(segments[0])!;
        var concatFile = Path.Combine(dir, "concat_muxed.txt");
        await File.WriteAllLinesAsync(concatFile,
            segments.Select(s => $"file '{s.Replace("\\", "/")}'"));

        try
        {
            await RunFFmpegAsync(
                $"-y -f concat -safe 0 -i \"{concatFile}\" -map 0 -c copy " +
                $"-movflags +faststart \"{outputPath}\"");
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                return true;
            Debug.WriteLine("[Concat] Copy path produced 0/tiny output, will remux.");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"[Concat] Copy concat failed, falling back to remux: {ex.Message}");
        }

        // Re-encode fallback. Use the encoder we recorded with so a user on
        // nvenc doesn't get a sudden libx264 slowdown for the rare bad-concat
        // case. Audio is already in AAC inside each segment — copy it.
        var enc = string.IsNullOrEmpty(encoderHint) ? "libx264" : encoderHint;
        var rateCtl = enc == "libx264" ? "-crf 20 -preset medium"
                    : enc == "h264_nvenc" ? "-rc vbr -cq 21 -b:v 0 -preset p5"
                    : enc == "h264_amf"   ? "-rc cqp -qp_i 20 -qp_p 20 -quality quality"
                    : enc == "h264_qsv"   ? "-preset slower -global_quality 21"
                    : "-crf 20 -preset medium";

        await RunFFmpegAsync(
            $"-y -f concat -safe 0 -i \"{concatFile}\" " +
            $"-c:v {enc} {rateCtl} -bf 0 -fps_mode vfr -pix_fmt yuv420p " +
            $"-c:a aac -b:a 320k -ar 48000 -ac 2 " +
            $"-movflags +faststart \"{outputPath}\"");
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
    }

    // ─── Ses segmentleri birleştir ───────────────────────────────────────────
    // Normalizes each segment (sample-rate / channels / bit-depth) so segments
    // from devices that changed format mid-recording (e.g. Bluetooth A2DP↔HFP,
    // default-device switch) still concat into a usable file. Empty / header-
    // only WAVs are skipped so a single failed segment can't poison the result.
    public async Task<string?> ConcatenateAudioAsync(List<string> files, string outputPath)
    {
        if (_ffmpegPath == null) return null;

        // Drop empty or header-only files (WAV header is 44 bytes).
        var valid = files
            .Where(f => File.Exists(f) && new FileInfo(f).Length > 200)
            .ToList();

        if (valid.Count == 0) return null;

        if (valid.Count == 1)
        {
            // Even single-file path: normalize to a known PCM format so the
            // downstream MergeVideoAudioAsync step never trips over format drift.
            await RunFFmpegAsync(
                $"-y -i \"{valid[0]}\" " +
                $"-ar 48000 -ac 2 -c:a pcm_s16le \"{outputPath}\"");
            return outputPath;
        }

        var inputs  = string.Join(' ', valid.Select(f => $"-i \"{f}\""));
        var filters = string.Join(';',
            Enumerable.Range(0, valid.Count).Select(i =>
                $"[{i}:a]aresample=async=1000:first_pts=0," +
                $"aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo" +
                $"[a{i}]"));
        var concatIns = string.Concat(Enumerable.Range(0, valid.Count).Select(i => $"[a{i}]"));
        var fullFilter = $"{filters};{concatIns}concat=n={valid.Count}:v=0:a=1[a]";

        await RunFFmpegAsync(
            $"-y {inputs} -filter_complex \"{fullFilter}\" -map \"[a]\" " +
            $"-c:a pcm_s16le \"{outputPath}\"");

        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 200
            ? outputPath
            : null;
    }

    // ─── Tek-pass: video concat + ses concat + amix + AAC + mux ─────────────
    /// <summary>
    /// Single ffmpeg invocation that takes the per-segment video MP4s plus the
    /// per-segment mic/system WAVs and produces the final muxed file. Replaces
    /// what used to be 4 sequential ffmpegs (video concat → mic concat →
    /// system concat → AAC mux), cutting wall-time roughly 2-3× and avoiding
    /// the intermediate WAV writes. Returns true on success.
    /// </summary>
    public async Task<bool> MuxFinalAsync(
        List<string> videoSegments,
        List<string> micSegments,
        List<string> sysSegments,
        string outputPath)
    {
        if (_ffmpegPath == null || videoSegments.Count == 0) return false;

        // Validate audio: skip header-only / missing WAVs (single bad segment
        // poisoning the concat is the bug we already fixed).
        var mic = micSegments.Where(f => File.Exists(f) && new FileInfo(f).Length > 200).ToList();
        var sys = sysSegments.Where(f => File.Exists(f) && new FileInfo(f).Length > 200).ToList();

        // Video input via concat demuxer (input 0).
        var dir = Path.GetDirectoryName(videoSegments[0])!;
        var videoListFile = Path.Combine(dir, "concat_video.txt");
        await File.WriteAllLinesAsync(videoListFile,
            videoSegments.Select(s => $"file '{s.Replace("\\", "/")}'"));

        var sb = new System.Text.StringBuilder();
        sb.Append("-y -f concat -safe 0 -i \"").Append(videoListFile).Append("\" ");

        // All mic + sys WAVs become inputs starting at index 1.
        int inputIdx = 1;
        var micIdx = new List<int>();
        var sysIdx = new List<int>();
        foreach (var f in mic) { sb.Append("-i \"").Append(f).Append("\" "); micIdx.Add(inputIdx++); }
        foreach (var f in sys) { sb.Append("-i \"").Append(f).Append("\" "); sysIdx.Add(inputIdx++); }

        // Build filter graph only when there's audio to handle.
        var hasAudio = mic.Count > 0 || sys.Count > 0;
        if (hasAudio)
        {
            var filter = new System.Text.StringBuilder();

            string BuildBranch(IList<int> idxs, string outLabel)
            {
                // Per-segment normalize → fltp/48k/stereo.
                for (int k = 0; k < idxs.Count; k++)
                {
                    filter.Append($"[{idxs[k]}:a]aresample=async=1000:first_pts=0,")
                          .Append("aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo")
                          .Append($"[n{outLabel}{k}];");
                }
                if (idxs.Count == 1) return $"n{outLabel}0";
                var ins = string.Concat(Enumerable.Range(0, idxs.Count).Select(k => $"[n{outLabel}{k}]"));
                filter.Append($"{ins}concat=n={idxs.Count}:v=0:a=1[c{outLabel}];");
                return $"c{outLabel}";
            }

            string finalLabel;
            if (mic.Count > 0 && sys.Count > 0)
            {
                var ml = BuildBranch(micIdx, "m");
                var sl = BuildBranch(sysIdx, "s");
                filter.Append($"[{ml}][{sl}]amix=inputs=2:duration=longest:dropout_transition=0[aout];");
                finalLabel = "aout";
            }
            else if (mic.Count > 0)
            {
                finalLabel = BuildBranch(micIdx, "m");
            }
            else
            {
                finalLabel = BuildBranch(sysIdx, "s");
            }

            // Trim trailing ';' if present.
            var fStr = filter.ToString().TrimEnd(';');
            sb.Append("-filter_complex \"").Append(fStr).Append("\" ");
            sb.Append("-map 0:v -map \"[").Append(finalLabel).Append("]\" ");
            sb.Append("-c:v copy -c:a aac -b:a 320k -ar 48000 -ac 2 ");
        }
        else
        {
            sb.Append("-map 0:v -c:v copy ");
        }

        sb.Append("-movflags +faststart \"").Append(outputPath).Append('"');

        await RunFFmpegAsync(sb.ToString());
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
    }

    // ─── Video + Ses birleştir ───────────────────────────────────────────────
    public async Task MergeVideoAudioAsync(
        string videoPath, string? micPath, string? systemPath, string outputPath)
    {
        if (_ffmpegPath == null) return;

        bool hasMic    = !string.IsNullOrEmpty(micPath)    && File.Exists(micPath);
        bool hasSystem = !string.IsNullOrEmpty(systemPath) && File.Exists(systemPath);

        if (!hasMic && !hasSystem)
        {
            // Ses yok – sadece kopyala
            File.Copy(videoPath, outputPath, true);
            return;
        }

        string inputs, filter, maps;
        if (hasMic && hasSystem)
        {
            inputs = $"-i \"{micPath}\" -i \"{systemPath}\"";
            filter = "-filter_complex \"[1:a][2:a]amix=inputs=2:duration=first:dropout_transition=0[aout]\"";
            maps   = "-map 0:v -map \"[aout]\"";
        }
        else
        {
            var audioFile = hasMic ? micPath : systemPath;
            inputs = $"-i \"{audioFile}\"";
            filter = "";
            maps   = "-map 0:v -map 1:a";
        }

        // Yüksek kalite AAC ses (320 kbps stereo @ 48 kHz)
        await RunFFmpegAsync(
            $"-y -i \"{videoPath}\" {inputs} {filter} {maps} " +
            $"-vcodec copy -acodec aac -b:a 320k -ar 48000 -ac 2 " +
            $"\"{outputPath}\"");
    }

    // ─── Yardımcı ────────────────────────────────────────────────────────────
    private async Task RunFFmpegAsync(string args)
    {
        if (_ffmpegPath == null)
            throw new InvalidOperationException("FFmpeg bulunamadi.");

        var psi = new ProcessStartInfo
        {
            FileName              = _ffmpegPath,
            Arguments             = args,
            UseShellExecute       = false,
            RedirectStandardError = true,
            CreateNoWindow        = true
        };
        using var p = Process.Start(psi)!;
        var errors = new List<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (errors)
            {
                errors.Add(e.Data);
                if (errors.Count > 40) errors.RemoveAt(0);
            }
            Debug.WriteLine("[FFmpeg] " + e.Data);
        };

        // Must drain stderr before WaitForExit, otherwise FFmpeg blocks
        // when its 4KB stderr buffer fills (deadlock).
        p.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("FFmpeg islemi zaman asimina ugradi.");
        }

        if (p.ExitCode != 0)
        {
            string tail;
            lock (errors) tail = string.Join(Environment.NewLine, errors.TakeLast(12));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(tail)
                    ? $"FFmpeg islemi basarisiz oldu. Cikis kodu: {p.ExitCode}"
                    : $"FFmpeg islemi basarisiz oldu. Cikis kodu: {p.ExitCode}\n{tail}");
        }
    }

    private static string QualityArg(string enc, string q) => enc switch
    {
        // NVENC: VBR + cq target. NVIDIA's guide recommends -rc vbr with -cq
        // over plain constqp for screen content: it lets the encoder spend
        // bits where motion actually happens (e.g. video clips, scrolling)
        // and saves them on the static parts of a UI.
        "h264_nvenc" => q switch
        {
            "Düşük"  => "-rc vbr -cq 28 -b:v 0",
            "Yüksek" => "-rc vbr -cq 15 -b:v 0",
            _        => "-rc vbr -cq 21 -b:v 0"
        },
        "h264_amf"   => q switch
        {
            "Düşük"  => "-rc cqp -qp_i 28 -qp_p 28",
            "Yüksek" => "-rc cqp -qp_i 16 -qp_p 16",
            _        => "-rc cqp -qp_i 20 -qp_p 20"
        },
        // libx264 / qsv: crf — Yüksek=16 görsel olarak kayıpsıza yakın
        _            => q switch { "Düşük" => "-crf 28", "Yüksek" => "-crf 16", _ => "-crf 20" }
    };

    private static string PresetArg(string enc, bool usePerformancePreset) =>
        usePerformancePreset ? PerformancePresetArg(enc) : enc switch
    {
        // NVENC p5: kaliteli, p7: en yüksek kalite ama yavaş — p5 dengeli
        "h264_nvenc" => "-preset p5",
        "h264_amf"   => "-quality quality",
        "h264_qsv"   => "-preset slower",
        _            => "-preset medium"
    };

    private static string PerformancePresetArg(string enc) => enc switch
    {
        "h264_nvenc" => "-preset p3",
        "h264_amf"   => "-quality balanced",
        "h264_qsv"   => "-preset medium",
        _            => "-preset faster"
    };

    // Codec-specific tuning. For screen content the right choices differ from
    // the natural-video defaults: x264's "stillimage" tune protects text
    // edges (psy-rdo / aq-strength), NVENC's "hq" maximises quality for VOD
    // (vs. "ull" which would gut B-frames for low latency we don't need).
    private static string TuneArg(string enc) => enc switch
    {
        "h264_nvenc" => "-tune hq",
        // libx264 + libx265 share the stillimage tune.
        "libx264"    => "-tune stillimage",
        _            => ""
    };

    // GOP / keyframe interval. Default x264 keyint can stretch to 250 which
    // makes scrubbing the final MP4 sluggish. Forcing keyint = 2×fps keeps
    // seek granularity to ~2s with negligible bitrate cost.
    private static string GopArg(string enc, int fps)
    {
        var keyint = Math.Max(60, fps * 2);
        return enc switch
        {
            "h264_nvenc" => $"-g {keyint}",
            "h264_amf"   => $"-g {keyint}",
            "h264_qsv"   => $"-g {keyint}",
            _            => $"-x264-params keyint={keyint}:min-keyint={fps}:scenecut=40"
        };
    }
}
