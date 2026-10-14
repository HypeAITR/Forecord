using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ScreenRecorder.Services.Audio;

/// <summary>
/// Lightweight in-memory Windows Media Foundation AAC-LC frame encoder MFT.
/// Accepts 1024-sample 48 kHz 16-bit Stereo PCM chunks and emits raw AAC frames.
/// </summary>
internal sealed class MfAacFrameEncoder : IDisposable
{
    private static readonly Guid ClsidAacMfTransform = new("93AF0C51-2275-45D2-A35B-F2BA21CAED00");
    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatAac = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    private static readonly Guid MfMtAudioSamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    private static readonly Guid MfMtAudioNumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    private static readonly Guid MfMtAudioAvgBytesPerSecond = new("1AAB75C8-CFED-451C-AB95-AC034B8E1731");
    private static readonly Guid MfMtAudioBlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    private static readonly Guid MfMtAacPayloadType = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188");

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

    private IMFTransform? _transform;
    private readonly long _frameDuration100Ns;
    private bool _disposed;

    public MfAacFrameEncoder(int sampleRate = 48000, int channels = 2, int bitrate = 192000)
    {
        _frameDuration100Ns = 1024L * 10_000_000L / Math.Max(1, sampleRate);

        var comType = Type.GetTypeFromCLSID(ClsidAacMfTransform, throwOnError: true)!;
        _transform = (IMFTransform)Activator.CreateInstance(comType)!;

        // 1. Configure PCM input type
        var inputType = MediaFoundationApi.CreateMediaType();
        inputType.SetGUID(MfMtMajorType, MfMediaTypeAudio);
        inputType.SetGUID(MfMtSubtype, MfAudioFormatPcm);
        inputType.SetUINT32(MfMtAudioBitsPerSample, 16);
        inputType.SetUINT32(MfMtAudioSamplesPerSecond, sampleRate);
        inputType.SetUINT32(MfMtAudioNumChannels, channels);
        _transform.SetInputType(0, inputType, 0);
        Marshal.ReleaseComObject(inputType);

        // 2. Select AAC output type using NAudio's MediaFoundationEncoder.SelectMediaType
        var selectedMediaType = MediaFoundationEncoder.SelectMediaType(
            AudioSubtypes.MFAudioFormat_AAC,
            new WaveFormat(sampleRate, 16, channels),
            bitrate);

        if (selectedMediaType == null)
            throw new InvalidOperationException("MediaFoundationEncoder.SelectMediaType returned null for AAC.");

        _transform.SetOutputType(0, selectedMediaType.MediaFoundationObject, 0);

        _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    public void ProcessPcmChunk(byte[] pcmBytes, long frameTime100Ns, Action<byte[]> onAacPacket)
    {
        if (_disposed || _transform == null || pcmBytes.Length == 0)
            return;

        Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(pcmBytes.Length, out var mediaBuffer));
        try
        {
            mediaBuffer.Lock(out var ptr, out _, out _);
            try
            {
                Marshal.Copy(pcmBytes, 0, ptr, pcmBytes.Length);
            }
            finally
            {
                mediaBuffer.Unlock();
            }
            mediaBuffer.SetCurrentLength(pcmBytes.Length);

            Marshal.ThrowExceptionForHR(MFCreateSample(out var inputSample));
            try
            {
                inputSample.AddBuffer(mediaBuffer);
                inputSample.SetSampleTime(frameTime100Ns);
                inputSample.SetSampleDuration(_frameDuration100Ns);
                _transform.ProcessInput(0, inputSample, 0);
            }
            finally
            {
                Marshal.ReleaseComObject(inputSample);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(mediaBuffer);
        }

        DrainAvailableOutput(onAacPacket);
    }

    public void Drain(Action<byte[]> onAacPacket)
    {
        if (_disposed || _transform == null)
            return;

        try
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
            DrainAvailableOutput(onAacPacket);
        }
        catch
        {
        }
    }

    private void DrainAvailableOutput(Action<byte[]> onAacPacket)
    {
        if (_transform == null) return;

        while (true)
        {
            Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(8192, out var outBuffer));
            Marshal.ThrowExceptionForHR(MFCreateSample(out var outSample));
            try
            {
                outSample.AddBuffer(outBuffer);
                var outputBufferStruct = new MFT_OUTPUT_DATA_BUFFER
                {
                    dwStreamID = 0,
                    pSample = outSample,
                    dwStatus = 0,
                    pEvents = null
                };
                var buffers = new[] { outputBufferStruct };

                var hr = _transform.ProcessOutput(0, 1, buffers, out _);
                if (hr == MfETransformNeedMoreInput)
                    break;

                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                outSample.ConvertToContiguousBuffer(out var contiguous);
                try
                {
                    contiguous.Lock(out var dataPtr, out _, out var currentLen);
                    try
                    {
                        if (currentLen > 0 && dataPtr != IntPtr.Zero)
                        {
                            var packet = new byte[currentLen];
                            Marshal.Copy(dataPtr, packet, 0, currentLen);
                            onAacPacket(packet);
                        }
                    }
                    finally
                    {
                        contiguous.Unlock();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(contiguous);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(outSample);
                Marshal.ReleaseComObject(outBuffer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_transform != null)
        {
            try { _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_transform); } catch { }
            _transform = null;
        }
    }
}
