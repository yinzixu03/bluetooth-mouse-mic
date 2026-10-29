using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class MicApp
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [STAThread] private static int Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool preview = args.Length > 0 && (args[0] == "--preview" || args[0] == "--render");
        bool owner;
        using (var mutex = new Mutex(true, "Local\\M110Mic.NativeUI", out owner))
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\M110Mic.ShowUI"))
        {
            if (!owner && !preview) { signal.Set(); return 0; }
            using (var window = new MicWindow(preview, signal))
            {
                if (args.Length == 2 && args[0] == "--render")
                    window.Shown += delegate { window.BeginInvoke((Action)delegate {
                        using (var bitmap = new Bitmap(window.Width, window.Height))
                        {
                            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            Point origin = window.PointToScreen(Point.Empty); origin.Offset(-window.Left, -window.Top);
                            using (var client = bitmap.Clone(new Rectangle(origin, window.ClientSize), bitmap.PixelFormat))
                                client.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        window.Close();
                    }); };
                int seconds;
                if (args.Length == 2 && args[0] == "--preview" && int.TryParse(args[1], out seconds))
                {
                    var finish = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
                    finish.Tick += delegate { finish.Stop(); window.Close(); finish.Dispose(); };
                    finish.Start();
                }
                Application.Run(window);
            }
        }
        return 0;
    }
}

internal sealed class MicWindow : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 45, 39), Muted = Color.FromArgb(116, 128, 121);
    private readonly bool preview;
    private readonly EventWaitHandle showSignal;
    private readonly string stopFile = Path.Combine(Path.GetTempPath(), "m110-mic-" + Guid.NewGuid().ToString("N") + ".stop");
    private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1000 };
    private readonly SoftPanel badge;
    private readonly Label badgeText, description, footer;
    private readonly SoftButton action;
    private readonly LinkSwitch link;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    private Process worker;
    private bool stopping, closing;
    private string state;

    public MicWindow(bool preview, EventWaitHandle showSignal)
    {
        this.preview = preview; this.showSignal = showSignal;
        Text = "M110 · 鼠标麦克风"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(460, 454); FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; BackColor = Color.FromArgb(245, 247, 244);
        Font = new Font("Microsoft YaHei UI", 10); DoubleBuffered = true;
        Icon = MakeIcon();

        var mark = new PictureBox { Image = Icon.ToBitmap(), Bounds = new Rectangle(24, 25, 36, 36), SizeMode = PictureBoxSizeMode.Zoom };
        Controls.Add(mark);
        LabelAt(this, "M110", 72, 19, 150, 29, 17, true, Ink);
        LabelAt(this, "鼠标麦克风", 73, 48, 190, 20, 9, false, Muted);
        badge = new SoftPanel { Bounds = new Rectangle(330, 32, 106, 28), Radius = 14 };
        Controls.Add(badge);
        badgeText = LabelAt(badge, "", 0, 0, 106, 28, 9, false, Muted);
        badgeText.TextAlign = ContentAlignment.MiddleCenter;

        var card = new SoftPanel { Bounds = new Rectangle(24, 88, 412, 224), Radius = 18 };
        Controls.Add(card);
        LabelAt(card, "鼠标语音键", 24, 20, 350, 22, 9, false, Muted);
        LabelAt(card, "按住说话，松开出字", 22, 57, 372, 39, 20, true, Ink);
        description = LabelAt(card, "网易麦克风请选择 CABLE Output", 25, 107, 362, 28, 10, false, Muted);
        action = new SoftButton { Text = "开启麦克风", Bounds = new Rectangle(24, 155, 364, 46) };
        action.Click += delegate { if (preview) return; if (worker == null) StartBridge(); else StopBridge(); };
        card.Controls.Add(action);

        var options = new SoftPanel { Bounds = new Rectangle(24, 328, 412, 72), Radius = 16 };
        Controls.Add(options);
        LabelAt(options, "联动网易叭哥说", 18, 12, 290, 23, 10, true, Ink);
        LabelAt(options, "点按右 Alt，自动开始 / 结束录音", 18, 40, 310, 19, 8.5f, false, Muted);
        link = new LinkSwitch { Checked = true, Bounds = new Rectangle(341, 23, 50, 28), AccessibleName = "联动网易叭哥说" };
        options.Controls.Add(link);
        link.CheckedChanged += delegate { if (worker == null) SetState(state ?? "idle"); };

        var hide = new LinkLabel { Text = "收起到托盘", AutoSize = true, Location = new Point(25, 419),
            LinkColor = Muted, ActiveLinkColor = Ink, LinkBehavior = LinkBehavior.HoverUnderline, Font = new Font(Font.FontFamily, 9) };
        hide.LinkClicked += delegate { HideToTray(); }; Controls.Add(hide);
        footer = LabelAt(this, "保持网易在后台运行", 202, 417, 234, 24, 8.5f, false, Muted);
        footer.TextAlign = ContentAlignment.MiddleRight;
        tray = new NotifyIcon { Icon = Icon, Text = "M110 鼠标麦克风", ContextMenuStrip = trayMenu };
        trayMenu.Items.Add("显示窗口", null, delegate { RestoreWindow(); });
        trayMenu.Items.Add("退出", null, delegate { Close(); });
        tray.DoubleClick += delegate { RestoreWindow(); };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        poll.Tick += delegate { PollWorker(); }; poll.Start();
        Shown += delegate { if (!preview) StartBridge(); };
        SetState("idle");
        using (var graphics = CreateGraphics())
            if (Math.Abs(graphics.DpiX - 96) > 1) Scale(new SizeF(graphics.DpiX / 96, graphics.DpiY / 96));
    }

    private static Label LabelAt(Control parent, string text, int x, int y, int width, int height, float size, bool bold, Color color)
    {
        var label = new Label { Text = text, Bounds = new Rectangle(x, y, width, height), BackColor = Color.Transparent,
            ForeColor = color, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
        parent.Controls.Add(label); return label;
    }

    private void SetState(string value)
    {
        state = value;
        bool ready = value == "ready", busy = value == "connecting" || value == "stopping", error = value == "error";
        badge.Fill = ready ? Color.FromArgb(225, 242, 233) : error ? Color.FromArgb(253, 233, 228) : Color.FromArgb(233, 237, 232);
        badgeText.ForeColor = ready ? Color.FromArgb(38, 117, 80) : error ? Color.FromArgb(163, 76, 58) : Muted;
        badgeText.Text = "● " + (ready ? "已连接" : error ? "连接失败" : value == "connecting" ? "连接中" : value == "stopping" ? "正在停止" : "未连接");
        action.Text = ready ? "暂停连接" : value == "connecting" ? "正在连接…" : value == "stopping" ? "正在停止…" : error ? "重新连接" : "开启麦克风";
        action.Enabled = !busy; link.Enabled = worker == null;
        description.Text = error ? "请检查鼠标蓝牙连接和虚拟麦克风" : link.Checked ? "网易麦克风请选择 CABLE Output" : "语音软件的麦克风请选择 CABLE Output";
        footer.Text = link.Checked ? "保持网易在后台运行" : "按住鼠标语音键传输声音";
        badge.Invalidate();
    }

    private void StartBridge()
    {
        if (worker != null) return;
        try
        {
            CleanStatus(); stopping = false;
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "support", "monitor_key_events.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException();
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"));
            info.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script +
                "\" -RunBridge -KeyMode hold -KeyId 0 -DurationSeconds 86400 -OutputDeviceName \"CABLE Input\" -StopFile \"" + stopFile +
                "\" -StatusFile \"" + stopFile + ".status\"" + (link.Checked ? " -TargetHotkeyMode toggle" : "");
            info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            worker = new Process { StartInfo = info };
            worker.OutputDataReceived += delegate { }; worker.ErrorDataReceived += delegate { };
            worker.Start(); worker.BeginOutputReadLine(); worker.BeginErrorReadLine(); SetState("connecting");
        }
        catch { if (worker != null) { worker.Dispose(); worker = null; } SetState("error"); }
    }

    private void StopBridge()
    {
        if (worker == null || stopping) return;
        try { File.WriteAllText(stopFile, "stop"); stopping = true; SetState("stopping"); }
        catch { SetState("error"); }
    }

    private void PollWorker()
    {
        if (!preview && showSignal.WaitOne(0)) RestoreWindow();
        if (worker == null) return;
        if (worker.HasExited)
        {
            bool failed = worker.ExitCode != 0 && !stopping;
            worker.Dispose(); worker = null; CleanStatus(); SetState(failed ? "error" : "idle");
            if (closing) Close();
        }
        else if (!stopping && state == "connecting")
        {
            try { if (File.ReadAllText(stopFile + ".status") == "ready") SetState("ready"); } catch (IOException) { }
        }
    }

    private void CleanStatus()
    {
        foreach (string path in new string[] {stopFile, stopFile + ".status"}) try { File.Delete(path); } catch (IOException) { }
    }
    private void HideToTray() { tray.Visible = true; Hide(); }
    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (worker != null && !worker.HasExited) { e.Cancel = true; closing = true; StopBridge(); }
        base.OnFormClosing(e);
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        poll.Stop(); poll.Dispose(); tray.Dispose(); trayMenu.Dispose(); CleanStatus();
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static Icon MakeIcon()
    {
        using (var bitmap = new Bitmap(64, 64))
        using (var g = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(Ink))
        using (var pen = new Pen(Color.FromArgb(187, 235, 207), 4))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outline = Ui.Round(new Rectangle(1, 1, 62, 62), 16)) g.FillPath(fill, outline);
            pen.StartCap = pen.EndCap = LineCap.Round;
            using (var mic = Ui.Round(new Rectangle(26, 14, 12, 24), 6)) g.DrawPath(pen, mic);
            g.DrawArc(pen, 19, 24, 26, 24, 0, 180); g.DrawLine(pen, 32, 48, 32, 54); g.DrawLine(pen, 24, 54, 40, 54);
            IntPtr handle = bitmap.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
        }
    }
}

internal static class Ui
{
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2; var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}
internal sealed class SoftPanel : Panel
{
    public Color Fill = Color.White;
    public int Radius = 16;
    public SoftPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), (int)(Radius * e.Graphics.DpiX / 96)))
        using (var brush = new SolidBrush(Fill)) e.Graphics.FillPath(brush, path);
        base.OnPaint(e);
    }
}
internal sealed class SoftButton : Button
{
    private bool hover;
    public SoftButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; DoubleBuffered = true; BackColor = Color.White; Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Round(new Rectangle(1, 1, Width - 3, Height - 3), (int)(12 * e.Graphics.DpiX / 96)))
        using (var brush = new SolidBrush(!Enabled ? Color.FromArgb(208, 218, 211) : hover ? Color.FromArgb(48, 82, 66) : Color.FromArgb(28, 55, 43)))
        { e.Graphics.FillPath(brush, path); if (Focused) using (var pen = new Pen(Color.FromArgb(100, 153, 120), 2)) e.Graphics.DrawPath(pen, path); }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? Color.White : Color.FromArgb(97, 115, 103), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
internal sealed class LinkSwitch : CheckBox
{
    public LinkSwitch() { Cursor = Cursors.Hand; DoubleBuffered = true; Appearance = Appearance.Button; FlatStyle = FlatStyle.Flat; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = Height / 28f;
        using (var path = Ui.Round(new Rectangle(1, 1, Width - 2, Height - 2), Height / 2 - 1))
        using (var brush = new SolidBrush(Checked ? Color.FromArgb(63, 125, 90) : Color.FromArgb(205, 214, 206))) e.Graphics.FillPath(brush, path);
        using (var brush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(brush, Checked ? Width - 25 * scale : 4 * scale, 4 * scale, 20 * scale, 20 * scale);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
