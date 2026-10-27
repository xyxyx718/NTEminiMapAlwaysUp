using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace MinimapUp;

// Capture only the game's compositor surface. Overlay/control windows remain
// recordable and are never part of this input, so display-affinity flags are unnecessary.
sealed class WindowCapture : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    Bitmap? minimap, driving;
    Rectangle sampledClient;
    long sampledAt;
    int disposed;
    int active = 1;
    public nint Window { get; }
    public bool Active
    {
        get => Volatile.Read(ref active) != 0;
        set
        {
            if (Interlocked.Exchange(ref active, value ? 1 : 0) == 1 && !value)
                lock (gate) { minimap?.Dispose(); driving?.Dispose(); minimap = driving = null; }
        }
    }
    public volatile int IntervalMs = 33;
    public volatile string? Error;
    public double ReadbackMilliseconds { get; private set; }

    public WindowCapture(nint window)
    {
        Window = window;
        worker = Task.Run(PumpAsync);
    }
    public bool TryGetMinimap(Rectangle client, out Bitmap? image)
    {
        lock (gate)
        {
            image = null;
            if (minimap == null || client != sampledClient || Error != null) return false;
            image = (Bitmap)minimap.Clone(); return true;
        }
    }
    public Bitmap CopyDriving(Rectangle client)
    {
        lock (gate)
        {
            if (driving == null || client != sampledClient || Error != null)
                throw new InvalidOperationException("等待驾驶提示的新捕获帧");
            return (Bitmap)driving.Clone();
        }
    }
    async Task PumpAsync()
    {
        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows 窗口捕获不可用");
            device = CaptureInterop.CreateDevice();
            var item = CaptureInterop.CreateItem(Window);
            var size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch (Exception) { /* OS may require the standard capture border. */ }
            session.StartCapture();
            byte[] bytes = [];
            while (!stop.IsCancellationRequested)
            {
                if (!Active) { await Task.Delay(30, stop.Token); continue; }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var frame = pool.TryGetNextFrame();
                if (frame == null) { await Task.Delay(5, stop.Token); continue; }
                if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                {
                    size = frame.ContentSize;
                    frame.Dispose();
                    if (size.Width > 0 && size.Height > 0) pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    continue;
                }
                using (frame)
                {
                var client = Native.Client(Window);
                if (client.Width < 640 || client.Height < 400) { await Task.Delay(30, stop.Token); continue; }
                using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                int width = software.PixelWidth, height = software.PixelHeight;
                if (bytes.Length != width * height * 4) bytes = new byte[checked(width * height * 4)];
                software.CopyToBuffer(bytes.AsBuffer());
                var bounds = CaptureInterop.VisualBounds(Window);
                int cx = client.X - bounds.X, cy = client.Y - bounds.Y;
                // Borderless windows commonly have exactly the client dimensions.
                if (width == client.Width && height == client.Height) { cx = 0; cy = 0; }
                double scale = client.Height / 1080.0;
                var mapRegion = new Rectangle(cx, cy, (int)Math.Round(320 * scale), (int)Math.Round(300 * scale));
                var driveRegion = DrivingDetector.Region(new Rectangle(cx, cy, client.Width, client.Height));
                var map = Crop(bytes, width, height, mapRegion);
                Bitmap drive;
                try { drive = Crop(bytes, width, height, driveRegion); }
                catch { map.Dispose(); throw; }
                lock (gate)
                {
                    if (Active && !stop.IsCancellationRequested)
                    {
                        minimap?.Dispose(); driving?.Dispose(); minimap = map; driving = drive;
                        sampledClient = client; sampledAt = Environment.TickCount64;
                        ReadbackMilliseconds = clock.Elapsed.TotalMilliseconds;
                    }
                    else { map.Dispose(); drive.Dispose(); }
                }
                await Task.Delay(Math.Max(1, Math.Clamp(IntervalMs, 16, 1000) - (int)clock.ElapsedMilliseconds), stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            session?.Dispose(); pool?.Dispose(); device?.Dispose();
            lock (gate) { minimap?.Dispose(); driving?.Dispose(); minimap = driving = null; }
            stop.Dispose();
        }
    }
    static Bitmap Crop(byte[] bytes, int width, int height, Rectangle region)
    {
        if (!new Rectangle(0, 0, width, height).Contains(region)) throw new InvalidOperationException("窗口捕获客户区坐标超出画面，请检查窗口边框或缩放");
        var result = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        var data = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < region.Height; y++)
                Marshal.Copy(bytes, ((region.Y + y) * width + region.X) * 4, data.Scan0 + y * data.Stride, region.Width * 4);
        }
        finally { result.UnlockBits(data); }
        return result;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
    }
}

static class CaptureInterop
{
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, int attribute, out Native.Rect bounds, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out Native.Rect bounds);

    internal static IDirect3DDevice CreateDevice()
    {
        nint native = 0, context = 0, dxgi = 0, projected = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context));
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out projected));
            return MarshalInterface<IDirect3DDevice>.FromAbi(projected);
        }
        finally
        {
            if (projected != 0) Marshal.Release(projected);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (native != 0) Marshal.Release(native);
        }
    }
    internal static unsafe GraphicsCaptureItem CreateItem(nint window)
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"));
        var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        nint item = 0;
        var table = *(nint**)factory.ThisPtr;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)table[3])(factory.ThisPtr, window, &iid, &item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally { if (item != 0) Marshal.Release(item); }
    }
    internal static Rectangle VisualBounds(nint window)
    {
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<Native.Rect>()) < 0 && !GetWindowRect(window, out rect))
            throw new InvalidOperationException("无法查询游戏窗口边界");
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
