using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace PigPet;

public partial class PetWindow : Window
{
    public PetBrain Brain { get; }
    public event Action? SettingsRequested;

    public double PetSize => Config.Current.Size;
    public int Dir { get; private set; } = -1; // -1 朝左（原图朝向），1 朝右

    /// <summary>是否是分裂出来的克隆（克隆可以合体消失，本体不会）。</summary>
    public bool IsClone { get; }

    /// <summary>身体中心的屏幕坐标（DIP）。</summary>
    public Point BodyCenter => new(Left + Width / 2, Top + Height - PetSize / 2);

    readonly Action<Config> _onConfig;

    public PetWindow(Point? spawnPos = null, Vector velocity = default, bool isClone = false)
    {
        InitializeComponent();
        IsClone = isClone;
        Brain = new PetBrain(this);
        _onConfig = c => Dispatcher.Invoke(() => ApplyConfig(c));

        SourceInitialized += (_, _) =>
        {
            ApplyConfig(Config.Current);
            if (spawnPos is Point p) MoveRaw(p.X, p.Y);
            else
            {
                ResetPosition();
                // 多只时错开，不叠在同一个位置
                MoveTo(Left - (Herd.Count - 1) * PetSize * 1.2, Top);
            }
        };
        Loaded += (_, _) => Brain.Start(velocity, spawnPos != null);
        CompositionTarget.Rendering += OnRender;
        Config.Changed += _onConfig;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            Config.Changed -= _onConfig;
            Brain.Stop();
        };

        Pig.MouseLeftButtonDown += OnDown;
        Pig.MouseMove += OnMove;
        Pig.MouseLeftButtonUp += OnUp;
        Pig.LostMouseCapture += (_, _) => Release();
        Pig.MouseRightButtonUp += (_, _) => SettingsRequested?.Invoke();
    }

    // ---------- 配置 ----------
    void ApplyConfig(Config c)
    {
        Width = Height = c.Size * 2;
        Pig.Width = Pig.Height = c.Size;
        Canvas.SetLeft(Pig, (Width - c.Size) / 2);
        Canvas.SetTop(Pig, Height - c.Size);
        Opacity = c.Opacity;
        Topmost = c.AlwaysOnTop;
        SetClickThrough(c.ClickThrough);
        Clamp();
        _ = LoadFrames(c.Size);
    }

    // ---------- 官方动画帧播放 ----------
    BitmapSource[]? _frames;
    int _framePx, _frameIdx = -1, _loadVersion;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    async Task LoadFrames(double size)
    {
        var (sx, _) = Dpi();
        int px = (int)Math.Min(512, Math.Ceiling(size * sx));
        if (px == _framePx) return;
        _framePx = px;
        int ver = ++_loadVersion;
        try
        {
            var frames = await LottieFrames.GetAsync(px);
            if (ver != _loadVersion) return; // 期间尺寸又变了
            _frames = frames;
            _frameIdx = -1;
            Still.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); } // 失败就保留静态图
    }

    /// <summary>官方 rest 姿势帧（形态里复制群友、做灰色版本用）。</summary>
    public BitmapSource? RestFrame { get; private set; }
    /// <summary>非空时固定显示这一帧（例如“蛇咬猪”的灰色猪）。</summary>
    public BitmapSource? OverrideFrame { get; set; }

    /// <summary>开发用：依次应用每个形态并渲染整窗 PNG。</summary>
    public async Task Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        while (RestFrame == null) await Task.Delay(100);
        Brain.SetPaused(true);
        var ids = new System.Collections.Generic.List<string> { "rest" };
        foreach (var f in Forms.All) ids.Add(f.Id);
        ids.Add("bite"); ids.Add("closed");
        foreach (var flip in new[] { -1, 1 })
            foreach (var id in ids)
            {
                FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                Dir = flip; FlipScale.ScaleX = flip > 0 ? -1 : 1;
                ResetTransform();
                Forms.Apply(this, id);
                if (id == "closed") Forms.ClosedEyes(Props);
                OverrideFrame = RestFrame;
                if (id == "peek") { Anim.RenderTransformOrigin = new Point(0.29, 0.55); AScale.ScaleX = AScale.ScaleY = 2.4; AMove.X = PetSize * 0.21; AMove.Y = PetSize * 0.1; }
                if (id == "flat") { Anim.RenderTransformOrigin = new Point(0.5, 0.92); AScale.ScaleX = 1.35; AScale.ScaleY = 0.5; }
                if (id == "token") { Anim.RenderTransformOrigin = new Point(0.5, 0.5); ARotate.Angle = 165; }
                if (id == "dead") { OverrideFrame = Forms.ToGray(RestFrame); Anim.RenderTransformOrigin = new Point(0.5, 0.5); ARotate.Angle = 180; }
                if (id == "bite") { OverrideFrame = Forms.ToGray(RestFrame); Anim.RenderTransformOrigin = new Point(0.5, 0.5); ARotate.Angle = 180; }
                await Task.Delay(150);
                UpdateLayout();
                var rtb = new RenderTargetBitmap((int)Width * 2, (int)Height * 2, 192, 192, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
                rtb.Render(bg);
                rtb.Render(Root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(dir, $"{id}_{(flip > 0 ? "R" : "L")}.png"));
                enc.Save(fs);
                Forms.Clear(this);
            }
    }

    double? _playStart; // 官方动画开始播放的时刻；null = 停在 rest 姿势

    /// <summary>按官方用法播放一遍动画，播完停在 rest 帧。返回时长（秒）。</summary>
    public double PlayOfficial()
    {
        _playStart = _clock.Elapsed.TotalSeconds;
        return _frames == null ? 0 : _frames.Length / LottieFrames.Fps;
    }

    void OnRender(object? s, EventArgs e)
    {
        PollDrag();
        if (_frames == null) return;
        int rest = Math.Min(_frames.Length - 1, (int)Math.Round(LottieFrames.RestSeconds * LottieFrames.Fps));
        int idx = rest;
        if (_playStart is double st)
        {
            idx = (int)((_clock.Elapsed.TotalSeconds - st) * LottieFrames.Fps);
            if (idx >= _frames.Length) { _playStart = null; idx = rest; }
        }
        RestFrame = _frames[rest];
        if (OverrideFrame != null) { if (Lazy.Source != OverrideFrame) { Lazy.Source = OverrideFrame; _frameIdx = -1; } return; }
        if (idx == _frameIdx) return; // 只有帧号变化才换图
        _frameIdx = idx;
        Lazy.Source = _frames[idx];
    }

    // ---------- 外观切换 ----------
    public void ShowLazy(bool lazy)
    {
        // 动作期间也保持官方动画形象，避免两套画风来回切换造成跳变
    }

    public void ResetTransform()
    {
        AScale.ScaleX = AScale.ScaleY = 1;
        ARotate.Angle = 0;
        AMove.X = AMove.Y = 0;
        Anim.RenderTransformOrigin = new Point(0.5, 0.85);
    }

    public void SetDir(int d)
    {
        if (Dir == d) return;
        Dir = d;
        // 转身做一个很短的翻面动画，而不是瞬间镜像
        var turn = new DoubleAnimation(d > 0 ? -1 : 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        FlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, turn);
    }

    public void Say(string? text, int ms = 2500)
    {
        if (!Config.Current.ShowBubbles || string.IsNullOrEmpty(text)) return;
        BubbleText.Text = text;
        Bubble.UpdateLayout();
        Canvas.SetLeft(Bubble, Math.Max(0, (Width - Bubble.ActualWidth) / 2));
        Canvas.SetTop(Bubble, Math.Max(0, Height - PetSize - Bubble.ActualHeight - 4));
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms + 300))));
        Bubble.BeginAnimation(OpacityProperty, fade);
    }

    public void Particle(string text, Color color, double xRatio = 0.5)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(color),
            FontSize = PetSize * 0.2, FontWeight = FontWeights.Bold,
        };
        double x = (Width - PetSize) / 2 + PetSize * xRatio + Random.Shared.Next(-15, 15);
        double y = Height - PetSize * 0.9;
        Canvas.SetLeft(tb, x);
        Fx.Children.Add(tb);
        var dur = TimeSpan.FromMilliseconds(1600);
        var up = new DoubleAnimation(y, y - PetSize * 0.8, dur) { EasingFunction = new QuadraticEase() };
        var fade = new DoubleAnimation(1, 0, dur);
        fade.Completed += (_, _) => Fx.Children.Remove(tb);
        tb.BeginAnimation(Canvas.TopProperty, up);
        tb.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- 窗口位置（DIP） ----------
    Rect? _wa; // 工作区缓存，拖动结束 / 配置变化时刷新

    public void RefreshWorkArea() => _wa = null;

    public Rect WorkArea()
    {
        if (_wa is Rect r) return r;
        var (sx, sy) = Dpi();
        var center = new System.Drawing.Point((int)((Left + Width / 2) * sx), (int)((Top + Height / 2) * sy));
        var wa = WinForms.Screen.FromPoint(center).WorkingArea;
        return (_wa = new Rect(wa.X / sx, wa.Y / sy, wa.Width / sx, wa.Height / sy)).Value;
    }

    (double, double) Dpi()
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice ?? Matrix.Identity;
        return (m.M11, m.M22);
    }

    public double MinX => WorkArea().Left;
    public double MaxX => WorkArea().Right - Width;
    public double MaxY => WorkArea().Bottom - Height;

    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>一次系统调用同时设置 X/Y，避免分别改 Left/Top 造成两次移动和抖动。</summary>
    public void MoveTo(double x, double y)
    {
        var wa = WorkArea();
        x = Math.Clamp(x, wa.Left, wa.Right - Width);
        y = Math.Clamp(y, wa.Top, wa.Bottom - Height);
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) { Left = x; Top = y; return; }
        var (sx, sy) = Dpi();
        SetWindowPos(h, IntPtr.Zero, (int)Math.Round(x * sx), (int)Math.Round(y * sy), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>小猪身体可活动的窗口坐标范围（窗口透明边缘可以出屏，让身体贴到屏幕边）。</summary>
    public Rect PhysicsBounds()
    {
        var wa = WorkArea();
        double side = (Width - PetSize) / 2, top = Height - PetSize;
        return new Rect(wa.Left - side, wa.Top - top, wa.Width - Width + 2 * side, wa.Height - Height + top);
    }

    public void MoveRaw(double x, double y)
    {
        var (sx, sy) = Dpi();
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)Math.Round(x * sx), (int)Math.Round(y * sy), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void Clamp() { RefreshWorkArea(); MoveTo(Left, Top); }

    public void ResetPosition()
    {
        RefreshWorkArea();
        var wa = WorkArea();
        MoveTo(wa.Right - Width - 80, wa.Bottom - Height);
    }

    // ---------- 点击穿透 ----------
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    void SetClickThrough(bool on)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int s = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
        s = on ? s | WS_EX_TRANSPARENT : s & ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, s);
    }

    // ---------- 拖动 / 点击 ----------
    // 每个渲染帧直接读系统光标位置并 SetWindowPos，不依赖 WM_MOUSEMOVE 的频率，保证跟手
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    bool _pressed, _dragging;
    public Point GrabPoint { get; private set; } = new(0.5, 0.3);
    POINT _downCursor, _lastCursor;
    RECT _downRect;
    DateTime _lastClick;

    void OnDown(object s, MouseButtonEventArgs e)
    {
        // 记录抓取点（相对小猪 0~1），拎起时以此为支点摆动，身体始终挂在光标下
        var gp = e.GetPosition(Pig);
        GrabPoint = new Point(Math.Clamp(gp.X / Pig.ActualWidth, 0, 1), Math.Clamp(gp.Y / Pig.ActualHeight, 0, 1));
        GetCursorPos(out _downCursor);
        _lastCursor = _downCursor;
        GetWindowRect(new WindowInteropHelper(this).Handle, out _downRect);
        _pressed = true;
        _trail.Clear();
        Pig.CaptureMouse();
    }

    void OnMove(object s, MouseEventArgs e)
    {
        // 故意什么都不做：鼠标移动消息量极大，在这里干活会让消息积压，
        // 导致松手延迟、跟手变差。拖动全部在 PollDrag 里按帧轮询完成。
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    const int VK_LBUTTON = 0x01;

    /// <summary>每帧轮询：直接读左键状态和光标位置，不依赖排队的鼠标消息。</summary>
    void PollDrag()
    {
        if (!_pressed) return;
        // 交换了左右键的用户，物理左键对应 VK_RBUTTON
        int vk = WinForms.SystemInformation.MouseButtonsSwapped ? 0x02 : VK_LBUTTON;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (!down) { Release(); return; } // 一松手立刻结束，不等 MouseUp 消息排到

        if (!_dragging)
        {
            GetCursorPos(out var c);
            if (Math.Abs(c.X - _downCursor.X) + Math.Abs(c.Y - _downCursor.Y) < 6) return;
            _dragging = true;
            Brain.BeginDrag();
        }
        DragStep();
    }

    void DragStep()
    {
        GetCursorPos(out var c);
        if (c.X == _lastCursor.X && c.Y == _lastCursor.Y) return;
        // 拖动中不转身：翻面会让身体绕中心镜像，从光标下滑开
        _lastCursor = c;
        SampleVelocity(c);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            _downRect.L + c.X - _downCursor.X, _downRect.T + c.Y - _downCursor.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | SWP_NOCOPYBITS);
    }

    const uint SWP_NOREDRAW = 0x8, SWP_NOCOPYBITS = 0x100;

    // 最近 ~100ms 的光标轨迹，用来估算甩出去的速度（DIP/s）
    readonly System.Collections.Generic.Queue<(double t, double x, double y)> _trail = new();

    void SampleVelocity(POINT c)
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        var p = m.Transform(new Point(c.X, c.Y));
        double now = _clock.Elapsed.TotalSeconds;
        _trail.Enqueue((now, p.X, p.Y));
        while (_trail.Count > 2 && now - _trail.Peek().t > 0.1) _trail.Dequeue();
    }

    /// <summary>当前拖动速度（DIP/s）。松手前停顿超过 0.1s 视为没有甩。</summary>
    public Vector DragVelocity
    {
        get
        {
            if (_trail.Count < 2) return default;
            var a = _trail.Peek();
            var b = System.Linq.Enumerable.Last(_trail);
            double now = _clock.Elapsed.TotalSeconds, dt = b.t - a.t;
            if (dt < 0.005 || now - b.t > 0.1) return default;
            return new Vector((b.x - a.x) / dt, (b.y - a.y) / dt);
        }
    }

    void OnUp(object s, MouseButtonEventArgs e) => Release();

    void Release()
    {
        if (!_pressed) return;
        _pressed = false;            // 先清标志：释放捕获会触发 LostMouseCapture 重入
        Pig.ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            Brain.EndDrag(DragVelocity);
            return;
        }

        var now = DateTime.Now;
        bool dbl = (now - _lastClick).TotalMilliseconds < WinForms.SystemInformation.DoubleClickTime;
        _lastClick = dbl ? DateTime.MinValue : now;
        Brain.OnClick(dbl);
    }

}
