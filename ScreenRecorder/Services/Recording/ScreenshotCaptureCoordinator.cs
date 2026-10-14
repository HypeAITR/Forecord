using System.IO;
using System.Runtime.InteropServices;
using ScreenRecorder.Services.Diagnostics;

namespace ScreenRecorder.Services.Recording;

internal sealed class ScreenshotCaptureCoordinator
{
    private volatile bool _pending;
    private TaskCompletionSource<(byte[] pixels, int w, int h)>? _tcs;
    private readonly object _lock = new();

    public bool IsPending => _pending;

    public bool TryComplete(byte[] buffer, int width, int height)
    {
        if (!_pending)
            return false;

        _pending = false;
        var tcs = Volatile.Read(ref _tcs);
        if (tcs == null)
            return false;

        var snap = new byte[buffer.Length];
        Buffer.BlockCopy(buffer, 0, snap, 0, buffer.Length);
        return tcs.TrySetResult((snap, width, height));
    }

    public void CancelPending()
    {
        TaskCompletionSource<(byte[] pixels, int w, int h)>? tcs;
        lock (_lock)
        {
            tcs = _tcs;
            _tcs = null;
            _pending = false;
        }

        tcs?.TrySetCanceled();
    }

    public async Task<bool> CaptureAndSaveAsync(string path, bool isRecordingOrPaused)
    {
        if (!isRecordingOrPaused)
            return false;

        var tcs = new TaskCompletionSource<(byte[] pixels, int w, int h)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            if (_tcs != null)
                return false;

            _tcs = tcs;
            _pending = true;
        }

        try
        {
            var (pixels, w, h) = await tcs.Task
                .WaitAsync(TimeSpan.FromMilliseconds(1000))
                .ConfigureAwait(false);

            WriteSnapshotPng(pixels, w, h, path);
            return true;
        }
        catch (TimeoutException)
        {
            tcs.TrySetCanceled();
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            RecordingDiagnostics.ReportError(
                DiagnosticStage.Screenshot,
                $"Screenshot save failed for {path}",
                ex);
            return false;
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_tcs, tcs))
                {
                    _tcs = null;
                    _pending = false;
                }
            }
        }
    }

    private static void WriteSnapshotPng(byte[] pixels, int w, int h, string path)
    {
        using var bmp = new System.Drawing.Bitmap(w, h,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = w * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * rowBytes,
                        IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
