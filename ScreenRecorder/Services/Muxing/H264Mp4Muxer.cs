using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenRecorder.Services.Muxing;

internal sealed class H264Mp4Muxer : IDisposable
{
    private const uint Timescale = 1_000_000;
    private const int FtypBoxSize = 32;
    private const int MdatHeaderSize = 16; // 64-bit mdat header (size=1 + 'mdat' + uint64 size)
    private const long MdatPayloadStartOffset = FtypBoxSize + MdatHeaderSize; // 48 bytes

    private readonly string _outputPath;
    private readonly FileStream _output;
    private readonly object _writeLock = new();
    private readonly int _width;
    private readonly int _height;
    private readonly uint _defaultDuration;
    private readonly List<Sample> _samples = new();
    private readonly List<AudioSample> _audioSamples = new();

    private byte[]? _sps;
    private byte[]? _pps;
    private TimeSpan? _lastPts;
    private bool _audioConfigured;
    private int _audioSampleRate = 48000;
    private int _audioChannels = 2;
    private long _mdatBytesWritten;
    private bool _finished;

    public H264Mp4Muxer(string outputPath, int width, int height, int fps)
    {
        _outputPath = outputPath;
        _width = width;
        _height = height;
        _defaultDuration = ToDurationUnits(TimeSpan.FromSeconds(1.0 / Math.Max(1, fps)));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        _output = new FileStream(
            _outputPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 256 * 1024);

        // Reserve 48 bytes at the start for ftyp (32B) + 64-bit mdat header (16B).
        _output.Write(new byte[MdatPayloadStartOffset]);
    }

    public int SampleCount => _samples.Count;
    public int AudioSampleCount => _audioSamples.Count;

    public void ConfigureAudio(int sampleRate = 48000, int channels = 2)
    {
        lock (_writeLock)
        {
            _audioConfigured = true;
            _audioSampleRate = Math.Max(8000, sampleRate);
            _audioChannels = Math.Clamp(channels, 1, 2);
        }
    }

    public void AddAudioPacket(byte[] rawAacFrame, uint sampleDuration = 1024)
    {
        if (rawAacFrame.Length == 0)
            return;

        // Strip 7-byte or 9-byte ADTS header if the encoder emitted ADTS instead of raw AAC.
        var payloadOffset = 0;
        var payloadLength = rawAacFrame.Length;
        if (payloadLength >= 7 && rawAacFrame[0] == 0xFF && (rawAacFrame[1] & 0xF0) == 0xF0)
        {
            var protectionAbsent = (rawAacFrame[1] & 0x01) != 0;
            var headerLen = protectionAbsent ? 7 : 9;
            if (payloadLength > headerLen)
            {
                payloadOffset = headerLen;
                payloadLength -= headerLen;
            }
        }

        lock (_writeLock)
        {
            if (_finished)
                return;

            if (!_audioConfigured)
                ConfigureAudio(48000, 2);

            var offset = _mdatBytesWritten;
            _output.Write(rawAacFrame, payloadOffset, payloadLength);
            _mdatBytesWritten += payloadLength;
            _audioSamples.Add(new AudioSample(offset, payloadLength, Math.Max(1u, sampleDuration)));
        }
    }

    public void AddAnnexBPacket(byte[] packet, TimeSpan presentationTime, TimeSpan duration, string pictureType)
    {
        if (packet.Length == 0)
            return;

        var sampleBytes = ConvertAnnexBToAvcc(packet, out var sps, out var pps, out var hasIdr);
        if (sampleBytes.Length == 0)
            return;

        var sampleDuration = duration > TimeSpan.Zero
            ? ToDurationUnits(duration)
            : _defaultDuration;
        var key = hasIdr ||
                  pictureType.Equals("IDR", StringComparison.OrdinalIgnoreCase) ||
                  pictureType.Equals("I", StringComparison.OrdinalIgnoreCase);

        lock (_writeLock)
        {
            if (_finished)
                throw new InvalidOperationException("MP4 muxer is already finished.");

            _sps ??= sps;
            _pps ??= pps;

            if (_lastPts.HasValue && presentationTime > _lastPts.Value && _samples.Count > 0)
            {
                var previous = _samples[^1];
                _samples[^1] = previous with
                {
                    Duration = ToDurationUnits(presentationTime - _lastPts.Value)
                };
            }

            var offset = _mdatBytesWritten;
            _output.Write(sampleBytes);
            _mdatBytesWritten += sampleBytes.Length;

            _samples.Add(new Sample(offset, sampleBytes.Length, sampleDuration, key));
            _lastPts = presentationTime;
        }
    }

    public void Finish()
    {
        lock (_writeLock)
        {
            if (_finished) return;
            _finished = true;

            if (_samples.Count == 0)
                throw new InvalidOperationException("Own MP4 muxer did not receive any encoded H.264 samples.");
            if (_sps is not { Length: >= 4 } || _pps is not { Length: > 0 })
                throw new InvalidOperationException("Own MP4 muxer could not find H.264 SPS/PPS in the NVENC stream.");

            var ftyp = BuildFtypBox();
            var mdatDataStart = (ulong)MdatPayloadStartOffset;
            uint videoDuration = 0;
            for (var i = 0; i < _samples.Count; i++)
                videoDuration += _samples[i].Duration;

            var moov = BuildMoov(videoDuration, mdatDataStart);

            // 1. Append moov box directly at the current end of the file (right after mdat payload).
            _output.Write(moov);

            // 2. Seek back to 0 and patch the 48-byte ftyp + 64-bit mdat header in-place (zero copy!).
            _output.Position = 0;
            _output.Write(ftyp);
            WriteUInt32(_output, 1);
            WriteAscii(_output, "mdat");
            WriteUInt64(_output, (ulong)_mdatBytesWritten + MdatHeaderSize);
            _output.Flush();

            Debug.WriteLine(
                $"[OwnMux] wrote MP4 in-place videoSamples={_samples.Count} audioSamples={_audioSamples.Count} bytes={_output.Length} path={_outputPath}");
        }
    }

    private static byte[] BuildFtypBox() =>
        Box("ftyp", payload =>
        {
            WriteAscii(payload, "isom");
            WriteUInt32(payload, 0x00000200);
            WriteAscii(payload, "isom");
            WriteAscii(payload, "iso6");
            WriteAscii(payload, "avc1");
            WriteAscii(payload, "mp41");
        });

    public static bool TryProbeSegmentStructure(string path) =>
        File.Exists(path) && TryParseOwnMp4Segment(path, out _);

    public static bool TryConcatenateSegments(IReadOnlyList<string> segmentPaths, string outputPath)
    {
        if (segmentPaths.Count == 0)
            return false;

        if (segmentPaths.Count == 1)
        {
            File.Copy(segmentPaths[0], outputPath, true);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 256;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            var combinedSamples = new List<Sample>();
            var combinedAudioSamples = new List<AudioSample>();
            byte[]? firstSps = null;
            byte[]? firstPps = null;
            var width = 0;
            var height = 0;
            var audioSampleRate = 48000;
            var audioChannels = 2;
            long totalMdatPayloadBytes = 0;

            using (var output = new FileStream(
                       outputPath,
                       FileMode.Create,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       bufferSize: 256 * 1024))
            {
                output.Write(new byte[MdatPayloadStartOffset]);

                foreach (var segPath in segmentPaths)
                {
                    if (!File.Exists(segPath) || !TryParseOwnMp4Segment(segPath, out var parsed))
                        return false;

                    firstSps ??= parsed.Sps;
                    firstPps ??= parsed.Pps;
                    if (width == 0) width = parsed.Width;
                    if (height == 0) height = parsed.Height;
                    if (parsed.AudioSampleRate > 0) audioSampleRate = parsed.AudioSampleRate;
                    if (parsed.AudioChannels > 0) audioChannels = parsed.AudioChannels;

                    using var segStream = new FileStream(segPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    foreach (var s in parsed.Samples)
                    {
                        var newOffset = totalMdatPayloadBytes;
                        segStream.Position = (long)s.FileOffset;
                        CopyBytes(segStream, output, s.Size);
                        totalMdatPayloadBytes += s.Size;
                        combinedSamples.Add(new Sample(newOffset, s.Size, s.Duration, s.IsKeyframe));
                    }

                    foreach (var a in parsed.AudioSamples)
                    {
                        var newOffset = totalMdatPayloadBytes;
                        segStream.Position = (long)a.FileOffset;
                        CopyBytes(segStream, output, a.Size);
                        totalMdatPayloadBytes += a.Size;
                        combinedAudioSamples.Add(new AudioSample(newOffset, a.Size, a.Duration));
                    }
                }

                if (combinedSamples.Count == 0 || firstSps == null || firstPps == null || width <= 0 || height <= 0)
                    return false;

                uint duration = 0;
                for (var i = 0; i < combinedSamples.Count; i++)
                    duration += combinedSamples[i].Duration;

                var moov = BuildMoovStatic(
                    width,
                    height,
                    firstSps,
                    firstPps,
                    combinedSamples,
                    combinedAudioSamples,
                    audioSampleRate,
                    audioChannels,
                    duration,
                    (ulong)MdatPayloadStartOffset);
                output.Write(moov);

                output.Position = 0;
                output.Write(BuildFtypBox());
                WriteUInt32(output, 1);
                WriteAscii(output, "mdat");
                WriteUInt64(output, (ulong)totalMdatPayloadBytes + MdatHeaderSize);
                output.Flush();
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 256;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OwnMux] TryConcatenateSegments failed: {ex.Message}");
            return false;
        }
    }

    private static byte[] BuildMoovStatic(
        int width,
        int height,
        byte[] sps,
        byte[] pps,
        List<Sample> samples,
        List<AudioSample> audioSamples,
        int audioSampleRate,
        int audioChannels,
        uint duration,
        ulong mdatDataStart)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"forecord_hdr_{Guid.NewGuid():N}.tmp");
        try
        {
            using var muxer = new H264Mp4Muxer(tempPath, width, height, 60);
            muxer._sps = sps;
            muxer._pps = pps;
            muxer._samples.AddRange(samples);
            if (audioSamples.Count > 0)
            {
                muxer.ConfigureAudio(audioSampleRate, audioChannels);
                muxer._audioSamples.AddRange(audioSamples);
            }
            muxer._finished = true;
            return muxer.BuildMoov(duration, mdatDataStart);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void CopyBytes(Stream source, Stream destination, int count)
    {
        var buffer = new byte[Math.Min(65536, Math.Max(4096, count))];
        var remaining = count;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException("Unexpected EOF while copying MP4 sample.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private sealed record ParsedSegment(
        int Width,
        int Height,
        byte[] Sps,
        byte[] Pps,
        List<ParsedSample> Samples,
        List<ParsedAudioSample> AudioSamples,
        int AudioSampleRate,
        int AudioChannels);

    private readonly record struct ParsedSample(
        ulong FileOffset,
        int Size,
        uint Duration,
        bool IsKeyframe);

    private readonly record struct ParsedAudioSample(
        ulong FileOffset,
        int Size,
        uint Duration);

    private static bool TryParseOwnMp4Segment(string path, out ParsedSegment result)
    {
        result = null!;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 128)
            return false;

        // Locate moov box at the end of the file without loading mdat into RAM!
        long offset = 0;
        byte[]? moovBytes = null;
        Span<byte> hdr = stackalloc byte[16];
        while (offset + 8 <= fs.Length)
        {
            fs.Position = offset;
            if (fs.Read(hdr.Slice(0, 8)) != 8)
                return false;

            var size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(0, 4));
            var type = Encoding.ASCII.GetString(hdr.Slice(4, 4));
            long boxSize = size32;
            var headerLen = 8;
            if (size32 == 1)
            {
                if (offset + 16 > fs.Length || fs.Read(hdr.Slice(8, 8)) != 8)
                    return false;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                headerLen = 16;
            }
            else if (size32 == 0)
            {
                boxSize = fs.Length - offset;
            }

            if (boxSize < headerLen || offset + boxSize > fs.Length)
                return false;

            if (type == "moov")
            {
                var moovPayloadLen = checked((int)(boxSize - headerLen));
                moovBytes = new byte[moovPayloadLen];
                fs.Position = offset + headerLen;
                if (fs.Read(moovBytes, 0, moovPayloadLen) != moovPayloadLen)
                    return false;
                break;
            }

            offset += boxSize;
        }

        if (moovBytes == null)
            return false;

        int width = 0, height = 0;
        byte[]? sps = null, pps = null;
        int audioSampleRate = 48000, audioChannels = 2;
        var parsedVideoSamples = new List<ParsedSample>();
        var parsedAudioSamples = new List<ParsedAudioSample>();

        var pos = 0;
        while (pos + 8 <= moovBytes.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(pos, 4));
            var type = Encoding.ASCII.GetString(moovBytes, pos + 4, 4);
            if (size < 8 || pos + size > moovBytes.Length)
                break;

            if (type == "trak")
            {
                var isAudioTrack = false;
                var durations = new List<uint>();
                var sizes = new List<int>();
                var offsets = new List<ulong>();
                var keyframes = new HashSet<int>();

                ParseBoxesRecursive(moovBytes, pos + 8, pos + size, (boxType, payloadPos, payloadLen) =>
                {
                    var span = moovBytes.AsSpan(payloadPos, payloadLen);
                    if (boxType == "hdlr" && payloadLen >= 12)
                    {
                        var handler = Encoding.ASCII.GetString(moovBytes, payloadPos + 8, 4);
                        if (handler == "soun")
                            isAudioTrack = true;
                    }
                    else if (boxType == "stsd" && payloadLen >= 16)
                    {
                        var entryType = Encoding.ASCII.GetString(moovBytes, payloadPos + 12, 4);
                        var entryPayloadPos = payloadPos + 16;
                        var entryPayloadLen = payloadLen - 16;
                        var entrySpan = moovBytes.AsSpan(entryPayloadPos, entryPayloadLen);

                        if (entryType == "avc1" && entryPayloadLen >= 78)
                        {
                            width = BinaryPrimitives.ReadUInt16BigEndian(entrySpan.Slice(24, 2));
                            height = BinaryPrimitives.ReadUInt16BigEndian(entrySpan.Slice(26, 2));
                            if (entryPayloadLen > 86)
                            {
                                var avccPos = entryPayloadPos + 78;
                                var avccLen = (int)BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(avccPos, 4));
                                var avccType = Encoding.ASCII.GetString(moovBytes, avccPos + 4, 4);
                                if (avccType == "avcC" && avccPos + avccLen <= entryPayloadPos + entryPayloadLen)
                                {
                                    var avcc = moovBytes.AsSpan(avccPos + 8, avccLen - 8);
                                    var spsLen = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(6, 2));
                                    sps = avcc.Slice(8, spsLen).ToArray();
                                    var ppsCountPos = 8 + spsLen;
                                    var ppsLen = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(ppsCountPos + 1, 2));
                                    pps = avcc.Slice(ppsCountPos + 3, ppsLen).ToArray();
                                }
                            }
                        }
                        else if (entryType == "mp4a" && entryPayloadLen >= 28)
                        {
                            isAudioTrack = true;
                            audioChannels = BinaryPrimitives.ReadUInt16BigEndian(entrySpan.Slice(16, 2));
                            audioSampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(entrySpan.Slice(24, 4)) >> 16);
                        }
                    }
                    else if (boxType == "stts" && payloadLen >= 8)
                    {
                        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
                        var p = 8;
                        for (var i = 0; i < entryCount && p + 8 <= payloadLen; i++, p += 8)
                        {
                            var count = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(p, 4));
                            var dur = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(p + 4, 4));
                            for (var c = 0; c < count; c++) durations.Add(dur);
                        }
                    }
                    else if (boxType == "stss" && payloadLen >= 8)
                    {
                        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
                        var p = 8;
                        for (var i = 0; i < entryCount && p + 4 <= payloadLen; i++, p += 4)
                            keyframes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(p, 4)) - 1);
                    }
                    else if (boxType == "stsz" && payloadLen >= 12)
                    {
                        var count = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
                        var p = 12;
                        for (var i = 0; i < count && p + 4 <= payloadLen; i++, p += 4)
                            sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(p, 4)));
                    }
                    else if (boxType == "co64" && payloadLen >= 8)
                    {
                        var count = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
                        var p = 8;
                        for (var i = 0; i < count && p + 8 <= payloadLen; i++, p += 8)
                            offsets.Add(BinaryPrimitives.ReadUInt64BigEndian(span.Slice(p, 8)));
                    }
                });

                if (sizes.Count > 0 && sizes.Count == offsets.Count && sizes.Count == durations.Count)
                {
                    if (isAudioTrack)
                    {
                        for (var i = 0; i < sizes.Count; i++)
                            parsedAudioSamples.Add(new ParsedAudioSample(offsets[i], sizes[i], durations[i]));
                    }
                    else
                    {
                        for (var i = 0; i < sizes.Count; i++)
                            parsedVideoSamples.Add(new ParsedSample(offsets[i], sizes[i], durations[i], keyframes.Contains(i)));
                    }
                }
            }

            pos += size;
        }

        if (sps == null || pps == null || parsedVideoSamples.Count == 0)
            return false;

        result = new ParsedSegment(
            width,
            height,
            sps,
            pps,
            parsedVideoSamples,
            parsedAudioSamples,
            audioSampleRate,
            audioChannels);
        return true;
    }

    private static void ParseBoxesRecursive(
        byte[] bytes,
        int start,
        int end,
        Action<string, int, int> onBox)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            var type = Encoding.ASCII.GetString(bytes, pos + 4, 4);
            if (size < 8 || pos + size > end)
                break;

            var payloadPos = pos + 8;
            var payloadLen = size - 8;
            if (type is "trak" or "mdia" or "minf" or "stbl")
                ParseBoxesRecursive(bytes, payloadPos, pos + size, onBox);
            else
                onBox(type, payloadPos, payloadLen);

            pos += size;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_finished && _samples.Count > 0)
                Finish();
        }
        finally
        {
            _output.Dispose();
        }
    }

    private byte[] BuildMoov(uint duration, ulong mdatDataStart) =>
        Box("moov", moov =>
        {
            var hasAudio = _audioConfigured && _audioSamples.Count > 0;
            moov.Write(BuildMvhd(duration, nextTrackId: hasAudio ? 3u : 2u));
            moov.Write(BuildTrak(duration, mdatDataStart));
            if (hasAudio)
                moov.Write(BuildAudioTrak(duration, mdatDataStart));
        });

    private byte[] BuildMvhd(uint duration, uint nextTrackId = 2) =>
        FullBox("mvhd", 0, 0, payload =>
        {
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, Timescale);
            WriteUInt32(payload, duration);
            WriteUInt32(payload, 0x00010000);
            WriteUInt16(payload, 0x0100);
            WriteUInt16(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteMatrix(payload);
            for (var i = 0; i < 6; i++) WriteUInt32(payload, 0);
            WriteUInt32(payload, nextTrackId);
        });

    private byte[] BuildTrak(uint duration, ulong mdatDataStart) =>
        Box("trak", trak =>
        {
            trak.Write(BuildTkhd(duration));
            trak.Write(BuildMdia(duration, mdatDataStart));
        });

    private byte[] BuildTkhd(uint duration) =>
        FullBox("tkhd", 0, 0x000007, payload =>
        {
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 1);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, duration);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt16(payload, 0);
            WriteUInt16(payload, 0);
            WriteUInt16(payload, 0);
            WriteUInt16(payload, 0);
            WriteMatrix(payload);
            WriteUInt32(payload, (uint)_width << 16);
            WriteUInt32(payload, (uint)_height << 16);
        });

    private byte[] BuildMdia(uint duration, ulong mdatDataStart) =>
        Box("mdia", mdia =>
        {
            mdia.Write(BuildMdhd(duration));
            mdia.Write(BuildHdlr());
            mdia.Write(BuildMinf(mdatDataStart));
        });

    private byte[] BuildMdhd(uint duration) =>
        FullBox("mdhd", 0, 0, payload =>
        {
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, Timescale);
            WriteUInt32(payload, duration);
            WriteUInt16(payload, 0x55C4);
            WriteUInt16(payload, 0);
        });

    private static byte[] BuildHdlr() =>
        FullBox("hdlr", 0, 0, payload =>
        {
            WriteUInt32(payload, 0);
            WriteAscii(payload, "vide");
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            WriteUInt32(payload, 0);
            payload.WriteByte(0);
        });

    private byte[] BuildMinf(ulong mdatDataStart) =>
        Box("minf", minf =>
        {
            minf.Write(FullBox("vmhd", 0, 1, payload =>
            {
                WriteUInt16(payload, 0);
                WriteUInt16(payload, 0);
                WriteUInt16(payload, 0);
                WriteUInt16(payload, 0);
            }));
            minf.Write(BuildDinf());
            minf.Write(BuildStbl(mdatDataStart));
        });

    private static byte[] BuildDinf() =>
        Box("dinf", dinf =>
        {
            dinf.Write(FullBox("dref", 0, 0, payload =>
            {
                WriteUInt32(payload, 1);
                payload.Write(FullBox("url ", 0, 1, _ => { }));
            }));
        });

    private byte[] BuildStbl(ulong mdatDataStart) =>
        Box("stbl", stbl =>
        {
            stbl.Write(BuildStsd());
            stbl.Write(BuildStts());
            stbl.Write(BuildStss());
            stbl.Write(BuildStsc());
            stbl.Write(BuildStsz());
            stbl.Write(BuildCo64(mdatDataStart));
        });

    private byte[] BuildStsd() =>
        FullBox("stsd", 0, 0, payload =>
        {
            WriteUInt32(payload, 1);
            payload.Write(Box("avc1", avc1 =>
            {
                avc1.Write(new byte[6]);
                WriteUInt16(avc1, 1);
                WriteUInt16(avc1, 0);
                WriteUInt16(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt32(avc1, 0);
                WriteUInt16(avc1, (ushort)_width);
                WriteUInt16(avc1, (ushort)_height);
                WriteUInt32(avc1, 0x00480000);
                WriteUInt32(avc1, 0x00480000);
                WriteUInt32(avc1, 0);
                WriteUInt16(avc1, 1);
                avc1.Write(new byte[32]);
                WriteUInt16(avc1, 0x0018);
                WriteUInt16(avc1, 0xFFFF);
                avc1.Write(BuildAvcc());
            }));
        });

    private byte[] BuildAvcc() =>
        Box("avcC", avcc =>
        {
            var sps = _sps!;
            var pps = _pps!;
            avcc.WriteByte(1);
            avcc.WriteByte(sps[1]);
            avcc.WriteByte(sps[2]);
            avcc.WriteByte(sps[3]);
            avcc.WriteByte(0xFF);
            avcc.WriteByte(0xE1);
            WriteUInt16(avcc, checked((ushort)sps.Length));
            avcc.Write(sps);
            avcc.WriteByte(1);
            WriteUInt16(avcc, checked((ushort)pps.Length));
            avcc.Write(pps);
        });

    private byte[] BuildStts()
    {
        var entries = new List<(uint Count, uint Duration)>();
        foreach (var sample in _samples)
        {
            if (entries.Count > 0 && entries[^1].Duration == sample.Duration)
            {
                var entry = entries[^1];
                entries[^1] = (entry.Count + 1, entry.Duration);
            }
            else
            {
                entries.Add((1, sample.Duration));
            }
        }

        return FullBox("stts", 0, 0, payload =>
        {
            WriteUInt32(payload, checked((uint)entries.Count));
            foreach (var entry in entries)
            {
                WriteUInt32(payload, entry.Count);
                WriteUInt32(payload, entry.Duration);
            }
        });
    }

    private byte[] BuildStss()
    {
        var keyframes = _samples
            .Select((sample, index) => (sample, index))
            .Where(x => x.sample.IsKeyframe)
            .Select(x => checked((uint)x.index + 1))
            .ToArray();

        return FullBox("stss", 0, 0, payload =>
        {
            WriteUInt32(payload, checked((uint)keyframes.Length));
            foreach (var keyframe in keyframes)
                WriteUInt32(payload, keyframe);
        });
    }

    private static byte[] BuildStsc() =>
        FullBox("stsc", 0, 0, payload =>
        {
            WriteUInt32(payload, 1);
            WriteUInt32(payload, 1);
            WriteUInt32(payload, 1);
            WriteUInt32(payload, 1);
        });

    private byte[] BuildStsz() =>
        FullBox("stsz", 0, 0, payload =>
        {
            WriteUInt32(payload, 0);
            WriteUInt32(payload, checked((uint)_samples.Count));
            foreach (var sample in _samples)
                WriteUInt32(payload, checked((uint)sample.Size));
        });

    private byte[] BuildCo64(ulong mdatDataStart) =>
        FullBox("co64", 0, 0, payload =>
        {
            WriteUInt32(payload, checked((uint)_samples.Count));
            foreach (var sample in _samples)
                WriteUInt64(payload, mdatDataStart + (ulong)sample.Offset);
        });

    private byte[] BuildAudioTrak(uint movieDuration, ulong mdatDataStart)
    {
        uint audioTotalSamples = 0;
        for (var i = 0; i < _audioSamples.Count; i++)
            audioTotalSamples += _audioSamples[i].Duration;

        var tkhdDuration = (uint)Math.Min(
            uint.MaxValue,
            Math.Round((double)audioTotalSamples * Timescale / Math.Max(1, _audioSampleRate)));
        if (tkhdDuration == 0)
            tkhdDuration = movieDuration;

        return Box("trak", trak =>
        {
            trak.Write(FullBox("tkhd", 0, 0x000007, payload =>
            {
                WriteUInt32(payload, 0);
                WriteUInt32(payload, 0);
                WriteUInt32(payload, 2); // track_ID = 2
                WriteUInt32(payload, 0);
                WriteUInt32(payload, tkhdDuration);
                WriteUInt32(payload, 0);
                WriteUInt32(payload, 0);
                WriteUInt16(payload, 0);
                WriteUInt16(payload, 0);
                WriteUInt16(payload, 0x0100); // volume = 1.0
                WriteUInt16(payload, 0);
                WriteMatrix(payload);
                WriteUInt32(payload, 0);
                WriteUInt32(payload, 0);
            }));

            trak.Write(Box("mdia", mdia =>
            {
                mdia.Write(FullBox("mdhd", 0, 0, payload =>
                {
                    WriteUInt32(payload, 0);
                    WriteUInt32(payload, 0);
                    WriteUInt32(payload, (uint)_audioSampleRate);
                    WriteUInt32(payload, audioTotalSamples);
                    WriteUInt16(payload, 0x55C4);
                    WriteUInt16(payload, 0);
                }));
                mdia.Write(FullBox("hdlr", 0, 0, payload =>
                {
                    WriteUInt32(payload, 0);
                    WriteAscii(payload, "soun");
                    WriteUInt32(payload, 0);
                    WriteUInt32(payload, 0);
                    WriteUInt32(payload, 0);
                    payload.WriteByte(0);
                }));
                mdia.Write(Box("minf", minf =>
                {
                    minf.Write(FullBox("smhd", 0, 0, payload =>
                    {
                        WriteUInt16(payload, 0);
                        WriteUInt16(payload, 0);
                    }));
                    minf.Write(BuildDinf());
                    minf.Write(BuildAudioStbl(mdatDataStart));
                }));
            }));
        });
    }

    private byte[] BuildAudioStbl(ulong mdatDataStart) =>
        Box("stbl", stbl =>
        {
            stbl.Write(BuildAudioStsd());
            stbl.Write(BuildAudioStts());
            stbl.Write(BuildStsc());
            stbl.Write(BuildAudioStsz());
            stbl.Write(BuildAudioCo64(mdatDataStart));
        });

    private byte[] BuildAudioStsd() =>
        FullBox("stsd", 0, 0, payload =>
        {
            WriteUInt32(payload, 1);
            payload.Write(Box("mp4a", mp4a =>
            {
                mp4a.Write(new byte[6]);
                WriteUInt16(mp4a, 1); // data_reference_index
                WriteUInt32(mp4a, 0);
                WriteUInt32(mp4a, 0);
                WriteUInt16(mp4a, (ushort)_audioChannels);
                WriteUInt16(mp4a, 16); // sampleSize
                WriteUInt16(mp4a, 0);
                WriteUInt16(mp4a, 0);
                WriteUInt32(mp4a, (uint)_audioSampleRate << 16);
                mp4a.Write(BuildEsdsBox());
            }));
        });

    private byte[] BuildEsdsBox()
    {
        var asc = BuildAacAudioSpecificConfig(_audioSampleRate, _audioChannels);
        return FullBox("esds", 0, 0, esds =>
        {
            // ES_Descriptor (tag 0x03)
            var decSpecificLen = 2 + asc.Length;
            var decConfigLen = 13 + decSpecificLen;
            var slConfigLen = 3;
            var esDescPayloadLen = 3 + (2 + decConfigLen) + slConfigLen;

            esds.WriteByte(0x03);
            esds.WriteByte((byte)esDescPayloadLen);
            WriteUInt16(esds, 2); // ES_ID = 2
            esds.WriteByte(0x00); // streamDependenceFlag=0

            // DecoderConfigDescriptor (tag 0x04)
            esds.WriteByte(0x04);
            esds.WriteByte((byte)decConfigLen);
            esds.WriteByte(0x40); // objectTypeIndication = 0x40 (Audio ISO/IEC 14496-3)
            esds.WriteByte(0x15); // streamType = 0x05 (AudioStream) << 2 | 1
            esds.WriteByte(0x00);
            esds.WriteByte(0x00);
            esds.WriteByte(0x00); // bufferSizeDB
            WriteUInt32(esds, 192000); // maxBitrate
            WriteUInt32(esds, 192000); // avgBitrate

            // DecoderSpecificInfo (tag 0x05)
            esds.WriteByte(0x05);
            esds.WriteByte((byte)asc.Length);
            esds.Write(asc);

            // SLConfigDescriptor (tag 0x06)
            esds.WriteByte(0x06);
            esds.WriteByte(0x01);
            esds.WriteByte(0x02);
        });
    }

    private static byte[] BuildAacAudioSpecificConfig(int sampleRate, int channels)
    {
        var freqIdx = sampleRate switch
        {
            96000 => 0,
            88200 => 1,
            64000 => 2,
            48000 => 3,
            44100 => 4,
            32000 => 5,
            24000 => 6,
            22050 => 7,
            16000 => 8,
            12000 => 9,
            11025 => 10,
            8000  => 11,
            _     => 3
        };
        var chCfg = Math.Clamp(channels, 1, 2);
        // AAC-LC (audioObjectType = 2): 5 bits aot | 4 bits freqIdx | 4 bits chCfg | 3 bits 0
        var val = (2 << 11) | (freqIdx << 7) | (chCfg << 3);
        return new[] { (byte)((val >> 8) & 0xFF), (byte)(val & 0xFF) };
    }

    private byte[] BuildAudioStts()
    {
        var entries = new List<(uint Count, uint Duration)>();
        foreach (var sample in _audioSamples)
        {
            if (entries.Count > 0 && entries[^1].Duration == sample.Duration)
            {
                var entry = entries[^1];
                entries[^1] = (entry.Count + 1, entry.Duration);
            }
            else
            {
                entries.Add((1, sample.Duration));
            }
        }

        return FullBox("stts", 0, 0, payload =>
        {
            WriteUInt32(payload, checked((uint)entries.Count));
            foreach (var entry in entries)
            {
                WriteUInt32(payload, entry.Count);
                WriteUInt32(payload, entry.Duration);
            }
        });
    }

    private byte[] BuildAudioStsz() =>
        FullBox("stsz", 0, 0, payload =>
        {
            WriteUInt32(payload, 0);
            WriteUInt32(payload, checked((uint)_audioSamples.Count));
            foreach (var sample in _audioSamples)
                WriteUInt32(payload, checked((uint)sample.Size));
        });

    private byte[] BuildAudioCo64(ulong mdatDataStart) =>
        FullBox("co64", 0, 0, payload =>
        {
            WriteUInt32(payload, checked((uint)_audioSamples.Count));
            foreach (var sample in _audioSamples)
                WriteUInt64(payload, mdatDataStart + (ulong)sample.Offset);
        });

    private static byte[] ConvertAnnexBToAvcc(
        byte[] packet,
        out byte[]? sps,
        out byte[]? pps,
        out bool hasIdr)
    {
        sps = null;
        pps = null;
        hasIdr = false;

        var starts = FindStartCodes(packet);
        if (starts.Count == 0)
            return packet;

        using var output = new MemoryStream(packet.Length);
        for (var i = 0; i < starts.Count; i++)
        {
            var payloadStart = starts[i].PayloadStart;
            var payloadEnd = i + 1 < starts.Count ? starts[i + 1].Start : packet.Length;
            while (payloadEnd > payloadStart && packet[payloadEnd - 1] == 0)
                payloadEnd--;

            var length = payloadEnd - payloadStart;
            if (length <= 0)
                continue;

            var type = packet[payloadStart] & 0x1F;
            var nal = packet.AsSpan(payloadStart, length).ToArray();
            if (type == 7)
            {
                sps ??= nal;
                continue;
            }
            if (type == 8)
            {
                pps ??= nal;
                continue;
            }

            if (type == 5)
                hasIdr = true;

            WriteUInt32(output, checked((uint)length));
            output.Write(packet, payloadStart, length);
        }

        return output.ToArray();
    }

    private static List<StartCode> FindStartCodes(byte[] data)
    {
        var starts = new List<StartCode>();
        for (var i = 0; i <= data.Length - 3; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                continue;

            if (data[i + 2] == 1)
            {
                starts.Add(new StartCode(i, i + 3));
                i += 2;
            }
            else if (i <= data.Length - 4 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                starts.Add(new StartCode(i, i + 4));
                i += 3;
            }
        }

        return starts;
    }

    private static byte[] Box(string type, Action<MemoryStream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        using var box = new MemoryStream();
        WriteUInt32(box, checked((uint)payload.Length + 8));
        WriteAscii(box, type);
        payload.Position = 0;
        payload.CopyTo(box);
        return box.ToArray();
    }

    private static byte[] FullBox(string type, byte version, int flags, Action<MemoryStream> writePayload) =>
        Box(type, payload =>
        {
            payload.WriteByte(version);
            payload.WriteByte((byte)((flags >> 16) & 0xFF));
            payload.WriteByte((byte)((flags >> 8) & 0xFF));
            payload.WriteByte((byte)(flags & 0xFF));
            writePayload(payload);
        });

    private static uint ToDurationUnits(TimeSpan value) =>
        checked((uint)Math.Max(1, Math.Round(value.TotalSeconds * Timescale)));

    private static void WriteMatrix(Stream stream)
    {
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x40000000);
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private readonly record struct Sample(long Offset, int Size, uint Duration, bool IsKeyframe);
    private readonly record struct AudioSample(long Offset, int Size, uint Duration);

    private readonly record struct StartCode(int Start, int PayloadStart);
}
