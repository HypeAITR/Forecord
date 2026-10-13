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
