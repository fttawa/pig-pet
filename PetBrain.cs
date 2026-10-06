using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PigPet;

/// <summary>状态机：加权随机挑选动作，所有动作都可被打断。</summary>
public partial class PetBrain
{
    readonly PetWindow w;
    CancellationTokenSource? _cts;
    bool _paused, _dragging;
    DateTime _lastInteract = DateTime.Now;
    static readonly Random R = Random.Shared;

    static readonly Color Pink = Color.FromRgb(0xFF, 0x5C, 0x8A);
    static readonly Color Blue = Color.FromRgb(0x6B, 0x7C, 0xFF);

    public PetBrain(PetWindow window)
    {
        w = window;
        _onConfig = _ => w.Dispatcher.Invoke(() => { if (!_dragging && _current is "lazy" or "idle") Play("lazy"); });
    }

    Config C => Config.Current;
    double K => Math.Max(0.1, C.Speed);
    double Size => C.Size;

    readonly Action<Config> _onConfig;

    /// <summary>启动。velocity/airborne：分裂或从天而降的小猪一出生就在空中。</summary>
    public void Start(Vector velocity = default, bool airborne = false)
    {
        Config.Changed += _onConfig;
        InitHealth();
        if (airborne) { _throw = velocity; Play("fall"); return; }
        w.Say(Herd.Count > 1 ? "又来一只！" : "哼哼~ 我来啦");
        Play("lazy");
    }

    public void Stop()
    {
        Config.Changed -= _onConfig;
        StopHealth();
        _cts?.Cancel();
    }

    // ---------- 调度 ----------
    public void Play(string name, bool byUser = false)
    {
        if (byUser) _lastInteract = DateTime.Now;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _ = Run(name, cts.Token);
    }

    async Task Run(string name, CancellationToken ct)
    {
        Log($"开始 {name}");
        try
        {
            _current = name;
            w.ShowLazy(name == "lazy");
            bool airborne = name == "fall" || name.StartsWith("throw:");
            // 死着的时候被切去做别的事：先复活，清掉灰色和 ×× 眼
            if (IsDead && !airborne && name != "drag") { Hp = MaxHp * 0.3; Forms.Clear(w); }
            // 从被打断的姿势平滑过渡；飞出去的例外：直接带着当时的角度飞
            if (!airborne) await EaseHome(0.15, ct);
            await (name switch
            {
                "idle" => Idle(ct), "walk" => Walk(ct), "roll" => Roll(ct), "jump" => Jump(ct),
                "spin" => Spin(ct), "shake" => Shake(ct), "sleep" => Sleep(ct), "drag" => Drag(ct),
                "fall" => Fall(ct), "form" => Form(PickForm(), ct),
                "clone" => Clone(ct), "visit" => Visit(ct), "greet" => GreetInvited(ct), "pile" => Pile(ct),
                "beneath" => Beneath(ct), "chase" => Chase(ct), "follow" => Follow(ct), "merge" => Merge(ct),
                "merge-out" => MergeOut(ct), "duel" => Duel(ct), "duel-b" => DuelFollower(ct),
                // 调试 / 脚本：throw:vx,vy 以指定速度把小猪抛出去
                _ when name.StartsWith("throw:") && TryParseVector(name[6..], out var tv) => ThrowAndFall(tv, ct),
                _ when name.StartsWith("burst:") && int.TryParse(name[6..], out var n) => Burst(n, ct),
                _ when name.StartsWith("form:") => Form(name[5..], ct),
                _ => Lazy(ct),
            });
            // 动作结束：回到官方慵懒动画，稍后再随机下一个
            _current = "";
            _partner = null;
            await EaseHome(0.3, ct);
            w.ShowLazy(true);
            if (_paused || _dragging) return;
            await Task.Delay(TimeSpan.FromSeconds(Rand(C.IntervalMin, Math.Max(C.IntervalMin, C.IntervalMax))), ct);
            Play(Choose());
        }
        catch (OperationCanceledException) { Log($"中断 {name}"); }
        catch (Exception ex) { Log($"异常 {name}: {ex}"); }
    }

    // 调试日志：设置环境变量 PIGPET_LOG=<文件路径> 时启用
    static readonly string? LogPath = Environment.GetEnvironmentVariable("PIGPET_LOG");
    static int _nextId;
    readonly int _id = ++_nextId;

    void Log(string msg)
    {
        if (LogPath == null) return;
        try
        {
            System.IO.File.AppendAllText(LogPath,
                $"{DateTime.Now:HH:mm:ss.fff} 猪{_id}{(w.IsClone ? "(克隆)" : "")} {msg} | cur={_current} top={w.Top:0} maxY={w.MaxY:0}" + Environment.NewLine);
        }
        catch { }
    }

    string Choose()
    {
        if (C.Actions.TryGetValue("sleep", out var s) && s.Enabled &&
            (DateTime.Now - _lastInteract).TotalSeconds > C.SleepAfter && R.NextDouble() < 0.5)
            return "sleep";
        var pool = C.Actions.Where(a => a.Value.Enabled && a.Value.Weight > 0 && CoopAllowed(a.Key)).ToList();
        if (pool.Count == 0) return "lazy";
        double r = R.NextDouble() * pool.Sum(a => a.Value.Weight);
        foreach (var a in pool) if ((r -= a.Value.Weight) <= 0) return a.Key;
        return pool[0].Key;
    }

    public void SetPaused(bool p)
    {
        _paused = p;
        if (p) { _cts?.Cancel(); w.ResetTransform(); w.ShowLazy(true); }
        else Play("lazy");
    }

    // ---------- 交互 ----------
    public void OnClick(bool dbl)
    {
        _lastInteract = DateTime.Now;
        if (dbl) { Play("roll"); return; }
        w.Particle("♥", Pink, 0.35); w.Particle("♥", Pink, 0.6);
        if (C.Bubbles.Count > 0) w.Say(C.Bubbles[R.Next(C.Bubbles.Count)]);
        Play(new[] { "jump", "shake", "spin", "roll" }[R.Next(4)]);
    }

    public void BeginDrag()
    {
        _dragging = true;
        if (IsDead) { Hp = MaxHp * 0.3; Forms.Clear(w); }
        w.Say(_form == "stand" ? "挪一次五块！" : "放我下来！", 1500);
        Play("drag");
    }

    Vector _throw;

    public void EndDrag(Vector velocity)
    {
        _dragging = false;
        _lastInteract = DateTime.Now;
        _throw = velocity * C.ThrowStrength;
        if (_throw.Length > 1500) w.Say("哇啊啊——", 1200);
        Play("fall");
    }

    // ---------- 帧工具 ----------
    // WPF 的 Rendering 事件在一帧里可能触发多次（窗口移动会引发额外渲染），
    // 必须按 RenderingTime 去重，否则一帧跑很多次、每次 dt≈0，位移取整后等于没动。
    static TimeSpan _frameTime;
    static bool _frameHooked;

    static Task NextFrame()
    {
        if (!_frameHooked)
        {
            _frameHooked = true;
            CompositionTarget.Rendering += (_, e) => _frameTime = ((RenderingEventArgs)e).RenderingTime;
        }
        var start = _frameTime;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? h = null;
        h = (_, e) =>
        {
            if (((RenderingEventArgs)e).RenderingTime == start) return; // 还是同一帧
            CompositionTarget.Rendering -= h;
            tcs.TrySetResult();
        };
        CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    /// <summary>逐帧回调 f(已过秒数, 本帧秒数)，持续 seconds 秒（≤0 表示直到取消）。</summary>
    static async Task Animate(double seconds, Action<double, double> f, CancellationToken ct, Func<bool>? until = null)
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        while ((seconds <= 0 || sw.Elapsed.TotalSeconds < seconds) && until?.Invoke() != true)
        {
            await NextFrame();
            ct.ThrowIfCancellationRequested();
            double now = seconds > 0 ? Math.Min(seconds, sw.Elapsed.TotalSeconds) : sw.Elapsed.TotalSeconds;
            f(now, now - last);
            last = now;
        }
    }

    /// <summary>把当前缩放/旋转/位移平滑插值回原位。</summary>
    async Task EaseHome(double seconds, CancellationToken ct)
    {
        double sx = w.AScale.ScaleX, sy = w.AScale.ScaleY, ang = w.ARotate.Angle % 360, mx = w.AMove.X, my = w.AMove.Y;
        if (ang > 180) ang -= 360; else if (ang < -180) ang += 360; // 走最短的角度
        bool home = Math.Abs(sx - 1) + Math.Abs(sy - 1) + Math.Abs(ang) + Math.Abs(mx) + Math.Abs(my) < 0.01;
        if (!home)
            await Animate(seconds, (t, _) =>
            {
                double p = t / seconds, e = 1 - Math.Pow(1 - p, 3); // easeOutCubic
                w.AScale.ScaleX = sx + (1 - sx) * e; w.AScale.ScaleY = sy + (1 - sy) * e;
                w.ARotate.Angle = ang * (1 - e); w.AMove.X = mx * (1 - e); w.AMove.Y = my * (1 - e);
            }, ct);
        w.ResetTransform();
    }

    static double Rand(double a, double b) => a + R.NextDouble() * (b - a);

    /// <summary>水平移动窗口，碰边掉头。</summary>
    void Step(double pxPerSec, double dt)
    {
        w.MoveTo(w.Left + w.Dir * pxPerSec * dt, w.Top);
        if ((w.Dir < 0 && w.Left <= w.MinX + 0.5) || (w.Dir > 0 && w.Left >= w.MaxX - 0.5)) w.SetDir(-w.Dir);
    }

    // ---------- 动作 ----------
    // ---------- 表情包形态 ----------
    string? _form;

    string PickForm()
    {
        var on = Forms.All.Where(f => !C.FormsEnabled.TryGetValue(f.Id, out var e) || e).ToList();
        return on.Count == 0 ? "benzene" : on[R.Next(on.Count)].Id;
    }

    async Task Form(string id, CancellationToken ct, double? duration = null)
    {
        try
        {
            _form = id;
            Forms.Apply(w, id);
            // 形态期间锁定 rest 帧，叠加的表情/道具才能对准五官
            w.OverrideFrame = id is "bite" or "dead" && w.RestFrame != null ? Forms.ToGray(w.RestFrame) : w.RestFrame;
            var f = Forms.All.FirstOrDefault(x => x.Id == id);
            w.Say(f?.Text, 3000);
            double dur = duration ?? Rand(8, 14), nextZ = 1;
            int angryBeat = 0, deadTick = -1;
            bool bitten = false;
            await Animate(dur, (t, _) =>
            {
                // 猪吃蛇：吃到一半有概率反被咬
                if (id == "python" && !bitten && t > 4)
                {
                    bitten = true;
                    if (R.NextDouble() < 0.5)
                    {
                        id = _form = "bite";
                        Forms.Apply(w, "bite");
                        w.OverrideFrame = null;
                        if (w.RestFrame != null) w.OverrideFrame = Forms.ToGray(w.RestFrame);
                        w.Say("蛇咬猪", 3000);
                    }
                }
                double breath = Math.Sin(2 * Math.PI * t / (id == "stack" ? 3.5 : 2.4));
                if (id == "angry")
                {
                    // 气呼呼：每 0.9 秒猛一转身，转身时跺一脚
                    int beat = (int)(t / 0.9);
                    if (beat != angryBeat)
                    {
                        angryBeat = beat;
                        w.SetDir(-w.Dir);
                        if (beat % 3 == 2) w.Say("哼！", 700);
                    }
                    double ph = (t % 0.9) / 0.9;
                    double stomp = ph < 0.25 ? Math.Sin(ph / 0.25 * Math.PI) : 0;
                    w.AMove.Y = -Size * 0.05 * stomp;
                    w.AScale.ScaleX = 1 + 0.06 * (1 - stomp) * Math.Max(0, 1 - ph * 4);
                    w.AScale.ScaleY = 1 - 0.06 * (1 - stomp) * Math.Max(0, 1 - ph * 4);
                }
                else if (id == "peek")
                {
                    // 脸放大，从屏幕底边冒出来，只露出脸，停一会儿再缩回去
                    double up = Math.Min(1, t / 0.35), down = Math.Max(0, (t - (dur - 0.5)) / 0.5);
                    double rise = 1 - Math.Pow(1 - up, 3) - down * down;
                    w.Anim.RenderTransformOrigin = new Point(0.29, 0.55);   // 脸的中心
                    w.AScale.ScaleX = w.AScale.ScaleY = 2.4;
                    w.AMove.X = Size * 0.21;                                 // 内层坐标：把脸挪到正中
                    w.AMove.Y = Size * (1.4 - 1.3 * rise) + Size * 0.01 * Math.Sin(2 * Math.PI * t / 0.25) * rise;
                    if (t >= nextZ && rise > 0.9)
                    {
                        w.Particle(R.NextDouble() < 0.5 ? "?!" : "!?", Color.FromRgb(0x22, 0x22, 0x22), R.NextDouble());
                        nextZ = t + 0.5;
                    }
                }
                else if (id == "flat")
                {
                    // 慢慢化开摊成一张猪饼，果冻一样轻颤，最后弹回
                    double melt = 1 - Math.Pow(1 - Math.Min(1, t / 1.2), 3);
                    double back = Math.Max(0, (t - (dur - 0.6)) / 0.6);
                    double k = melt * (1 - back * back);
                    double jelly = 0.015 * Math.Sin(2 * Math.PI * t / 0.6) * k;
                    w.Anim.RenderTransformOrigin = new Point(0.5, 0.92);
                    w.AScale.ScaleX = 1 + 0.35 * k + jelly;
                    w.AScale.ScaleY = 1 - 0.5 * k - jelly;
                }
                else if (id == "token")
                {
                    // 吃饱了四脚朝天躺着，肚子一起一伏
                    w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
                    w.ARotate.Angle = 165;
                    w.AMove.Y = -Size * 0.08;
                    w.AScale.ScaleX = 1 + 0.02 * breath; w.AScale.ScaleY = 1 - 0.02 * breath;
                }
                else if (id == "code")
                {
                    // 写代码写晕了：晃来晃去
                    w.ARotate.Angle = 6 * Math.Sin(2 * Math.PI * t / 1.6);
                }
                else if (id == "cry")
                {
                    // 抽泣：一抽一抽
                    double sob = Math.Max(0, Math.Sin(2 * Math.PI * t / 0.7));
                    w.AScale.ScaleY = 1 - 0.04 * sob; w.AScale.ScaleX = 1 + 0.02 * sob;
                    w.AMove.Y = Size * 0.02 * sob;
                }
                else if (id == "dead")
                {
                    w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
                    // 每 60ms 换一个随机小抖动，模拟原图三帧来回抽
                    int tick = (int)(t / 0.06);
                    if (tick != deadTick)
                    {
                        deadTick = tick;
                        w.ARotate.Angle = 180 + Rand(-2.5, 2.5);
                        w.AMove.X = Size * Rand(-0.012, 0.012);
                        w.AMove.Y = -Size * 0.06 + Size * Rand(-0.01, 0.01);
                    }
                }
                else if (id == "bite")
                {
                    // 四脚朝天，轻轻抽动
                    w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
                    w.ARotate.Angle = 180 + 2 * Math.Sin(2 * Math.PI * t / 0.4) * Math.Exp(-(t - 4) * 0.8);
                    w.AMove.Y = -Size * 0.06;
                }
                else if (id == "coffee" || id == "chopsticks" || id == "python" || id == "crab")
                {
                    // 吃/喝：头一点一点
                    w.ARotate.Angle = -3 + 3 * Math.Sin(2 * Math.PI * t / 0.9);
                }
                else
                {
                    w.AScale.ScaleX = 1 + 0.015 * breath; w.AScale.ScaleY = 1 - 0.015 * breath;
                }
                if (id == "stack" && t >= nextZ) { w.Particle(R.NextDouble() < 0.5 ? "z" : "Z", Blue, w.Dir > 0 ? 0.85 : 0.15); nextZ = t + 1.6; }
            }, ct);
        }
        finally
        {
            _form = null;
            Forms.Clear(w);
        }
    }

    async Task Lazy(CancellationToken ct)
    {
        // 官方用法：播一遍慵懒动画，然后停在 rest 姿势发呆
        double dur = w.PlayOfficial();
        if (R.NextDouble() < 0.3 && C.Bubbles.Count > 0) w.Say(C.Bubbles[R.Next(C.Bubbles.Count)]);
        await Task.Delay(TimeSpan.FromSeconds(dur + Rand(4, 8)), ct);
    }

    Task Idle(CancellationToken ct) => Animate(Rand(3, 6), (t, _) =>
    {
        double s = 1 + 0.04 * Math.Sin(2 * Math.PI * t * K / 2.4);
        w.AScale.ScaleX = s; w.AScale.ScaleY = 2 - s;
    }, ct);

    Task Walk(CancellationToken ct)
    {
        if (R.NextDouble() < 0.5) w.SetDir(-w.Dir);
        return Animate(Rand(3, 7), (t, dt) =>
        {
            double ph = Math.Sin(2 * Math.PI * t * K / 0.5);
            w.ARotate.Angle = 6 * ph;
            w.AMove.Y = -Math.Abs(ph) * Size * 0.06;
            Step(60 * K, dt);
        }, ct);
    }

    async Task Roll(CancellationToken ct)
    {
        if (R.NextDouble() < 0.5) w.SetDir(-w.Dir);
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
        int turns = R.Next(1, 4);
        double perTurn = 0.9 / K, dur = turns * perTurn;
        // 原图朝左，翻转后内部角度取负即与前进方向一致；位移 = 周长 × 圈数，无打滑
        await Animate(dur, (t, dt) =>
        {
            w.ARotate.Angle = -360 * t / perTurn;
            Step(Math.PI * Size / perTurn, dt);
        }, ct);
        w.ARotate.Angle = 0;
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.85);
        await Squash(ct);
    }

    async Task Jump(CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            double dur = 0.8 / K;
            await Animate(dur, (t, _) =>
            {
                double p = Math.Min(1, t / dur);
                // 蓄力 → 腾空（抛物线）→ 落地压扁
                if (p < 0.15) { double q = p / 0.15; w.AScale.ScaleX = 1 + 0.12 * q; w.AScale.ScaleY = 1 - 0.15 * q; w.AMove.Y = 0; }
                else if (p < 0.85)
                {
                    double q = (p - 0.15) / 0.7;
                    w.AMove.Y = -Size * 0.7 * 4 * q * (1 - q);
                    w.AScale.ScaleX = 0.92; w.AScale.ScaleY = 1.08;
                }
                else { double q = (p - 0.85) / 0.15; w.AMove.Y = 0; w.AScale.ScaleX = 1.15 - 0.15 * q; w.AScale.ScaleY = 0.85 + 0.15 * q; }
            }, ct);
        }
    }

    Task Spin(CancellationToken ct)
    {
        double dur = 1.2 / K;
        return Animate(dur, (t, _) =>
        {
            double p = Math.Min(1, t / dur);
            double e = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2; // easeInOut
            w.AScale.ScaleX = Math.Cos(2 * Math.PI * 2 * e);
        }, ct);
    }

    Task Shake(CancellationToken ct)
    {
        w.Say("呜呜呜~", 1200);
        return Animate(1.2, (t, _) => w.AMove.X = Size * 0.04 * Math.Sin(2 * Math.PI * t * K / 0.12), ct);
    }

    /// <summary>趴着睡：身体压低、慢呼吸、头微微垂下，Z 从头顶冒出；不再侧翻出画面。</summary>
    async Task Sleep(CancellationToken ct)
    {
        w.Say("Zzz…", 1500);
        double until = Rand(15, 30), nextZ = 1.5;
        await Animate(until, (t, _) =>
        {
            double lie = 1 - Math.Pow(1 - Math.Min(1, t / 1.5), 3); // 缓缓趴下
            double breath = Math.Sin(2 * Math.PI * t / 3.5);
            w.AScale.ScaleX = 1 + 0.08 * lie + 0.015 * breath;
            w.AScale.ScaleY = 1 - 0.14 * lie - 0.025 * breath;
            w.ARotate.Angle = -6 * lie; // 头往下点一点
            // 原图头朝左；朝右时头在右边
            if (t >= nextZ) { w.Particle(R.NextDouble() < 0.5 ? "z" : "Z", Blue, w.Dir > 0 ? 0.8 : 0.2); nextZ = t + 1.6; }
        }, ct);
        await EaseHome(0.9, ct); // 慢慢起身
    }

    /// <summary>拎着时像钟摆：角度被拖动的水平速度带偏，再弹簧回正。</summary>
    Task Drag(CancellationToken ct)
    {
        var gp = w.GrabPoint;
        double ang = 0, angV = 0;
        return Animate(0, (t, dt) =>
        {
            dt = Math.Min(dt, 0.05);
            double target = Math.Clamp(-w.DragVelocity.X * 0.012, -25, 25); // 往右拖，身体落后向左摆
            angV += ((target - ang) * 60 - angV * 6) * dt; // 弹簧 + 阻尼
            ang += angV * dt;
            // 内层在翻转容器里：朝右时支点 X 与角度都要镜像，屏幕上才始终绕抓取点、往同一侧摆
            bool mirrored = w.Dir > 0;
            w.Anim.RenderTransformOrigin = new Point(mirrored ? 1 - gp.X : gp.X, gp.Y);
            double screenAng = ang + 3 * Math.Sin(2 * Math.PI * t / 1.2); // 悬空轻晃
            w.ARotate.Angle = mirrored ? -screenAng : screenAng;
        }, ct);
    }

    /// <summary>
    /// 刚体物理：重力、抛出速度、墙/地/顶反弹、地面摩擦；
    /// 空中按水平速度自旋，着地按位移纯滚动，撞击时按冲击力压扁。
    /// </summary>
    async Task Fall(CancellationToken ct)
    {
        w.RefreshWorkArea();
        var b = w.PhysicsBounds();
        double x = w.Left, y = w.Top, vx = _throw.X, vy = _throw.Y;
        double g = C.Gravity, e = C.Bounce, r = Size / 2, deg = 180 / Math.PI;
        double angV = 0, squash = 0, squashT = 9;
        bool dead = IsDead; // 被撞飞时可能已经没血了
        _throw = default;

        // 保留松手时的角度。旋转支点要从抓取点换到身体中心才能正常滚动，
        // 换支点会让画面跳一下，所以把偏移 (R - I)(o2 - o) 折算进窗口位置抵消掉。
        double ang0 = w.ARotate.Angle, th = ang0 * Math.PI / 180;
        var o = w.Anim.RenderTransformOrigin;
        double dx = (0.5 - o.X) * Size, dy = (0.5 - o.Y) * Size;
        double offX = dx * Math.Cos(th) - dy * Math.Sin(th) - dx;
        double offY = dx * Math.Sin(th) + dy * Math.Cos(th) - dy;
        if (w.Dir > 0) offX = -offX; // 内层处在镜像容器里
        x += offX; y += offY;
        w.AScale.ScaleX = w.AScale.ScaleY = 1;
        w.AMove.X = w.AMove.Y = 0;
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
        w.MoveRaw(x, y);
        double ang = w.Dir > 0 ? -ang0 : ang0; // 换算成屏幕方向的角度

        var sw = Stopwatch.StartNew();
        double last = 0, restTime = 0;
        while (true)
        {
            await NextFrame();
            ct.ThrowIfCancellationRequested();
            double now = sw.Elapsed.TotalSeconds, dt = Math.Min(0.033, now - last);
            last = now;

            bool grounded = y >= b.Bottom - 0.5 && Math.Abs(vy) < 1;
            if (!grounded) vy += g * dt;
            x += vx * dt; y += vy * dt;

            void Hit(double impact)
            {
                Damage(ImpactDamage(impact));
                if (impact < 150) return;
                squash = Math.Min(0.3, impact / 5000); squashT = 0;
            }
            if (y > b.Bottom) { y = b.Bottom; Hit(vy); vy = vy > 200 ? -vy * e : 0; }
            if (y < b.Top) { y = b.Top; Hit(-vy); vy = -vy * e; }
            if (x < b.Left) { x = b.Left; Hit(-vx); vx = -vx * e; angV = -angV * e; }
            if (x > b.Right) { x = b.Right; Hit(vx); vx = -vx * e; angV = -angV * e; }

            grounded = y >= b.Bottom - 0.5 && Math.Abs(vy) < 1;
            if (grounded)
            {
                vx *= Math.Exp(-C.Friction * dt);
                angV = vx / r * deg;                                         // 纯滚动，无打滑
            }
            else angV += (vx / r * deg * 0.5 - angV) * Math.Min(1, 2 * dt); // 空中被带着转

            ang += angV * dt;
            CollideOthers(x, y, ref vx, ref vy);
            // 血量在这一帧归零：当场死亡，动量照旧，尸体继续翻滚弹跳
            if (!dead && IsDead) { dead = true; BecomeDead(); }

            squashT += dt;
            double s = 1 + squash * Math.Exp(-8 * squashT) * Math.Cos(2 * Math.PI * 3 * squashT);
            w.AScale.ScaleX = s; w.AScale.ScaleY = 2 - s;
            // 翻转容器会镜像旋转方向，按朝向修正，保证顺时针=向右滚
            w.ARotate.Angle = w.Dir > 0 ? -ang : ang;
            w.MoveRaw(x, y);

            restTime = grounded && Math.Abs(vx) < 15 ? restTime + dt : 0;
            if (restTime > 0.15 && squashT > 0.5) break;
        }
        if (dead)
        {
            await DeadSettleAndRevive(ang, ct);
            w.Clamp();
            return;
        }
        await EaseHome(0.5, ct); // 滚停后摆正
        w.Clamp();
    }

    Task Squash(CancellationToken ct) => Animate(0.5, (t, _) =>
    {
        double p = Math.Min(1, t / 0.5);
        // 阻尼弹簧：压扁后来回轻弹两下
        double s = 1 + 0.22 * Math.Exp(-6 * p) * Math.Cos(2 * Math.PI * 2 * p);
        w.AScale.ScaleX = s; w.AScale.ScaleY = 2 - s;
    }, ct);
}
