using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PigPet;

/// <summary>食物种类：图片（Noto Emoji）、回多少饱食度、回多少血、要咬几口。</summary>
public record FoodKind(string Id, string Name, int Satiety, int Heal, int Bites)
{
    public static readonly FoodKind[] All =
    {
        new("apple", "苹果", 15, 5, 3),
        new("corn", "玉米", 20, 5, 4),
        new("watermelon", "西瓜", 30, 10, 5),
        new("greens", "白菜", 10, 3, 3),
        new("cake", "蛋糕", 25, 25, 3),
        new("sweetpotato", "红薯", 25, 8, 4),
    };

    public static FoodKind Random() => All[System.Random.Shared.Next(All.Length)];
    public static FoodKind? ById(string id) => All.FirstOrDefault(k => k.Id == id);
}

/// <summary>场上所有食物。食物被谁“认领”了，别的猪就不去抢。</summary>
public static class FoodWorld
{
    /// <summary>场上（没吃完、没消失）的食物。</summary>
    public static readonly List<FoodWindow> Items = new();
    /// <summary>
    /// 备用窗口：新建一个 WPF 透明窗口要 20~40ms，连点时会卡住动画。
    /// 所以吃完 / 消失的食物窗口不关闭，挪到屏幕外放进这里，下次直接拿来用。
    /// </summary>
    static readonly Stack<FoodWindow> Pool = new();

    /// <summary>新食物出现：通知附近的猪。</summary>
    public static event Action<FoodWindow>? Spawned;

    /// <summary>场上最多几份：再多就让最早那份没人要的消失。</summary>
    public const int MaxItems = 24;

    const int PoolTarget = 8;
    static bool _warming;

    /// <summary>
    /// 后台预先建好备用窗口（每次只建一个，建完让出界面线程再建下一个），
    /// 保证连点时直接复用、不用当场新建。
    /// </summary>
    public static void Prewarm()
    {
        if (_warming || Pool.Count >= PoolTarget || Pool.Count + Items.Count >= MaxItems + PoolTarget) return;
        _warming = true;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _warming = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Pool.Push(FoodWindow.CreateParked());
            Perf.Log($"预建备用窗口 {sw.ElapsedMilliseconds}ms，备用 {Pool.Count}");
            Prewarm();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    internal static void Park(FoodWindow f)
    {
        Items.Remove(f);
        if (!Pool.Contains(f)) Pool.Push(f);
    }

    // 所有食物共用一个物理循环。用输入优先级的定时器而不是 CompositionTarget.Rendering：
    // 在渲染回调里逐帧移动窗口会不停触发新的渲染，把鼠标输入挤到后面，连点时点击要等几百毫秒才响应。
    // 场上没食物时停掉，不占 CPU。
    static System.Windows.Threading.DispatcherTimer? _timer;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static double _last;

    static void Hook()
    {
        if (_timer == null)
        {
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
            { Interval = TimeSpan.FromMilliseconds(15) };
            _timer.Tick += (_, _) => Tick();
        }
        if (_timer.IsEnabled) return;
        _last = Clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    static void Tick()
    {
        if (Items.Count == 0) { _timer!.Stop(); return; }
        double now = Clock.Elapsed.TotalSeconds, dt = Math.Min(0.033, now - _last);
        _last = now;
        if (dt <= 0) return;
        foreach (var f in Items.ToArray()) f.Step(dt);
    }

    /// <summary>扔一份食物下来（默认从屏幕顶部）。x 为 null 时随机挑一只猪，扔到它头顶附近。</summary>
    public static FoodWindow Drop(FoodKind kind, double? x = null, PetWindow? owner = null, double? y = null)
    {
        var wa = SystemParameters.WorkArea;
        double size = Config.Current.Size * 0.45;
        if (x == null)
        {
            var pig = Herd.Pets.Count > 0 ? Herd.Pets[System.Random.Shared.Next(Herd.Pets.Count)] : null;
            x = pig != null ? pig.BodyCenter.X + System.Random.Shared.Next(-150, 150) : wa.Left + wa.Width / 2;
        }
        double left = Math.Clamp(x.Value - size / 2, wa.Left, wa.Right - size);
        // 太多了：最早那份没人认领的直接收走（都被认领了就收走最早的）
        if (Items.Count >= MaxItems)
            Park((Items.FirstOrDefault(i => i.ClaimedBy == null) ?? Items[0]).Remove());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool reused = Pool.Count > 0;
        var f = reused ? Pool.Pop() : FoodWindow.CreateParked();
        f.Spawn(kind, left, y ?? wa.Top, owner);
        Items.Add(f);
        Hook();
        long tShow = sw.ElapsedMilliseconds;
        Spawned?.Invoke(f);
        Perf.Log($"投放食物：{(reused ? "复用" : "新建")}窗口 {tShow}ms，通知猪 {sw.ElapsedMilliseconds - tShow}ms，场上 {Items.Count} 份，备用 {Pool.Count}");
        Prewarm(); // 用掉了就在后台补上
        return f;
    }

    /// <summary>给 pig 找一份食物：优先它已经认领的，否则最近的、没被别人认领的，并认领下来。</summary>
    public static FoodWindow? Claim(PetWindow pig)
    {
        var mine = Items.FirstOrDefault(f => f.ClaimedBy == pig && !f.Gone);
        if (mine != null) return mine;
        var free = Items.Where(f => !f.Gone && (f.ClaimedBy == null || !f.ClaimedBy.IsLoaded))
                        .OrderBy(f => (f.Center - pig.BodyCenter).Length).FirstOrDefault();
        if (free != null) free.ClaimedBy = pig;
        return free;
    }

    public static bool AnyFor(PetWindow pig) =>
        Items.Any(f => !f.Gone && (f.ClaimedBy == null || f.ClaimedBy == pig || !f.ClaimedBy.IsLoaded));
}

/// <summary>
/// 一份食物：透明小窗口，有重力，会落在任务栏上方或其他窗口的顶边上，可以用鼠标拖动、甩出去。
/// 被咬一口就变小一圈，咬完消失；没人吃的话 2 分钟后淡出。窗口会被复用（见 FoodWorld.Pool）。
/// </summary>
public class FoodWindow : Window
{
    public FoodKind Kind { get; private set; } = FoodKind.All[0];
    public PetWindow? ClaimedBy { get; set; }
    public int BitesLeft { get; private set; }

    readonly Image _img;
    readonly ScaleTransform _scale = new(1, 1);
    DateTime _born;
    double _x, _y, _vy, _nextCheck;
    IntPtr _support;
    bool _grounded, _dragging, _gone = true;

    public double Size => Width;
    public Point Center => new(_x + Width / 2, _y + Height / 2);
    /// <summary>食物底部（放在地上时就是地面高度）。</summary>
    public double Bottom => _y + Height;
    public bool Grounded => _grounded;
    /// <summary>吃完了 / 消失了（窗口可能已经拿去当别的食物用了）。</summary>
    public bool Gone => _gone;

    static readonly Dictionary<string, BitmapImage> Images = new();

    /// <summary>食物图片（缓存、冻结，悬浮栏也用）。</summary>
    public static BitmapImage ImageOf(FoodKind kind)
    {
        if (!Images.TryGetValue(kind.Id, out var bmp))
        {
            bmp = new BitmapImage(new Uri($"pack://application:,,,/assets/food/{kind.Id}.png"));
            bmp.Freeze();
            Images[kind.Id] = bmp;
        }
        return bmp;
    }

    const double ParkPos = -32000;

    /// <summary>新建一个窗口并停在屏幕外待用。</summary>
    public static FoodWindow CreateParked()
    {
        var f = new FoodWindow();
        f.Show();
        return f;
    }

    FoodWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowActivated = false;
        Width = Height = Config.Current.Size * 0.45;
        Title = "PigPetFood";
        Left = Top = ParkPos;
        _img = new Image { RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _scale, Cursor = Cursors.Hand };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        Content = _img;
        _img.MouseLeftButtonDown += (_, _) => BeginDrag();
        SourceInitialized += (_, _) => MakeToolWindow();
        Closed += (_, _) => { _gone = true; FoodWorld.Items.Remove(this); };
    }

    /// <summary>拿出来当一份新食物。</summary>
    internal void Spawn(FoodKind kind, double x, double y, PetWindow? owner)
    {
        Kind = kind;
        BitesLeft = kind.Bites;
        ClaimedBy = owner;
        _img.Source = ImageOf(kind);
        _img.ToolTip = kind.Name;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Width = Height = Config.Current.Size * 0.45;
        _born = DateTime.Now;
        _x = x; _y = y; _vy = 0;
        _grounded = false; _support = IntPtr.Zero; _gone = false;
        // 移到位置并放到置顶层的最前面（不激活、不改大小）
        var h = new WindowInteropHelper(this).Handle;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice ?? Matrix.Identity;
        if (h != IntPtr.Zero)
            SetWindowPos(h, new IntPtr(-1), (int)Math.Round(_x * m.M11), (int)Math.Round(_y * m.M22), 0, 0, 0x1 | 0x10);
        else MoveWin();
    }

    /// <summary>收走：挪到屏幕外，返回自己（交给 FoodWorld.Park 放回备用）。</summary>
    internal FoodWindow Remove()
    {
        _gone = true;
        ClaimedBy = null;
        _x = _y = ParkPos;
        MoveWin();
        return this;
    }

    // ---------- 物理 ----------
    internal void Step(double dt)
    {
        if (_dragging || _gone) return;

        // 太久没人吃：淡出消失
        if (ClaimedBy == null && (DateTime.Now - _born).TotalSeconds > 120) { Vanish(); return; }

        var wa = SystemParameters.WorkArea;
        double floor = wa.Bottom - Height;
        if (_grounded)
        {
            // 站着的窗口没了 / 移走了：继续掉（每 0.25 秒查一次就够了）
            _nextCheck -= dt;
            if (_support == IntPtr.Zero || _nextCheck > 0) return;
            _nextCheck = 0.25;
            if (WindowPlatforms.Find(_x, _x + Width, Bottom - 3, Bottom + 3) == null)
            { _grounded = false; _support = IntPtr.Zero; }
            else return;
        }

        double prevBottom = Bottom;
        _vy += Config.Current.Gravity * dt;
        _y += _vy * dt;
        if (WindowPlatforms.Find(_x + Width * 0.2, _x + Width * 0.8, prevBottom - 1, Bottom) is { } p && _vy > 0)
        {
            _y = p.Y - Height;
            Land(p.Hwnd);
        }
        else if (_y >= floor) { _y = floor; Land(IntPtr.Zero); }
        MoveWin();
    }

    void Land(IntPtr support)
    {
        // 摔得快就弹一下，慢了就停住
        if (_vy > 500) { _vy = -_vy * 0.3; return; }
        _vy = 0; _grounded = true; _support = support;
        Squish();
    }

    /// <summary>落地时压扁一下再弹回（在当前大小的基础上，被咬小了也一样）。</summary>
    void Squish()
    {
        double sy = _scale.ScaleX;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(sy * 0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(sy, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void MoveWin()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) { Left = _x; Top = _y; return; }
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice ?? Matrix.Identity;
        SetWindowPos(h, IntPtr.Zero, (int)Math.Round(_x * m.M11), (int)Math.Round(_y * m.M22), 0, 0, 0x1 | 0x4 | 0x10);
    }

    // ---------- 拖动 ----------
    void BeginDrag()
    {
        if (_gone) return;
        _dragging = true; _grounded = false; _support = IntPtr.Zero;
        try { DragMove(); } catch (InvalidOperationException) { }
        _dragging = false;
        _x = Left; _y = Top; _vy = 0;
        // 被挪了位置：原来认领它的猪重新找路
        if (ClaimedBy != null && ClaimedBy.IsLoaded) ClaimedBy.Brain.NoticeFood();
    }

    // ---------- 被吃 ----------
    /// <summary>被咬一口：变小一圈。咬完返回 true（这份吃完了）。</summary>
    public bool Bite()
    {
        if (_gone) return true;
        BitesLeft--;
        if (BitesLeft <= 0) { FoodWorld.Park(Remove()); return true; }
        double s = Math.Max(0.15, (double)BitesLeft / Kind.Bites);
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(150));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        return false;
    }

    internal void Vanish()
    {
        _gone = true;
        ClaimedBy = null;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.8));
        fade.Completed += (_, _) => { if (_gone) FoodWorld.Park(Remove()); };
        BeginAnimation(OpacityProperty, fade);
    }

    // ---------- Win32 ----------
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    void MakeToolWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000); // 工具窗口、不抢焦点
    }
}
