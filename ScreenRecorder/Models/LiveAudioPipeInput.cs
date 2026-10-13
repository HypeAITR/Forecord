namespace ScreenRecorder.Models;

public sealed record LiveAudioPipeInput(
    string PipePath,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsFloat);
