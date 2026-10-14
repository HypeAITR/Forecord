using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenRecorder.Services.Muxing;

internal sealed class H264Mp4Muxer : IDisposable
{
    private const uint Timescale = 1_000_000;

    private readonly string _outputPath;
    private readonly string _mdatPath;
    private readonly FileStream _mdat;
    private readonly int _width;
    private readonly int _height;
    private readonly uint _defaultDuration;
    private readonly List<Sample> _samples = new();

    private byte[]? _sps;
    private byte[]? _pps;
    private TimeSpan? _lastPts;
    private bool _finished;

    public H264Mp4Muxer(string outputPath, int width, int height, int fps)
    {
        _outputPath = outputPath;
        _mdatPath = outputPath + ".mdat.tmp";
        _width = width;
        _height = height;
        _defaultDuration = ToDurationUnits(TimeSpan.FromSeconds(1.0 / Math.Max(1, fps)));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        _mdat = new FileStream(_mdatPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    }

    public int SampleCount => _samples.Count;

    public void AddAnnexBPacket(byte[] packet, TimeSpan presentationTime, TimeSpan duration, string pictureType)
    {
        if (_finished)
            throw new InvalidOperationException("MP4 muxer is already finished.");
        if (packet.Length == 0)
            return;

        var sampleBytes = ConvertAnnexBToAvcc(packet, out var sps, out var pps, out var hasIdr);
        if (sampleBytes.Length == 0)
            return;

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

        var offset = _mdat.Position;
        _mdat.Write(sampleBytes);

        var sampleDuration = duration > TimeSpan.Zero
            ? ToDurationUnits(duration)
            : _defaultDuration;
        var key = hasIdr ||
                  pictureType.Equals("IDR", StringComparison.OrdinalIgnoreCase) ||
                  pictureType.Equals("I", StringComparison.OrdinalIgnoreCase);

        _samples.Add(new Sample(offset, sampleBytes.Length, sampleDuration, key));
        _lastPts = presentationTime;
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _mdat.Flush();

        if (_samples.Count == 0)
            throw new InvalidOperationException("Own MP4 muxer did not receive any encoded H.264 samples.");
        if (_sps is not { Length: >= 4 } || _pps is not { Length: > 0 })
            throw new InvalidOperationException("Own MP4 muxer could not find H.264 SPS/PPS in the NVENC stream.");

        var ftyp = Box("ftyp", payload =>
        {
            WriteAscii(payload, "isom");
            WriteUInt32(payload, 0x00000200);
            WriteAscii(payload, "isom");
            WriteAscii(payload, "iso6");
            WriteAscii(payload, "avc1");
            WriteAscii(payload, "mp41");
        });

        var mdatPayloadSize = _mdat.Length;
        var useLargeMdat = mdatPayloadSize + 8 > uint.MaxValue;
        var mdatHeaderSize = useLargeMdat ? 16 : 8;
        var mdatDataStart = (ulong)(ftyp.Length + mdatHeaderSize);
        var duration = _samples.Aggregate(0u, (sum, sample) => sum + sample.Duration);
        var moov = BuildMoov(duration, mdatDataStart);

        using var output = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.Write(ftyp);
        if (useLargeMdat)
        {
            WriteUInt32(output, 1);
            WriteAscii(output, "mdat");
            WriteUInt64(output, (ulong)mdatPayloadSize + 16);
        }
        else
        {
            WriteUInt32(output, checked((uint)mdatPayloadSize + 8));
            WriteAscii(output, "mdat");
        }

        _mdat.Position = 0;
        _mdat.CopyTo(output);
        output.Write(moov);

        Debug.WriteLine(
            $"[OwnMux] wrote MP4 samples={_samples.Count} bytes={new FileInfo(_outputPath).Length} path={_outputPath}");
    }

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
            var tempMdatPath = outputPath + ".concat.mdat.tmp";
            var combinedSamples = new List<Sample>();
            byte[]? firstSps = null;
            byte[]? firstPps = null;
            var width = 0;
            var height = 0;

            try
            {
                using (var mdatOut = new FileStream(tempMdatPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (var segPath in segmentPaths)
                    {
                        if (!File.Exists(segPath) || !TryParseOwnMp4Segment(segPath, out var parsed))
                            return false;

                        firstSps ??= parsed.Sps;
                        firstPps ??= parsed.Pps;
                        if (width == 0) width = parsed.Width;
                        if (height == 0) height = parsed.Height;

                        using var segStream = new FileStream(segPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        foreach (var s in parsed.Samples)
                        {
                            var newOffset = mdatOut.Position;
                            segStream.Position = (long)s.FileOffset;
                            CopyBytes(segStream, mdatOut, s.Size);
                            combinedSamples.Add(new Sample(newOffset, s.Size, s.Duration, s.IsKeyframe));
                        }
                    }

                    mdatOut.Flush();
                    if (combinedSamples.Count == 0 || firstSps == null || firstPps == null || width <= 0 || height <= 0)
                        return false;

                    WriteMp4File(outputPath, mdatOut, combinedSamples, firstSps, firstPps, width, height);
                }

                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 256;
            }
            finally
            {
                try { if (File.Exists(tempMdatPath)) File.Delete(tempMdatPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OwnMux] TryConcatenateSegments failed: {ex.Message}");
            return false;
        }
    }

    private static void WriteMp4File(
        string outputPath,
        FileStream mdat,
        List<Sample> samples,
        byte[] sps,
        byte[] pps,
        int width,
        int height)
    {
        using var muxer = new H264Mp4Muxer(outputPath + ".hdr.tmp", width, height, 60);
        muxer._sps = sps;
        muxer._pps = pps;
        muxer._samples.AddRange(samples);
        muxer._finished = true;

        var ftyp = Box("ftyp", payload =>
        {
            WriteAscii(payload, "isom");
            WriteUInt32(payload, 0x00000200);
            WriteAscii(payload, "isom");
            WriteAscii(payload, "iso6");
            WriteAscii(payload, "avc1");
            WriteAscii(payload, "mp41");
        });

        var mdatPayloadSize = mdat.Length;
        var useLargeMdat = mdatPayloadSize + 8 > uint.MaxValue;
        var mdatHeaderSize = useLargeMdat ? 16 : 8;
        var mdatDataStart = (ulong)(ftyp.Length + mdatHeaderSize);
        var duration = samples.Aggregate(0u, (sum, sample) => sum + sample.Duration);
        var moov = muxer.BuildMoov(duration, mdatDataStart);

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.Write(ftyp);
        if (useLargeMdat)
        {
            WriteUInt32(output, 1);
            WriteAscii(output, "mdat");
            WriteUInt64(output, (ulong)mdatPayloadSize + 16);
        }
        else
        {
            WriteUInt32(output, checked((uint)mdatPayloadSize + 8));
            WriteAscii(output, "mdat");
        }

        mdat.Position = 0;
        mdat.CopyTo(output);
        output.Write(moov);
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
        List<ParsedSample> Samples);

    private readonly record struct ParsedSample(
        ulong FileOffset,
        int Size,
        uint Duration,
        bool IsKeyframe);

    private static bool TryParseOwnMp4Segment(string path, out ParsedSegment result)
    {
        result = null!;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 128)
            return false;

        // Locate moov box at the end of the file
        var offset = 0;
        var moovStart = -1;
        var moovSize = 0;
        while (offset + 8 <= bytes.Length)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            long boxSize = size32;
            var headerLen = 8;
            if (size32 == 1)
            {
                if (offset + 16 > bytes.Length) return false;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
                headerLen = 16;
            }
            else if (size32 == 0)
            {
                boxSize = bytes.Length - offset;
            }

            if (boxSize < headerLen || offset + boxSize > bytes.Length)
                return false;

            if (type == "moov")
            {
                moovStart = offset + headerLen;
                moovSize = (int)(boxSize - headerLen);
                break;
            }

            offset += (int)boxSize;
        }

        if (moovStart < 0)
            return false;

        int width = 0, height = 0;
        byte[]? sps = null, pps = null;
        var durations = new List<uint>();
        var sizes = new List<int>();
        var offsets = new List<ulong>();
        var keyframes = new HashSet<int>();

        ParseBoxesRecursive(bytes, moovStart, moovStart + moovSize, (boxType, payloadPos, payloadLen) =>
        {
            var span = bytes.AsSpan(payloadPos, payloadLen);
            if (boxType == "avc1" && payloadLen >= 78)
            {
                width = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(24, 2));
                height = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(26, 2));
                // avcC is nested inside avc1 at offset 78
                if (payloadLen > 86)
                {
                    var avccPos = payloadPos + 78;
                    var avccLen = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(avccPos, 4));
                    var avccType = Encoding.ASCII.GetString(bytes, avccPos + 4, 4);
                    if (avccType == "avcC" && avccPos + avccLen <= payloadPos + payloadLen)
                    {
                        var avcc = bytes.AsSpan(avccPos + 8, avccLen - 8);
                        var spsLen = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(6, 2));
                        sps = avcc.Slice(8, spsLen).ToArray();
                        var ppsCountPos = 8 + spsLen;
                        var ppsLen = BinaryPrimitives.ReadUInt16BigEndian(avcc.Slice(ppsCountPos + 1, 2));
                        pps = avcc.Slice(ppsCountPos + 3, ppsLen).ToArray();
                    }
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

        if (sps == null || pps == null || sizes.Count == 0 || sizes.Count != offsets.Count || sizes.Count != durations.Count)
            return false;

        var parsedSamples = new List<ParsedSample>(sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
            parsedSamples.Add(new ParsedSample(offsets[i], sizes[i], durations[i], keyframes.Contains(i)));

        result = new ParsedSegment(width, height, sps, pps, parsedSamples);
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
            _mdat.Dispose();
            try { if (File.Exists(_mdatPath)) File.Delete(_mdatPath); } catch { }
        }
    }

    private byte[] BuildMoov(uint duration, ulong mdatDataStart) =>
        Box("moov", moov =>
        {
            moov.Write(BuildMvhd(duration));
            moov.Write(BuildTrak(duration, mdatDataStart));
        });

    private byte[] BuildMvhd(uint duration) =>
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
            WriteUInt32(payload, 2);
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

    private readonly record struct StartCode(int Start, int PayloadStart);
}
