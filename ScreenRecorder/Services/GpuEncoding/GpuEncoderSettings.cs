namespace ScreenRecorder.Services.GpuEncoding;

public sealed record GpuEncoderSettings(
    int Width,
    int Height,
    int Fps,
    string Quality,
    string OutputPath);
