using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MinimapUp;

record DrivingResult(bool Detected, string[] Keywords, string Text, string? Error, double Milliseconds);

sealed class DrivingDetector
{
    internal static readonly string[] Keywords = ["刹车", "手刹", "加速", "切换舒适模式", "开启智能驾驶", "下车", "后视镜"];
    OcrEngine? engine;
    internal static string[] MatchKeywords(IEnumerable<string> lines)
    {
        var normalized = lines.Select(line => string.Concat(line.Where(c => !char.IsWhiteSpace(c)))).ToArray();
        return Keywords.Where(word => normalized.Any(line => line.Contains(word, StringComparison.Ordinal))).ToArray();
    }
    internal static Rectangle Region(Rectangle client)
    {
        // Search a broad relative area, not fixed text rows or vehicle layouts.
        int width = (int)Math.Round(client.Width * .35);
        int height = (int)Math.Round(client.Height * .75);
        return new(client.Right - width, client.Bottom - height, width, height);
    }
    public async Task<DrivingResult> RecognizeAsync(Bitmap roi)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            engine ??= OcrEngine.TryCreateFromLanguage(new Language("zh-Hans"))
                ?? throw new InvalidOperationException("Windows 简体中文 OCR 不可用，请取消驾驶模式限制或安装中文 OCR 语言组件。");
            // Normalize the text size for 1080p/1440p/4K and controller layouts.
            double zoom = Math.Min(1215.0 / roi.Height, 2048.0 / Math.Max(roi.Width, roi.Height));
            using var scaled = Detector.Normalize(roi, Math.Max(1, (int)(roi.Width * zoom)), Math.Max(1, (int)(roi.Height * zoom)));
            var pixels = new Pixels(scaled);
            // HUD letters are neutral white/gray; remove colorful scenery and
            // invert to dark letters on white to help read translucent hints.
            for (int i = 0; i < pixels.Data.Length; i += 4)
            {
                int b = pixels.Data[i], g = pixels.Data[i + 1], r = pixels.Data[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                byte value = (byte)(min >= 125 && max - min <= 22 ? 0 : 255);
                pixels.Data[i] = pixels.Data[i + 1] = pixels.Data[i + 2] = value;
            }
            var (data, width, height) = TrimMask(pixels);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(data.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines.Select(line => line.Text).ToArray();
            var matched = MatchKeywords(lines);
            // Faded/disabled labels may not survive the neutral-white mask.
            // Fall back to the original color crop only when no keyword matched.
            if (matched.Length == 0)
            {
                var original = new Pixels(scaled);
                using var originalBitmap = SoftwareBitmap.CreateCopyFromBuffer(original.Data.AsBuffer(), BitmapPixelFormat.Bgra8, original.W, original.H, BitmapAlphaMode.Ignore);
                var fallback = await engine.RecognizeAsync(originalBitmap);
                lines = lines.Concat(fallback.Lines.Select(line => line.Text)).ToArray();
                matched = MatchKeywords(lines);
            }
            return new(matched.Length > 0, matched, string.Join("\n", lines), null, watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) { return new(false, [], "", ex.Message, watch.Elapsed.TotalMilliseconds); }
    }
    static (byte[] Data, int Width, int Height) TrimMask(Pixels pixels)
    {
        int left = pixels.W, top = pixels.H, right = -1, bottom = -1;
        for (int y = 0; y < pixels.H; y++) for (int x = 0; x < pixels.W; x++)
            if (pixels.Data[(y * pixels.W + x) * 4] == 0)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left) return (pixels.Data, pixels.W, pixels.H);
        int width = right - left + 41, height = bottom - top + 41;
        var cropped = new byte[width * height * 4]; Array.Fill(cropped, (byte)255);
        for (int y = top; y <= bottom; y++)
            Buffer.BlockCopy(pixels.Data, (y * pixels.W + left) * 4, cropped, ((y - top + 20) * width + 20) * 4, (right - left + 1) * 4);
        return (cropped, width, height);
    }
}

// Only one OCR job is in flight. UI/minimap capture never waits for OCR.
// Reset invalidates an old job so a result from a previous screen cannot reappear.
sealed class DrivingMonitor
{
    readonly DrivingDetector detector = new();
    Task<DrivingResult>? pending;
    int generation, pendingGeneration;
    long sampledAt, acceptedAt;
    DrivingResult latest = new(false, [], "", null, 0);
    public DrivingResult Poll(Func<Bitmap> capture)
    {
        if (pending?.IsCompleted == true)
        {
            var completed = pending.GetAwaiter().GetResult();
            if (pendingGeneration == generation) { latest = completed; acceptedAt = sampledAt; }
            pending = null;
        }
        long now = Environment.TickCount64;
        if (pending == null)
        {
            var roi = capture();
            pendingGeneration = generation; sampledAt = now;
            pending = Task.Run(async () => { using (roi) return await detector.RecognizeAsync(roi); });
        }
        return now - acceptedAt <= 1000 ? latest : new(false, [], "", latest.Error, 0);
    }
    public void Reset() { generation++; latest = new(false, [], "", null, 0); acceptedAt = 0; }
}
