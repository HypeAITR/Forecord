using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace ScreenRecorder.Helpers;

public sealed class DesktopCapture : IDisposable
{
    private Device              _device    = null!;
    private Output1             _output1   = null!;
    private OutputDuplication   _duplication = null!;
    private Texture2D           _stagingTex  = null!;
    private bool                _hasFullFrame;
    private RawRectangle[]      _dirtyRects = Array.Empty<RawRectangle>();
    private OutputDuplicateMoveRectangle[] _moveRects = Array.Empty<OutputDuplicateMoveRectangle>();
    private byte[]?             _moveScratch;

    // Cursor compositing state. Desktop Duplication API delivers the cursor
    // separately from the frame (it's a hardware sprite on the GPU); we have
    // to blend it ourselves on every captured frame so it shows up in video.
    private byte[]? _cursorShape;
    private int     _cursorShapeW;
    private int     _cursorShapeH;
    private int     _cursorShapeType; // 1 = monochrome, 2 = color, 4 = masked color
    private int     _cursorHotSpotX;
    private int     _cursorHotSpotY;
    private int     _cursorX;
    private int     _cursorY;
    private bool    _cursorVisible;
    private byte[]? _cursorRestore;
    private int     _cursorRestoreX;
    private int     _cursorRestoreY;
    private int     _cursorRestoreW;
    private int     _cursorRestoreH;
    private bool    _cursorRestoreActive;
    private byte[]? _gpuCursorPatch;
    private int     _desktopLeft;
    private int     _desktopTop;

    public int Width  { get; private set; }
    public int Height { get; private set; }
    public Device Device => _device;
    public DesktopAdapterInfo AdapterInfo { get; private set; } =
        new("", 0, 0);

    public DesktopCapture(int monitorIndex = 0)
    {
        Initialize(monitorIndex);
    }

    private void Initialize(int monitorIndex)
    {
        _device = new Device(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });

        using var dxgiDev = _device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDev.GetParent<Adapter>();
        AdapterInfo = DesktopAdapterInfo.FromDescription(adapter.Description);

        using var output = adapter.GetOutput(monitorIndex);
        _output1 = output.QueryInterface<Output1>();

        var bounds = output.Description.DesktopBounds;
        _desktopLeft = bounds.Left;
        _desktopTop  = bounds.Top;
        Width  = bounds.Right  - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        _stagingTex = new Texture2D(_device, new Texture2DDescription
        {
            Width             = Width,
            Height            = Height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            CpuAccessFlags    = CpuAccessFlags.Read,
            BindFlags         = BindFlags.None,
            OptionFlags       = ResourceOptionFlags.None
        });

        _duplication = _output1.DuplicateOutput(_device);
    }

    public bool CaptureInitialFrameFallback(byte[] buffer)
    {
        if (Width <= 0 || Height <= 0 || buffer.Length < Width * Height * 4)
            return false;

        try
        {
            using var bmp = new System.Drawing.Bitmap(
                Width,
                Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(
                    _desktopLeft,
                    _desktopTop,
                    0,
                    0,
                    new System.Drawing.Size(Width, Height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, Width, Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = Width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, buffer, 0, rowBytes * Height);
                }
                else
                {
                    for (var y = 0; y < Height; y++)
                        Marshal.Copy(
                            IntPtr.Add(data.Scan0, y * data.Stride),
                            buffer,
                            y * rowBytes,
                            rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static DesktopAdapterInfo GetDefaultAdapterInfo()
    {
        using var device = new Device(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });

        using var dxgiDev = device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDev.GetParent<Adapter>();
        return DesktopAdapterInfo.FromDescription(adapter.Description);
    }

    public DesktopFrameResult TryGetFrame(
        byte[] buffer,
        int timeoutMs = 16,
        bool skipDuplicateReadback = false)
    {
        SharpDX.DXGI.Resource? resource = null;
        try
        {
            var hr = _duplication.TryAcquireNextFrame(timeoutMs,
                         out var frameInfo, out resource);

            if (hr.Failure || resource == null) return DesktopFrameResult.None;

            var metadata = ReadFrameMetadata(frameInfo);
            var hasImageUpdate = metadata.LastPresentTime != 0;

            // Update cursor state from frame info (always — even when the
            // image hasn't changed, the cursor may have moved).
            var cursorBefore = CaptureCursorSnapshot();
            UpdateCursorState(frameInfo);
            var cursorChanged = HasCursorStateChanged(
                cursorBefore,
                frameInfo.PointerShapeBufferSize > 0);

            if (skipDuplicateReadback && !hasImageUpdate && _hasFullFrame)
                return new DesktopFrameResult(
                    acquired: true,
                    pixelsCopied: false,
                    cursorChanged: cursorChanged,
                    metadata: metadata);

            using var tex = resource.QueryInterface<Texture2D>();
            // SharpDX CopyResource(source, destination): ekran texture'ından CPU staging'e
            return CopyFrameToBuffer(tex, metadata, buffer, cursorChanged, skipDuplicateReadback);

        }
        catch (SharpDX.SharpDXException ex)
            when (ex.ResultCode.Code == unchecked((int)0x887A0026) ||
                  ex.ResultCode.Code == unchecked((int)0x887A0005) ||
                  ex.ResultCode.Code == unchecked((int)0x887A0007))
        {
            throw new DeviceLostException("DXGI cihazı kayboldu.", ex);
        }
        catch { return DesktopFrameResult.None; }
        finally
        {
            resource?.Dispose();
            try { _duplication.ReleaseFrame(); } catch { }
        }
    }

    public bool TryAcquireGpuFrame(
        out DesktopGpuFrame? frame,
        int timeoutMs = 16)
    {
        frame = null;
        SharpDX.DXGI.Resource? resource = null;
        var acquired = false;
        var ownershipTransferred = false;

        try
        {
            var hr = _duplication.TryAcquireNextFrame(timeoutMs,
                         out var frameInfo, out resource);

            if (hr.Failure || resource == null)
                return false;

            acquired = true;
            var metadata = ReadFrameMetadata(frameInfo);
            var cursorBefore = CaptureCursorSnapshot();
            UpdateCursorState(frameInfo);
            var cursorChanged = HasCursorStateChanged(
                cursorBefore,
                frameInfo.PointerShapeBufferSize > 0);

            var texture = resource.QueryInterface<Texture2D>();
            frame = new DesktopGpuFrame(
                this,
                resource,
                texture,
                metadata,
                cursorChanged,
                Width,
                Height);
            ownershipTransferred = true;
            resource = null;
            return true;
        }
        catch (SharpDX.SharpDXException ex)
            when (ex.ResultCode.Code == unchecked((int)0x887A0026) ||
                  ex.ResultCode.Code == unchecked((int)0x887A0005) ||
                  ex.ResultCode.Code == unchecked((int)0x887A0007))
        {
            throw new DeviceLostException("DXGI cihazÄ± kayboldu.", ex);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                resource?.Dispose();
                if (acquired)
                    ReleaseAcquiredFrame();
            }
        }
    }

    private DesktopFrameResult CopyFrameToBuffer(
        Texture2D texture,
        DesktopFrameMetadata metadata,
        byte[] buffer,
        bool cursorChanged,
        bool allowPartialReadback)
    {
        if (allowPartialReadback &&
            _hasFullFrame &&
            (metadata.MoveRectCount > 0 || metadata.DirtyRectCount > 0))
        {
            var moveCount = metadata.MoveRectCount;
            var dirtyCount = metadata.DirtyRectCount;
            var dirtyPixels = metadata.DirtyPixels;
            var dirtyThreshold = (long)Width * Height * 3 / 4;

            if ((moveCount > 0 || dirtyCount > 0) &&
                dirtyCount <= 128 &&
                dirtyPixels <= dirtyThreshold)
            {
                try
                {
                    ApplyMoveRects(buffer, moveCount);
                    if (dirtyCount > 0)
                        CopyDirtyRects(texture, buffer, dirtyCount);

                    return new DesktopFrameResult(
                        acquired: true,
                        pixelsCopied: true,
                        cursorChanged: cursorChanged,
                        metadata: metadata,
                        partialUpdate: true,
                        readbackPixels: dirtyPixels);
                }
                catch
                {
                    // Metadata can be coalesced or stale after rare driver
                    // edges. A full copy is slower but preserves correctness.
                }
            }
        }

        CopyFullFrame(texture, buffer);
        _hasFullFrame = true;
        return new DesktopFrameResult(
            acquired: true,
            pixelsCopied: true,
            cursorChanged: cursorChanged,
            metadata: metadata,
            partialUpdate: false,
            readbackPixels: (long)Width * Height);
    }

    private DesktopFrameMetadata ReadFrameMetadata(
        OutputDuplicateFrameInformation frameInfo)
    {
        var moveCount = 0;
        var dirtyCount = 0;
        long dirtyPixels = 0;

        if (frameInfo.TotalMetadataBufferSize > 0)
        {
            try
            {
                var moveSize = Marshal.SizeOf<OutputDuplicateMoveRectangle>();
                var dirtySize = Marshal.SizeOf<RawRectangle>();
                EnsureMoveRectCapacity(Math.Max(1, frameInfo.TotalMetadataBufferSize / moveSize));
                EnsureDirtyRectCapacity(Math.Max(1, frameInfo.TotalMetadataBufferSize / dirtySize));

                var moveBytes = 0;
                _duplication.GetFrameMoveRects(_moveRects.Length * moveSize, _moveRects, out moveBytes);
                moveCount = Math.Min(_moveRects.Length, moveBytes / moveSize);

                var dirtyBytes = 0;
                _duplication.GetFrameDirtyRects(_dirtyRects.Length * dirtySize, _dirtyRects, out dirtyBytes);
                dirtyCount = Math.Min(_dirtyRects.Length, dirtyBytes / dirtySize);
                dirtyPixels = CountDirtyPixels(dirtyCount);
            }
            catch
            {
                moveCount = 0;
                dirtyCount = 0;
                dirtyPixels = 0;
            }
        }

        return new DesktopFrameMetadata(
            frameInfo.LastPresentTime,
            frameInfo.LastMouseUpdateTime,
            frameInfo.AccumulatedFrames,
            moveCount,
            dirtyCount,
            dirtyPixels,
            frameInfo.TotalMetadataBufferSize,
            frameInfo.PointerShapeBufferSize);
    }

    private void EnsureMoveRectCapacity(int count)
    {
        if (_moveRects.Length < count)
            _moveRects = new OutputDuplicateMoveRectangle[count];
    }

    private void EnsureDirtyRectCapacity(int count)
    {
        if (_dirtyRects.Length < count)
            _dirtyRects = new RawRectangle[count];
    }

    private long CountDirtyPixels(int dirtyCount)
    {
        long pixels = 0;
        for (var i = 0; i < dirtyCount; i++)
        {
            if (TryClipRect(_dirtyRects[i], out var left, out var top, out var right, out var bottom))
                pixels += (long)(right - left) * (bottom - top);
        }

        return pixels;
    }

    private void ApplyMoveRects(byte[] buffer, int moveCount)
    {
        for (var i = 0; i < moveCount; i++)
            ApplyMoveRect(buffer, _moveRects[i]);
    }

    private void ApplyMoveRect(byte[] buffer, OutputDuplicateMoveRectangle move)
    {
        var rect = move.DestinationRect;
        if (!TryClipRect(rect, out var left, out var top, out var right, out var bottom))
            return;

        var sourceX = move.SourcePoint.X + (left - rect.Left);
        var sourceY = move.SourcePoint.Y + (top - rect.Top);

        if (sourceX < 0)
        {
            left -= sourceX;
            sourceX = 0;
        }
        if (sourceY < 0)
        {
            top -= sourceY;
            sourceY = 0;
        }
        if (sourceX + (right - left) > Width)
            right = left + (Width - sourceX);
        if (sourceY + (bottom - top) > Height)
            bottom = top + (Height - sourceY);

        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0)
            return;

        var rowBytes = w * 4;
        var needed = rowBytes * h;
        if (_moveScratch == null || _moveScratch.Length < needed)
            _moveScratch = new byte[needed];

        var stride = Width * 4;
        for (var y = 0; y < h; y++)
        {
            System.Buffer.BlockCopy(
                buffer,
                (sourceY + y) * stride + sourceX * 4,
                _moveScratch,
                y * rowBytes,
                rowBytes);
        }

        for (var y = 0; y < h; y++)
        {
            System.Buffer.BlockCopy(
                _moveScratch,
                y * rowBytes,
                buffer,
                (top + y) * stride + left * 4,
                rowBytes);
        }
    }

    private void CopyDirtyRects(Texture2D texture, byte[] buffer, int dirtyCount)
    {
        var copied = 0;
        for (var i = 0; i < dirtyCount; i++)
        {
            if (!TryClipRect(_dirtyRects[i], out var left, out var top, out var right, out var bottom))
                continue;

            var region = new ResourceRegion(left, top, 0, right, bottom, 1);
            _device.ImmediateContext.CopySubresourceRegion(
                texture, 0, region, _stagingTex, 0, left, top, 0);
            copied++;
        }

        if (copied == 0)
            return;

        var box = _device.ImmediateContext
                         .MapSubresource(_stagingTex, 0, MapMode.Read, MapFlags.None);
        try
        {
            for (var i = 0; i < dirtyCount; i++)
            {
                if (TryClipRect(_dirtyRects[i], out var left, out var top, out var right, out var bottom))
                    CopyMappedRectToBuffer(box, buffer, left, top, right, bottom);
            }
        }
        finally
        {
            _device.ImmediateContext.UnmapSubresource(_stagingTex, 0);
        }
    }

    private void CopyFullFrame(Texture2D texture, byte[] buffer)
    {
        _device.ImmediateContext.CopyResource(texture, _stagingTex);

        var box = _device.ImmediateContext
                         .MapSubresource(_stagingTex, 0, MapMode.Read, MapFlags.None);
        try
        {
            CopyMappedRectToBuffer(box, buffer, 0, 0, Width, Height);
        }
        finally
        {
            _device.ImmediateContext.UnmapSubresource(_stagingTex, 0);
        }
    }

    private unsafe void CopyMappedRectToBuffer(
        SharpDX.DataBox box,
        byte[] buffer,
        int left,
        int top,
        int right,
        int bottom)
    {
        var rowBytes = (right - left) * 4;
        var dstStride = Width * 4;
        var src = (byte*)box.DataPointer.ToPointer();

        fixed (byte* dst = buffer)
        {
            for (var y = top; y < bottom; y++)
            {
                System.Buffer.MemoryCopy(
                    src + (long)y * box.RowPitch + left * 4,
                    dst + (long)y * dstStride + left * 4,
                    rowBytes,
                    rowBytes);
            }
        }
    }

    private bool TryClipRect(
        RawRectangle rect,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = Math.Clamp(rect.Left, 0, Width);
        top = Math.Clamp(rect.Top, 0, Height);
        right = Math.Clamp(rect.Right, 0, Width);
        bottom = Math.Clamp(rect.Bottom, 0, Height);
        return right > left && bottom > top;
    }

    private void UpdateCursorState(OutputDuplicateFrameInformation info)
    {
        // Position update is delivered every frame the cursor moves.
        if (info.LastMouseUpdateTime != 0)
        {
            _cursorVisible = info.PointerPosition.Visible;
            _cursorX       = info.PointerPosition.Position.X;
            _cursorY       = info.PointerPosition.Position.Y;
        }

        // Shape update only when PointerShapeBufferSize > 0.
        if (info.PointerShapeBufferSize > 0)
        {
            try
            {
                var buf = new byte[info.PointerShapeBufferSize];
                var pinned = System.Runtime.InteropServices.GCHandle.Alloc(buf,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    _duplication.GetFramePointerShape(
                        info.PointerShapeBufferSize,
                        pinned.AddrOfPinnedObject(),
                        out _,
                        out var shapeInfo);
                    _cursorShape     = buf;
                    _cursorShapeW    = shapeInfo.Width;
                    _cursorShapeH    = shapeInfo.Height;
                    _cursorShapeType = shapeInfo.Type;
                    _cursorHotSpotX  = shapeInfo.HotSpot.X;
                    _cursorHotSpotY  = shapeInfo.HotSpot.Y;
                }
                finally { pinned.Free(); }
            }
            catch { /* shape fetch failed — keep previous */ }
        }
    }

    private CursorSnapshot CaptureCursorSnapshot() =>
        new(
            _cursorX,
            _cursorY,
            _cursorVisible,
            _cursorShapeW,
            _cursorShapeH,
            _cursorShapeType,
            _cursorHotSpotX,
            _cursorHotSpotY);

    private bool HasCursorStateChanged(CursorSnapshot before, bool shapeUpdated) =>
        shapeUpdated ||
        before.X != _cursorX ||
        before.Y != _cursorY ||
        before.Visible != _cursorVisible ||
        before.ShapeWidth != _cursorShapeW ||
        before.ShapeHeight != _cursorShapeH ||
        before.ShapeType != _cursorShapeType ||
        before.HotSpotX != _cursorHotSpotX ||
        before.HotSpotY != _cursorHotSpotY;

    /// <summary>
    /// Composites the most recently observed cursor sprite into the BGRA frame
    /// buffer. The capture loop calls this AFTER cloning the buffer for the
    /// screenshot feature, so screenshots stay cursor-free per the user's
    /// request, while the encoded video shows the cursor.
    /// </summary>
    public bool CompositeCursor(byte[] buffer)
    {
        if (!_cursorVisible || _cursorShape == null || _cursorShapeW <= 0 || _cursorShapeH <= 0)
            return false;

        CaptureCursorRestore(buffer);

        switch (_cursorShapeType)
        {
            case 2: BlendColorCursor(buffer); break;        // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR
            case 1: BlendMonochromeCursor(buffer); break;   // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME
            case 4: BlendMaskedColorCursor(buffer); break;  // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR
            default:
                _cursorRestoreActive = false;
                return false;
        }

        return true;
    }

    public void RestoreCursor(byte[] buffer)
    {
        if (!_cursorRestoreActive || _cursorRestore == null ||
            _cursorRestoreW <= 0 || _cursorRestoreH <= 0)
        {
            _cursorRestoreActive = false;
            return;
        }

        var dstStride = Width * 4;
        var srcStride = _cursorRestoreW * 4;
        for (var y = 0; y < _cursorRestoreH; y++)
        {
            System.Buffer.BlockCopy(
                _cursorRestore,
                y * srcStride,
                buffer,
                (_cursorRestoreY + y) * dstStride + _cursorRestoreX * 4,
                srcStride);
        }

        _cursorRestoreActive = false;
    }

    /// <summary>
    /// Reads back the full GPU texture into <paramref name="buffer"/> on demand
    /// (used only when a lazy screenshot is requested in GPU-Direct mode).
    /// </summary>
    public void ReadbackFullTexture(Texture2D texture, byte[] buffer)
    {
        CopyFullFrame(texture, buffer);
    }

    /// <summary>
    /// Blends the hardware cursor onto <paramref name="targetTexture"/> by
    /// reading and updating ONLY the tiny cursor bounding box (e.g. 32x32 px)
    /// instead of copying the full screen texture to CPU.
    /// </summary>
    public unsafe bool CompositeCursorOnGpuTexture(Texture2D sourceTexture, Texture2D targetTexture)
    {
        if (!_cursorVisible || _cursorShape == null || _cursorShapeW <= 0 || _cursorShapeH <= 0)
            return false;

        var cursorH = _cursorShapeType == 1 ? _cursorShapeH / 2 : _cursorShapeH;
        if (cursorH <= 0)
            return false;

        var destX = _cursorX - _cursorHotSpotX;
        var destY = _cursorY - _cursorHotSpotY;
        var clippedX = Math.Max(0, destX);
        var clippedY = Math.Max(0, destY);
        var clippedRight = Math.Min(Width, destX + _cursorShapeW);
        var clippedBottom = Math.Min(Height, destY + cursorH);
        var clippedW = clippedRight - clippedX;
        var clippedH = clippedBottom - clippedY;

        if (clippedW <= 0 || clippedH <= 0)
            return false;

        var patchStride = clippedW * 4;
        var neededBytes = patchStride * clippedH;
        if (_gpuCursorPatch == null || _gpuCursorPatch.Length < neededBytes)
            _gpuCursorPatch = new byte[neededBytes];

        var region = new ResourceRegion(clippedX, clippedY, 0, clippedRight, clippedBottom, 1);
        _device.ImmediateContext.CopySubresourceRegion(
            sourceTexture, 0, region, _stagingTex, 0, clippedX, clippedY, 0);

        var box = _device.ImmediateContext.MapSubresource(_stagingTex, 0, MapMode.Read, MapFlags.None);
        try
        {
            var srcBase = (byte*)box.DataPointer.ToPointer();
            fixed (byte* dstBase = _gpuCursorPatch)
            {
                for (var row = 0; row < clippedH; row++)
                {
                    System.Buffer.MemoryCopy(
                        srcBase + (long)(clippedY + row) * box.RowPitch + clippedX * 4,
                        dstBase + (long)row * patchStride,
                        patchStride,
                        patchStride);
                }
            }
        }
        finally
        {
            _device.ImmediateContext.UnmapSubresource(_stagingTex, 0);
        }

        BlendCursorIntoPatch(_gpuCursorPatch, clippedX, clippedY, clippedW, clippedH, destX, destY, cursorH);

        fixed (byte* patchPtr = _gpuCursorPatch)
        {
            var targetRegion = new ResourceRegion(clippedX, clippedY, 0, clippedRight, clippedBottom, 1);
            _device.ImmediateContext.UpdateSubresource(
                new SharpDX.DataBox((IntPtr)patchPtr, patchStride, neededBytes),
                targetTexture,
                0,
                targetRegion);
        }

        return true;
    }

    private void BlendCursorIntoPatch(
        byte[] patch,
        int clippedX,
        int clippedY,
        int clippedW,
        int clippedH,
        int destX,
        int destY,
        int cursorH)
    {
        var patchStride = clippedW * 4;
        var shapeStride = _cursorShapeW * 4;
        var srcRowBytes = (_cursorShapeW + 7) / 8;

        for (var row = 0; row < clippedH; row++)
        {
            var sy = (clippedY + row) - destY;
            if (sy < 0 || sy >= cursorH) continue;

            for (var col = 0; col < clippedW; col++)
            {
                var sx = (clippedX + col) - destX;
                if (sx < 0 || sx >= _cursorShapeW) continue;

                var dIdx = row * patchStride + col * 4;

                if (_cursorShapeType == 2)
                {
                    var sIdx = sy * shapeStride + sx * 4;
                    var sa = _cursorShape![sIdx + 3];
                    if (sa == 0) continue;
                    var sb = _cursorShape[sIdx + 0];
                    var sg = _cursorShape[sIdx + 1];
                    var sr = _cursorShape[sIdx + 2];
                    if (sa == 255)
                    {
                        patch[dIdx + 0] = sb;
                        patch[dIdx + 1] = sg;
                        patch[dIdx + 2] = sr;
                    }
                    else
                    {
                        var ia = 255 - sa;
                        patch[dIdx + 0] = (byte)((sb * sa + patch[dIdx + 0] * ia) / 255);
                        patch[dIdx + 1] = (byte)((sg * sa + patch[dIdx + 1] * ia) / 255);
                        patch[dIdx + 2] = (byte)((sr * sa + patch[dIdx + 2] * ia) / 255);
                    }
                }
                else if (_cursorShapeType == 4)
                {
                    var sIdx = sy * shapeStride + sx * 4;
                    var sb = _cursorShape![sIdx + 0];
                    var sg = _cursorShape[sIdx + 1];
                    var sr = _cursorShape[sIdx + 2];
                    var sa = _cursorShape[sIdx + 3];
                    if (sa == 0)
                    {
                        patch[dIdx + 0] = sb;
                        patch[dIdx + 1] = sg;
                        patch[dIdx + 2] = sr;
                    }
                    else
                    {
                        patch[dIdx + 0] ^= sb;
                        patch[dIdx + 1] ^= sg;
                        patch[dIdx + 2] ^= sr;
                    }
                }
                else if (_cursorShapeType == 1)
                {
                    var andRow = sy * srcRowBytes;
                    var xorRow = (sy + cursorH) * srcRowBytes;
                    var byteOffset = sx / 8;
                    var bitMask = 0x80 >> (sx % 8);
                    var andBit = (_cursorShape![andRow + byteOffset] & bitMask) != 0 ? 1 : 0;
                    var xorBit = (_cursorShape![xorRow + byteOffset] & bitMask) != 0 ? 1 : 0;
                    if (andBit == 0 && xorBit == 0)
                    {
                        patch[dIdx + 0] = 0;
                        patch[dIdx + 1] = 0;
                        patch[dIdx + 2] = 0;
                    }
                    else if (andBit == 0 && xorBit == 1)
                    {
                        patch[dIdx + 0] = 255;
                        patch[dIdx + 1] = 255;
                        patch[dIdx + 2] = 255;
                    }
                    else if (andBit == 1 && xorBit == 1)
                    {
                        patch[dIdx + 0] = (byte)(255 - patch[dIdx + 0]);
                        patch[dIdx + 1] = (byte)(255 - patch[dIdx + 1]);
                        patch[dIdx + 2] = (byte)(255 - patch[dIdx + 2]);
                    }
                }
            }
        }
    }

    private void CaptureCursorRestore(byte[] buffer)
    {
        _cursorRestoreActive = false;

        var cursorH = _cursorShapeType == 1 ? _cursorShapeH / 2 : _cursorShapeH;
        var x = _cursorX - _cursorHotSpotX;
        var y = _cursorY - _cursorHotSpotY;
        var clippedX = Math.Max(0, x);
        var clippedY = Math.Max(0, y);
        var clippedRight = Math.Min(Width, x + _cursorShapeW);
        var clippedBottom = Math.Min(Height, y + cursorH);
        var clippedW = clippedRight - clippedX;
        var clippedH = clippedBottom - clippedY;

        if (clippedW <= 0 || clippedH <= 0)
            return;

        var needed = clippedW * clippedH * 4;
        if (_cursorRestore == null || _cursorRestore.Length < needed)
            _cursorRestore = new byte[needed];

        var srcStride = Width * 4;
        var dstStride = clippedW * 4;
        for (var row = 0; row < clippedH; row++)
        {
            System.Buffer.BlockCopy(
                buffer,
                (clippedY + row) * srcStride + clippedX * 4,
                _cursorRestore,
                row * dstStride,
                dstStride);
        }

        _cursorRestoreX = clippedX;
        _cursorRestoreY = clippedY;
        _cursorRestoreW = clippedW;
        _cursorRestoreH = clippedH;
        _cursorRestoreActive = true;
    }

    private void BlendColorCursor(byte[] buffer)
    {
        int destX = _cursorX - _cursorHotSpotX;
        int destY = _cursorY - _cursorHotSpotY;
        int dstStride = Width * 4;
        int srcStride = _cursorShapeW * 4;

        for (int y = 0; y < _cursorShapeH; y++)
        {
            int dy = destY + y;
            if (dy < 0 || dy >= Height) continue;
            for (int x = 0; x < _cursorShapeW; x++)
            {
                int dx = destX + x;
                if (dx < 0 || dx >= Width) continue;
                int sIdx = y * srcStride + x * 4;
                int dIdx = dy * dstStride + dx * 4;
                byte sa = _cursorShape![sIdx + 3];
                if (sa == 0) continue;
                byte sb = _cursorShape[sIdx + 0];
                byte sg = _cursorShape[sIdx + 1];
                byte sr = _cursorShape[sIdx + 2];
                if (sa == 255)
                {
                    buffer[dIdx + 0] = sb;
                    buffer[dIdx + 1] = sg;
                    buffer[dIdx + 2] = sr;
                }
                else
                {
                    int ia = 255 - sa;
                    buffer[dIdx + 0] = (byte)((sb * sa + buffer[dIdx + 0] * ia) / 255);
                    buffer[dIdx + 1] = (byte)((sg * sa + buffer[dIdx + 1] * ia) / 255);
                    buffer[dIdx + 2] = (byte)((sr * sa + buffer[dIdx + 2] * ia) / 255);
                }
            }
        }
    }

    private void BlendMaskedColorCursor(byte[] buffer)
    {
        int destX = _cursorX - _cursorHotSpotX;
        int destY = _cursorY - _cursorHotSpotY;
        int dstStride = Width * 4;
        int srcStride = _cursorShapeW * 4;

        for (int y = 0; y < _cursorShapeH; y++)
        {
            int dy = destY + y;
            if (dy < 0 || dy >= Height) continue;
            for (int x = 0; x < _cursorShapeW; x++)
            {
                int dx = destX + x;
                if (dx < 0 || dx >= Width) continue;
                int sIdx = y * srcStride + x * 4;
                int dIdx = dy * dstStride + dx * 4;
                byte sb = _cursorShape![sIdx + 0];
                byte sg = _cursorShape[sIdx + 1];
                byte sr = _cursorShape[sIdx + 2];
                byte sa = _cursorShape[sIdx + 3];
                if (sa == 0)
                {
                    // Replace destination
                    buffer[dIdx + 0] = sb;
                    buffer[dIdx + 1] = sg;
                    buffer[dIdx + 2] = sr;
                }
                else
                {
                    // XOR with destination
                    buffer[dIdx + 0] ^= sb;
                    buffer[dIdx + 1] ^= sg;
                    buffer[dIdx + 2] ^= sr;
                }
            }
        }
    }

    private void BlendMonochromeCursor(byte[] buffer)
    {
        // Monochrome cursor: shape height is 2× actual cursor height.
        // Top half = AND mask, bottom half = XOR mask. Each row is packed bits.
        int actualH   = _cursorShapeH / 2;
        int destX     = _cursorX - _cursorHotSpotX;
        int destY     = _cursorY - _cursorHotSpotY;
        int dstStride = Width * 4;
        int srcRowBytes = (_cursorShapeW + 7) / 8;

        for (int y = 0; y < actualH; y++)
        {
            int dy = destY + y;
            if (dy < 0 || dy >= Height) continue;
            int andRow = y * srcRowBytes;
            int xorRow = (y + actualH) * srcRowBytes;
            for (int x = 0; x < _cursorShapeW; x++)
            {
                int dx = destX + x;
                if (dx < 0 || dx >= Width) continue;
                int byteOffset = x / 8;
                int bitMask    = 0x80 >> (x % 8);
                int andBit = (_cursorShape![andRow + byteOffset] & bitMask) != 0 ? 1 : 0;
                int xorBit = (_cursorShape![xorRow + byteOffset] & bitMask) != 0 ? 1 : 0;
                int dIdx   = dy * dstStride + dx * 4;
                if (andBit == 0 && xorBit == 0)
                {
                    buffer[dIdx + 0] = 0; buffer[dIdx + 1] = 0; buffer[dIdx + 2] = 0;
                }
                else if (andBit == 0 && xorBit == 1)
                {
                    buffer[dIdx + 0] = 255; buffer[dIdx + 1] = 255; buffer[dIdx + 2] = 255;
                }
                else if (andBit == 1 && xorBit == 1)
                {
                    // Invert — approximate as white outline for visibility.
                    buffer[dIdx + 0] = (byte)(255 - buffer[dIdx + 0]);
                    buffer[dIdx + 1] = (byte)(255 - buffer[dIdx + 1]);
                    buffer[dIdx + 2] = (byte)(255 - buffer[dIdx + 2]);
                }
                // andBit==1 && xorBit==0: transparent — leave as is.
            }
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _stagingTex?.Dispose();
        _output1?.Dispose();
        _device?.Dispose();
    }

    internal void ReleaseAcquiredFrame()
    {
        try { _duplication.ReleaseFrame(); } catch { }
    }
}

public class DeviceLostException : Exception
{
    public DeviceLostException(string msg, Exception inner) : base(msg, inner) { }
}

public readonly struct DesktopFrameResult
{
    public static DesktopFrameResult None { get; } = new(false, false, false, DesktopFrameMetadata.Empty);

    public DesktopFrameResult(
        bool acquired,
        bool pixelsCopied,
        bool cursorChanged,
        DesktopFrameMetadata metadata,
        bool partialUpdate = false,
        long readbackPixels = 0)
    {
        Acquired = acquired;
        PixelsCopied = pixelsCopied;
        CursorChanged = cursorChanged;
        Metadata = metadata;
        PartialUpdate = partialUpdate;
        ReadbackPixels = readbackPixels;
    }

    public bool Acquired { get; }
    public bool PixelsCopied { get; }
    public bool CursorChanged { get; }
    public DesktopFrameMetadata Metadata { get; }
    public bool PartialUpdate { get; }
    public long ReadbackPixels { get; }
}

public sealed class DesktopGpuFrame : IDisposable
{
    private readonly DesktopCapture _owner;
    private SharpDX.DXGI.Resource? _resource;
    private Texture2D? _texture;
    private bool _disposed;

    internal DesktopGpuFrame(
        DesktopCapture owner,
        SharpDX.DXGI.Resource resource,
        Texture2D texture,
        DesktopFrameMetadata metadata,
        bool cursorChanged,
        int width,
        int height)
    {
        _owner = owner;
        _resource = resource;
        _texture = texture;
        Metadata = metadata;
        CursorChanged = cursorChanged;
        Width = width;
        Height = height;
    }

    public Texture2D Texture =>
        _texture ?? throw new ObjectDisposedException(nameof(DesktopGpuFrame));

    public DesktopFrameMetadata Metadata { get; }
    public bool CursorChanged { get; }
    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _texture?.Dispose();
        _texture = null;
        _resource?.Dispose();
        _resource = null;
        _owner.ReleaseAcquiredFrame();
    }
}

public readonly struct DesktopFrameMetadata
{
    public static DesktopFrameMetadata Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public DesktopFrameMetadata(
        long lastPresentTime,
        long lastMouseUpdateTime,
        int accumulatedFrames,
        int moveRectCount,
        int dirtyRectCount,
        long dirtyPixels,
        int totalMetadataBufferSize,
        int pointerShapeBufferSize)
    {
        LastPresentTime = lastPresentTime;
        LastMouseUpdateTime = lastMouseUpdateTime;
        AccumulatedFrames = accumulatedFrames;
        MoveRectCount = moveRectCount;
        DirtyRectCount = dirtyRectCount;
        DirtyPixels = dirtyPixels;
        TotalMetadataBufferSize = totalMetadataBufferSize;
        PointerShapeBufferSize = pointerShapeBufferSize;
    }

    public long LastPresentTime { get; }
    public long LastMouseUpdateTime { get; }
    public int AccumulatedFrames { get; }
    public int MoveRectCount { get; }
    public int DirtyRectCount { get; }
    public long DirtyPixels { get; }
    public int TotalMetadataBufferSize { get; }
    public int PointerShapeBufferSize { get; }

    public bool IsIdleDuplicate => LastPresentTime == 0 && LastMouseUpdateTime == 0;
    public bool IsMouseOnly => LastPresentTime == 0 && LastMouseUpdateTime != 0;
}

public readonly record struct DesktopAdapterInfo(
    string Description,
    int VendorId,
    int DeviceId)
{
    public const int NvidiaVendorId = 0x10DE;
    public const int AmdVendorId = 0x1002;
    public const int IntelVendorId = 0x8086;

    public bool IsNvidia => VendorId == NvidiaVendorId;
    public bool IsAmd => VendorId == AmdVendorId;
    public bool IsIntel => VendorId == IntelVendorId;

    public string VendorName =>
        IsNvidia ? "NVIDIA" :
        IsAmd ? "AMD" :
        IsIntel ? "Intel" :
        $"0x{VendorId:X4}";

    public string ToDebugString() =>
        $"{Description} vendor={VendorName} device=0x{DeviceId:X4}";

    internal static DesktopAdapterInfo FromDescription(AdapterDescription description) =>
        new(
            description.Description.TrimEnd('\0').Trim(),
            description.VendorId,
            description.DeviceId);
}

internal readonly struct CursorSnapshot
{
    public CursorSnapshot(
        int x,
        int y,
        bool visible,
        int shapeWidth,
        int shapeHeight,
        int shapeType,
        int hotSpotX,
        int hotSpotY)
    {
        X = x;
        Y = y;
        Visible = visible;
        ShapeWidth = shapeWidth;
        ShapeHeight = shapeHeight;
        ShapeType = shapeType;
        HotSpotX = hotSpotX;
        HotSpotY = hotSpotY;
    }

    public int X { get; }
    public int Y { get; }
    public bool Visible { get; }
    public int ShapeWidth { get; }
    public int ShapeHeight { get; }
    public int ShapeType { get; }
    public int HotSpotX { get; }
    public int HotSpotY { get; }
}
