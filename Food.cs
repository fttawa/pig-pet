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
    /// <summary>吃完 / 消失的食物不丢掉，隐藏起来放进这里，下次直接拿来用。</summary>
    static readonly Stack<FoodWindow> Pool = new();

    /// <summary>新食物出现：通知附近的猪。</summary>
    public static event Action<FoodWindow>? Spawned;

    /// <summary>场上最多几份：再多就让最早那份没人要的消失。</summary>
    public const int MaxItems = 24;

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
        Perf.Log($"投放食物：{(reused ? "复用" : "新建")} {tShow}ms，通知猪 {sw.ElapsedMilliseconds - tShow}ms，场上 {Items.Count} 份，备用 {Pool.Count}");
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
/// 一份食物：有重力，会落在任务栏上方或其他窗口的顶边上，可以用鼠标拖动、甩出去。
/// 被咬一口就变小一圈，咬完消失；没人吃的话 2 分钟后淡出。画在舞台上，吃完会被复用（见 FoodWorld.Pool）。
/// </summary>
public class FoodWindow : UserControl, IStageItem
{
    public FoodKind Kind { get; private set; } = FoodKind.All[0];
    public PetWindow? ClaimedBy { get; set; }
    public int BitesLeft { get; private set; }

    readonly Image _img;
    readonly ScaleTransform _scale = new(1, 1);
    DateTime _born;
    double _x, _y, _vx, _vy, _nextCheck, _angle, _angV, _hitCooldown;
    readonly RotateTransform _rot = new();
    // 拖动：按下时光标相对窗口的偏移，以及最近 ~100ms 的轨迹（估算甩出速度）
    Point _grab;
    readonly Queue<(double t, Point p)> _trail = new();
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

    /// <summary>新建一份放在舞台上、先隐藏待用。</summary>
    public static FoodWindow CreateParked()
    {
        var f = new FoodWindow();
        Stage.Place(f, SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top);
        return f;
    }

    /// <summary>光标是否落在食物上（圆形范围，决定舞台要不要接收鼠标）。</summary>
    public bool HitTest(Point stagePoint, Visual stage)
    {
        if (_gone) return false;
        var lp = stage.TransformToDescendant(this)?.Transform(stagePoint);
        if (lp is not Point q) return false;
        double r = Width / 2 * Math.Max(0.4, _scale.ScaleX);
        return (q - new Point(Width / 2, Height - r)).Length <= r;
    }

    FoodWindow()
    {
        Width = Height = Config.Current.Size * 0.45;
        Visibility = Visibility.Collapsed;
        _img = new Image { RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _scale, Cursor = Cursors.Hand };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        // 外层绕中心旋转（飞行 / 滚动），内层以底边为原点缩放（落地压扁、被咬变小）
        Content = new Grid { Children = { _img }, RenderTransform = _rot, RenderTransformOrigin = new Point(0.5, 0.5) };
        _img.MouseLeftButtonDown += (_, _) => BeginDrag();
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
        _x = x; _y = y; _vx = _vy = 0;
        _angle = _angV = 0; _rot.Angle = 0;
        _dragging = false;
        _grounded = false; _support = IntPtr.Zero; _gone = false;
        Visibility = Visibility.Visible;
        MoveWin();
        Stage.BringToFront(this);
    }

    /// <summary>收走：隐藏，返回自己（交给 FoodWorld.Park 放回备用）。</summary>
    internal FoodWindow Remove()
    {
        _gone = true;
        ClaimedBy = null;
        _dragging = false;
        if (_img.IsMouseCaptured) _img.ReleaseMouseCapture();
        Visibility = Visibility.Collapsed;
        return this;
    }

    // ---------- 物理 ----------
    // 和小猪一样：重力、甩出去的水平速度、撞墙 / 落地反弹、地面摩擦并滚动，空中打转；
    // 飞得快砸到猪会把猪撞开。
    internal void Step(double dt)
    {
        if (_gone) return;
        if (_dragging) { DragStep(); return; }

        // 太久没人吃：淡出消失
        if (ClaimedBy == null && (DateTime.Now - _born).TotalSeconds > 120) { Vanish(); return; }

        var wa = SystemParameters.WorkArea;
        double floor = wa.Bottom - Height, r = Width / 2, deg = 180 / Math.PI;
        _hitCooldown -= dt;
        if (_grounded)
        {
            // 地上还在滚：摩擦减速，滚动角度与位移匹配
            if (Math.Abs(_vx) > 3)
            {
                _vx *= Math.Exp(-3 * dt);
                _x += _vx * dt;
                Walls(wa);
                _angV = _vx / r * deg;
                _angle += _angV * dt;
                if (_support != IntPtr.Zero && WindowPlatforms.Find(_x + Width * 0.3, _x + Width * 0.7, Bottom - 3, Bottom + 3) == null)
                { _grounded = false; _support = IntPtr.Zero; } // 滚出了窗口边缘
                Apply();
                return;
            }
            _vx = 0;
            // 停稳了：慢慢立正
            if (Math.Abs(_angle) > 0.5) { _angle = NearestUpright(_angle, dt); Apply(); }
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
        _x += _vx * dt;
        _y += _vy * dt;
        Walls(wa);
        if (_y < wa.Top) { _y = wa.Top; _vy = -_vy * 0.4; }
        // 空中被带着转
        _angV += (_vx / r * deg * 0.5 - _angV) * Math.Min(1, 2 * dt);
        _angle += _angV * dt;
        HitPigs();
        // 离屏幕顶不到一个猪身高的窗口顶边不落（猪站上去头会出屏，够不着），继续往下掉
        if (_vy > 0 && WindowPlatforms.Find(_x + Width * 0.2, _x + Width * 0.8, prevBottom - 1, Bottom) is { } p
            && p.Y > wa.Top + Config.Current.Size * 1.05)
        {
            _y = p.Y - Height;
            Land(p.Hwnd);
        }
        else if (_y >= floor) { _y = floor; Land(IntPtr.Zero); }
        Apply();
    }

    void Walls(Rect wa)
    {
        if (_x < wa.Left) { _x = wa.Left; _vx = -_vx * 0.5; _angV = -_angV * 0.5; }
        if (_x > wa.Right - Width) { _x = wa.Right - Width; _vx = -_vx * 0.5; _angV = -_angV * 0.5; }
    }

    static double NearestUpright(double a, double dt)
    {
        double target = 360 * Math.Round(a / 360);
        return a + (target - a) * Math.Min(1, dt * 6);
    }

    void Apply()
    {
        _rot.Angle = _angle;
        MoveWin();
    }

    /// <summary>飞得快砸到猪：把猪撞开、扣点血，自己弹回来。</summary>
    void HitPigs()
    {
        double speed = Math.Sqrt(_vx * _vx + _vy * _vy);
        if (speed < 600 || _hitCooldown > 0) return;
        var c = Center;
        foreach (var pig in Herd.Pets)
        {
            var d = pig.BodyCenter - c;
            if (d.Length > pig.PetSize * 0.45 + Width * 0.3) continue;
            _hitCooldown = 0.4;
            pig.Brain.Damage(Math.Max(0, speed - 900) / 60);
            pig.Brain.Knock(new Vector(_vx * 0.25, Math.Min(-250, _vy * 0.2)), Kind.Name + "砸我！");
            _vx = -_vx * 0.4; _vy = -Math.Abs(_vy) * 0.3;
            return;
        }
    }

    void Land(IntPtr support)
    {
        // 摔得快就弹一下，慢了就停住（水平速度保留，接着在地上滚）
        if (_vy > 500) { _vy = -_vy * 0.35; _vx *= 0.8; Squish(); return; }
        _vy = 0; _grounded = true; _support = support;
        Squish();
        // 被扔出去后落地：认领它的猪重新找路，没人认领就叫最近的猪来吃
        if (_thrown)
        {
            _thrown = false;
            if (ClaimedBy != null && ClaimedBy.IsLoaded) ClaimedBy.Brain.NoticeFood();
            else PetBrain.AssignFood(this);
        }
    }

    bool _thrown;

    /// <summary>落地时压扁一下再弹回（在当前大小的基础上，被咬小了也一样）。</summary>
    void Squish()
    {
        double sy = _scale.ScaleX;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(sy * 0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(sy, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void MoveWin() => Stage.Place(this, _x, _y);

    // ---------- 拖动 / 甩 ----------
    // 和小猪一样每帧轮询鼠标，不用 DragMove（那个是模态的，拿不到松手时的速度）
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    static Point CursorDip() => Stage.CursorDip();

    void BeginDrag()
    {
        if (_gone) return;
        var c = CursorDip();
        _grab = new Point(c.X - _x, c.Y - _y);
        _dragging = true; _grounded = false; _support = IntPtr.Zero;
        _vx = _vy = 0;
        _trail.Clear();
        _img.CaptureMouse();
    }

    void DragStep()
    {
        int vk = System.Windows.Forms.SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01;
        double now = (DateTime.Now - _born).TotalSeconds;
        if ((GetAsyncKeyState(vk) & 0x8000) == 0)
        {
            // 松手：按最近 ~100ms 的轨迹甩出去
            _dragging = false;
            _img.ReleaseMouseCapture();
            if (_trail.Count >= 2)
            {
                var a = _trail.Peek();
                var b = _trail.Last();
                double dt = b.t - a.t;
                if (dt > 0.005 && now - b.t < 0.1)
                {
                    _vx = Math.Clamp((b.p.X - a.p.X) / dt * Config.Current.ThrowStrength, -5000, 5000);
                    _vy = Math.Clamp((b.p.Y - a.p.Y) / dt * Config.Current.ThrowStrength, -5000, 5000);
                }
            }
            _angV = _vx / (Width / 2) * 180 / Math.PI * 0.5;
            _thrown = true;
            return;
        }
        var c = CursorDip();
        _trail.Enqueue((now, c));
        while (_trail.Count > 2 && now - _trail.Peek().t > 0.1) _trail.Dequeue();
        _x = c.X - _grab.X; _y = c.Y - _grab.Y;
        MoveWin();
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
}
