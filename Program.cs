using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MinimapUp;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        try
        {
            if (args.Length > 0 && args[0] == "--test") { Offline.Run(args[1]); return; }
            if (args.Length > 0 && args[0] == "--capture") { Offline.Capture(args[1]); return; }
            if (args.Length > 0 && args[0] == "--analyze") { Offline.Analyze(args[1]); return; }
            Application.Run(new Controller());
        }
        catch (Exception ex) { File.WriteAllText("error.log", ex.ToString()); MessageBox.Show(ex.Message, "MinimapUp"); }
    }
}

sealed class Settings
{
    public string ProcessName { get; set; } = "HTGame";
    // Coordinates in a client area scaled to height 1080; HUD is anchored to top-left.
    public double CenterX { get; set; } = 156;
    public double CenterY { get; set; } = 140;
    public double Radius { get; set; } = 108;
    public double ArrowThreshold { get; set; } = .66;
    public double RingThreshold { get; set; } = .46;
    public int IntervalMs { get; set; } = 33;
    public bool Cover { get; set; } = false;
    public static Settings Load() => File.Exists("settings.json") ? JsonSerializer.Deserialize<Settings>(File.ReadAllText("settings.json"))! : new();
    public void Save() => File.WriteAllText("settings.json", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);
    internal static Rectangle Client(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r)) return Rectangle.Empty;
        var p = new Point();
        if (!ClientToScreen(hwnd, ref p)) return Rectangle.Empty;
        return new(p.X, p.Y, r.Right, r.Bottom);
    }
    internal static nint FindGame(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
            using (p) { if (p.MainWindowHandle != 0) return p.MainWindowHandle; }
        return 0;
    }
}

sealed class Pixels
{
    public int W, H;
    public byte[] Data;
    public Pixels(Bitmap bitmap)
    {
        W = bitmap.Width; H = bitmap.Height;
        using var copy = bitmap.Clone(new Rectangle(0, 0, W, H), PixelFormat.Format32bppArgb);
        var d = copy.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Data = new byte[W * H * 4];
        for (int y = 0; y < H; y++) Marshal.Copy(d.Scan0 + y * d.Stride, Data, y * W * 4, W * 4);
        copy.UnlockBits(d);
    }
    public double Light(double x, double y)
    {
        int i = (Math.Clamp((int)Math.Round(y), 0, H - 1) * W + Math.Clamp((int)Math.Round(x), 0, W - 1)) * 4;
        return .2126 * Data[i + 2] + .7152 * Data[i + 1] + .0722 * Data[i];
    }
    public bool Gold(int x, int y)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return false;
        int i = (y * W + x) * 4;
        int b = Data[i], g = Data[i + 1], r = Data[i + 2];
        // Player marker is pale gold. Saturated yellow navigation lines must
        // not become part of its silhouette as the road rotates beneath it.
        return r > 125 && g > 100 && r > g * 1.04 && b > g * .30 && b < g * .82 && r - b > 45;
    }
}

record Detection(bool Valid, double X, double Y, double Radius, double Heading, double Arrow, double Ring);

sealed class Detector
{
    const int N = 48;
    readonly List<(double Heading, bool[] Mask, int Count)> templates = new();
    readonly Settings settings;
    public Detector(Settings s, string templateFile)
    {
        settings = s;
        using var source = new Bitmap(templateFile);
        var p = new Pixels(source);
        for (int a = 0; a < 360; a += 3)
        {
            double rad = a * Math.PI / 180, co = Math.Cos(rad), si = Math.Sin(rad);
            var mask = new bool[N * N]; int count = 0;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                double dx = x - N / 2.0, dy = y - N / 2.0;
                int sx = (int)Math.Round(co * dx + si * dy + N / 2.0);
                int sy = (int)Math.Round(-si * dx + co * dy + N / 2.0);
                if (p.Gold(sx, sy)) { mask[y * N + x] = true; count++; }
            }
            templates.Add((Wrap(-105 + a), mask, count));
        }
        if (templates[0].Count < 25) throw new InvalidDataException("箭头模板无效");
    }
    public static double Wrap(double a) => (a % 360 + 540) % 360 - 180;
    public static Bitmap Normalize(Bitmap source, int width = 320, int height = 300)
    {
        var result = new Bitmap(width, height);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }
    public Detection Detect(Bitmap normalized)
    {
        var p = new Pixels(normalized);
        double bestRing = -1, cx = settings.CenterX, cy = settings.CenterY, radius = settings.Radius;
        // Small spatial search tolerates HUD rounding and slight scaling changes.
        for (double r = settings.Radius - 6; r <= settings.Radius + 6; r += 3)
        for (double y = settings.CenterY - 8; y <= settings.CenterY + 8; y += 4)
        for (double x = settings.CenterX - 8; x <= settings.CenterX + 8; x += 4)
        {
            double ring = Ring(p, x, y, r);
            // Prefer the calibrated geometry when several points fit the same soft ring.
            double score = ring - .001 * (Math.Abs(x - settings.CenterX) + Math.Abs(y - settings.CenterY) + Math.Abs(r - settings.Radius));
            if (score > bestRing) { bestRing = score; cx = x; cy = y; radius = r; }
        }
        double best = 0, heading = 0;
        // Arrow position stays near the calibrated center; ring fit must not move the arrow ROI.
        for (int oy = -4; oy <= 4; oy += 2) for (int ox = -4; ox <= 4; ox += 2)
        {
            var observed = new bool[N * N]; int count = 0;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
                if (p.Gold((int)settings.CenterX + x - N / 2 + ox, (int)settings.CenterY + y - N / 2 + oy)) { observed[y * N + x] = true; count++; }
            if (count < 25 || count > 650) continue;
            foreach (var t in templates)
            {
                int intersection = 0;
                for (int i = 0; i < observed.Length; i++) if (observed[i] && t.Mask[i]) intersection++;
                double dice = 2.0 * intersection / (count + t.Count);
                if (dice > best) { best = dice; heading = t.Heading; }
            }
        }
        return new(best >= settings.ArrowThreshold && bestRing >= settings.RingThreshold, cx, cy, radius, heading, best, bestRing);
    }
    static double Ring(Pixels p, double cx, double cy, double r)
    {
        int good = 0, ridgeGood = 0, count = 0; double total = 0, ridgeTotal = 0;
        for (int a = 0; a < 360; a += 6)
        {
            // Ignore ESC badge at upper-left; the road may intersect parts of the rim.
            if (a >= 195 && a <= 255) continue;
            double rad = a * Math.PI / 180, co = Math.Cos(rad), si = Math.Sin(rad);
            double inner = p.Light(cx + co * r * .84, cy + si * r * .84);
            double rim = p.Light(cx + co * r * 1.04, cy + si * r * 1.04);
            double delta = rim - inner;
            if (delta > 10) good++;
            // A dim background removes inside/outside contrast, but the HUD still
            // has a narrow luminous circular rim. Detect that radial ridge too.
            double peak = double.MinValue;
            for (double k = .98; k <= 1.101; k += .04)
                peak = Math.Max(peak, p.Light(cx + co * r * k, cy + si * r * k));
            double outer = p.Light(cx + co * r * 1.19, cy + si * r * 1.19);
            double ridge = peak - (inner + outer) * .5;
            if (ridge > 3) ridgeGood++;
            ridgeTotal += Math.Clamp(ridge / 12, 0, 1);
            total += Math.Clamp(delta / 35, 0, 1); count++;
        }
        return Math.Max(.55 * good / count + .45 * total / count,
            .55 * ridgeGood / count + .45 * ridgeTotal / count);
    }
}

sealed class Overlay : Form
{
    Bitmap? frame;
    double angle;
    public bool CaptureExcluded { get; private set; }
    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.Black; DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x80000; return p; } }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        CaptureExcluded = Native.SetWindowDisplayAffinity(Handle, 0x11);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var path = new GraphicsPath(); path.AddEllipse(ClientRectangle);
        var old = Region; Region = new Region(path); old?.Dispose();
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = -1; return; }
        if (m.Msg == 0x21) { m.Result = 3; return; }
        base.WndProc(ref m);
    }
    public void Present(Bitmap bitmap, Rectangle bounds, double rotation)
    {
        frame?.Dispose(); frame = bitmap; angle = rotation;
        Bounds = bounds; if (!Visible) Show(); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        if (frame == null) return;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        e.Graphics.TranslateTransform(Width / 2f, Height / 2f);
        e.Graphics.RotateTransform((float)angle);
        e.Graphics.DrawImage(frame, -Width / 2f, -Height / 2f, Width, Height);
    }
    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}

sealed class Controller : Form
{
    readonly Settings settings = Settings.Load();
    readonly Overlay overlay = new();
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer timer = new();
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Visible = true, Text = "异环小地图朝上" };
    readonly CheckBox cover = new() { Text = "覆盖原小地图（取消则在右侧预览）", AutoSize = true };
    readonly Button pause = new() { Text = "暂停 / 恢复", AutoSize = true };
    readonly Detector detector;
    bool paused;
    nint game;
    DateTime lastLookup;
    int hits;
    double smoothAngle;
    bool tracking;
    string lastState = "";
    DateTime lastDiagnostic;
    readonly Stopwatch watch = new();
    public Controller()
    {
        Text = "异环 · 小地图朝上（原型）"; ClientSize = new Size(510, 240);
        Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        detector = new Detector(settings, Path.Combine(AppContext.BaseDirectory, "arrow-template.png"));
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        cover.Checked = settings.Cover; panel.Controls.Add(cover); panel.Controls.Add(pause);
        status.Padding = new Padding(15, 85, 10, 10); Controls.Add(status); Controls.Add(panel);
        cover.CheckedChanged += (_, _) => { settings.Cover = cover.Checked; settings.Save(); Reset(); };
        pause.Click += (_, _) => TogglePause();
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示控制面板", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("暂停 / 恢复", null, (_, _) => TogglePause());
        menu.Items.Add("退出", null, (_, _) => Close()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        Shown += (_, _) =>
        {
            _ = overlay.Handle;
            bool keys = Native.RegisterHotKey(Handle, 1, 0x4003, 0x77);
            keys &= Native.RegisterHotKey(Handle, 2, 0x4003, 0x78);
            keys &= Native.RegisterHotKey(Handle, 3, 0x4003, 0x79);
            Log($"started captureExcluded={overlay.CaptureExcluded} hotkeys={keys}");
            timer.Interval = Math.Clamp(settings.IntervalMs, 16, 250); timer.Tick += Tick; timer.Start();
        };
    }
    void TogglePause() { paused = !paused; Reset(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) { if (m.WParam == 1) TogglePause(); else if (m.WParam == 2) cover.Checked = !cover.Checked; else if (m.WParam == 3) Close(); }
        base.WndProc(ref m);
    }
    void Reset() { hits = 0; tracking = false; overlay.Hide(); }
    void State(string text, string detail = "")
    {
        if (lastState != text) { lastState = text; Log(text); }
        status.Text = text + "\n" + detail + "\nCtrl+Alt+F8 暂停　F9 切换覆盖　F10 退出\n（后三者均需 Ctrl+Alt）";
        if (DateTime.UtcNow - lastDiagnostic > TimeSpan.FromSeconds(1))
        {
            lastDiagnostic = DateTime.UtcNow;
            File.WriteAllText("status.json", JsonSerializer.Serialize(new { time = DateTime.Now, state = text, detail, visible = overlay.Visible, cover = settings.Cover, captureExcluded = overlay.CaptureExcluded }));
        }
    }
    static void Log(string text) => File.AppendAllText("minimap.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}\n");
    void Tick(object? sender, EventArgs e)
    {
        try
        {
            if (paused) { Reset(); State("已暂停"); return; }
            if (DateTime.UtcNow - lastLookup > TimeSpan.FromSeconds(1)) { game = Native.FindGame(settings.ProcessName); lastLookup = DateTime.UtcNow; }
            if (game == 0 || !Native.IsWindow(game)) { Reset(); State("等待 HTGame.exe"); return; }
            if (Native.IsIconic(game) || Native.GetForegroundWindow() != game) { Reset(); State("游戏未在前台，已隐藏"); return; }
            if (!overlay.CaptureExcluded) { Reset(); State("无法排除覆盖窗口的捕获，已停用", "请查看 minimap.log"); return; }
            var client = Native.Client(game);
            if (client.Width < 640 || client.Height < 400) { Reset(); State("游戏窗口过小"); return; }
            watch.Restart();
            double scale = client.Height / 1080.0;
            var roi = new Rectangle(client.X, client.Y, (int)Math.Round(320 * scale), (int)Math.Round(300 * scale));
            using var captured = new Bitmap(roi.Width, roi.Height);
            using (var g = Graphics.FromImage(captured)) g.CopyFromScreen(roi.Location, Point.Empty, roi.Size, CopyPixelOperation.SourceCopy);
            using var normalized = Detector.Normalize(captured);
            var d = detector.Detect(normalized);
            string detail = $"箭头 {d.Arrow:F2} / 圆形 {d.Ring:F2} · 处理 {watch.ElapsedMilliseconds} ms";
            if (!d.Valid) { Reset(); State("未检测到可靠小地图，已隐藏", detail); return; }
            if (++hits < 3) { State("正在确认小地图", detail); return; }
            double target = -d.Heading;
            if (!tracking) { smoothAngle = target; tracking = true; }
            else smoothAngle += Detector.Wrap(target - smoothAngle) * .65;
            // Use calibrated center so the player icon does not orbit a fluctuating ring fit.
            int radius = (int)Math.Round(settings.Radius * scale);
            int x = (int)Math.Round(settings.CenterX * scale), y = (int)Math.Round(settings.CenterY * scale);
            var crop = new Rectangle(x - radius, y - radius, radius * 2, radius * 2);
            if (!new Rectangle(Point.Empty, captured.Size).Contains(crop)) { Reset(); State("地图裁剪超出捕获区"); return; }
            var map = captured.Clone(crop, PixelFormat.Format32bppArgb);
            int outputX = settings.Cover ? client.X + crop.X : client.X + crop.Right + (int)(20 * scale);
            overlay.Present(map, new Rectangle(outputX, client.Y + crop.Y, radius * 2, radius * 2), smoothAngle);
            State(settings.Cover ? "运行中：覆盖原地图" : "运行中：右侧预览", detail + $" · 朝向 {d.Heading:F0}°");
        }
        catch (Exception ex) { Reset(); State("捕获失败，已隐藏", ex.Message); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop(); timer.Dispose(); overlay.Dispose(); tray.Visible = false; tray.Dispose();
            for (int i = 1; i <= 3; i++) Native.UnregisterHotKey(Handle, i);
        }
        base.Dispose(disposing);
    }
}

static class Offline
{
    public static void Analyze(string filename)
    {
        using var image = new Bitmap(filename);
        double scale = image.Height / 1080.0;
        using var roi = image.Clone(new Rectangle(0, 0, (int)Math.Round(320 * scale), (int)Math.Round(300 * scale)), PixelFormat.Format32bppArgb);
        using var normalized = Detector.Normalize(roi);
        var detector = new Detector(new Settings(), "arrow-template.png");
        var timer = Stopwatch.StartNew();
        var d = detector.Detect(normalized);
        File.WriteAllText(filename + ".detection.json", JsonSerializer.Serialize(new { detection = d, milliseconds = timer.Elapsed.TotalMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void Capture(string destination)
    {
        nint hwnd = Native.FindGame("HTGame");
        if (hwnd == 0) throw new Exception("HTGame.exe 未找到");
        var client = Native.Client(hwnd);
        using var img = new Bitmap(client.Width, client.Height);
        using (var g = Graphics.FromImage(img)) g.CopyFromScreen(client.Location, Point.Empty, client.Size);
        img.Save(destination);
        File.WriteAllText(destination + ".json", JsonSerializer.Serialize(new { client, foreground = Native.GetForegroundWindow() == hwnd }));
    }
    public static void Run(string filename)
    {
        var s = new Settings();
        using var original = new Bitmap(filename);
        double scale = original.Height / 1080.0;
        using var roi = original.Clone(new Rectangle(0, 0, (int)Math.Round(320 * scale), (int)Math.Round(300 * scale)), PixelFormat.Format32bppArgb);
        using var normalized = Detector.Normalize(roi);
        // A template is built once from the supplied reference screenshot.
        string template = Path.Combine(AppContext.BaseDirectory, "arrow-template.png");
        if (!File.Exists(template))
        {
            using var arrow = normalized.Clone(new Rectangle(132, 116, 48, 48), PixelFormat.Format32bppArgb);
            arrow.Save(template);
        }
        var detector = new Detector(s, template);
        var results = new List<object>();
        var d = detector.Detect(normalized); results.Add(new { name = "reference", detection = d });
        normalized.Save("map-source.png");
        using var corrected = new Bitmap(216, 216);
        using (var g = Graphics.FromImage(corrected))
        {
            g.Clear(Color.Black); g.SetClip(new Rectangle(0, 0, 216, 216));
            g.TranslateTransform(108, 108); g.RotateTransform((float)-d.Heading);
            g.DrawImage(normalized, new Rectangle(-108, -108, 216, 216), new Rectangle(48, 32, 216, 216), GraphicsUnit.Pixel);
        }
        corrected.Save("map-corrected.png");
        int failures = 0;
        if (!d.Valid) failures++;
        foreach (int angle in Enumerable.Range(1, 23).Select(i => i * 15))
        {
            using var rotated = new Bitmap(320, 300);
            using (var g = Graphics.FromImage(rotated))
            {
                g.Clear(Color.Black); g.TranslateTransform(156, 140); g.RotateTransform(angle);
                g.DrawImage(normalized, -156, -140);
            }
            var rd = detector.Detect(rotated);
            double error = Math.Abs(Detector.Wrap(rd.Heading - d.Heading - angle));
            results.Add(new { name = $"rotated_{angle}", detection = rd, error });
            if (!rd.Valid || error > 8) failures++;
        }
        using var empty = new Bitmap(320, 300);
        var absent = detector.Detect(empty); results.Add(new { name = "no_map_black", detection = absent });
        if (absent.Valid) failures++;
        // A yellow marker alone must not be accepted as a minimap.
        using var arrowOnly = new Bitmap(320, 300);
        using (var g = Graphics.FromImage(arrowOnly)) { g.Clear(Color.FromArgb(60, 80, 100)); using var a = new Bitmap(template); g.DrawImage(a, 132, 116); }
        var ad = detector.Detect(arrowOnly); results.Add(new { name = "arrow_without_map", detection = ad });
        if (ad.Valid) failures++;
        File.WriteAllText("test-results.json", JsonSerializer.Serialize(new { failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
