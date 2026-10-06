using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace PigPet;

/// <summary>猪群合作：自我复制、串门贴贴、叠罗汉、追逐、合体，以及飞行中互相撞击。</summary>
public partial class PetBrain
{
    static readonly Color Gold = Color.FromRgb(0xFF, 0xB3, 0x00);
    static readonly string[] Greets = { "哼哼！", "你也是猪？", "贴贴", "一起摸鱼", "好巧啊", "我是本体！" };

    PetWindow? _partner;   // 合作对象
    string _current = "";  // 正在执行的动作

    /// <summary>空闲、站在地上、没被拖着，才能被别的猪拉来合作。</summary>
    public bool CoopReady => !_dragging && !_paused && _current is "lazy" or "idle" or "walk" or "" &&
                             w.Top >= w.MaxY - 2;

    /// <summary>被别的猪邀请参与合作动作（打断自己当前动作）。</summary>
    public void Invite(string action, PetWindow partner)
    {
        _partner = partner;
        Play(action);
    }

    // ---------- 自我复制 ----------
    async Task Clone(CancellationToken ct)
    {
        if (Herd.Count >= C.MaxPets) { w.Say("猪满啦，分不动了"); await Task.Delay(1500, ct); return; }
        w.Say("要分裂啦……", 1500);
        // 蓄力：越抖越快、身体越鼓
        await Animate(1.2, (t, _) =>
        {
            double p = t / 1.2;
            w.AScale.ScaleX = 1 + 0.25 * p + 0.03 * Math.Sin(2 * Math.PI * t * (4 + 10 * p));
            w.AScale.ScaleY = 1 - 0.12 * p;
            w.AMove.X = Size * 0.02 * Math.Sin(2 * Math.PI * t * (6 + 14 * p));
        }, ct);
        for (int i = 0; i < 6; i++) w.Particle("✦", Gold, R.NextDouble());
        w.Say("啵！", 1000);
        // 一分为二：新猪往一边飞，自己往另一边飞
        int side = R.Next(2) == 0 ? -1 : 1;
        var clone = Herd.Spawn(new Point(w.Left, w.Top), new Vector(side * Rand(350, 600), -Rand(900, 1300)), isClone: true);
        // 刚分出来两只完全重叠，先互相忽略碰撞，免得一出生就把对方撞飞
        IgnoreCollision(clone, 1.2);
        clone.Brain.IgnoreCollision(w, 1.2);
        _ = clone.Dispatcher.BeginInvoke(() => clone.SetDir(side));
        _throw = new Vector(-side * Rand(350, 600), -Rand(900, 1300));
        w.ResetTransform();
        await Fall(ct);
    }

    // ---------- 一键分裂：一次喷出 N 只 ----------
    public const int HardMaxPets = 50; // 每只猪都是一个透明窗口，太多会卡

    /// <summary>一次性分裂出 count 只克隆，呈扇形喷出。超过上限时自动调高上限（最多 HardMaxPets）。</summary>
    async Task Burst(int count, CancellationToken ct)
    {
        int room = HardMaxPets - Herd.Count;
        count = Math.Min(count, room);
        if (count <= 0) { w.Say($"最多 {HardMaxPets} 只，分不动了"); await Task.Delay(1500, ct); return; }
        if (Herd.Count + count > C.MaxPets)
        {
            var c = C.Clone();
            c.MaxPets = Herd.Count + count;
            Config.Save(c);
            w.Say($"上限调到 {c.MaxPets} 只", 1500);
            await Task.Delay(600, ct);
        }

        w.Say($"分裂 ×{count}！", 1500);
        // 蓄力时间随数量略增，越多抖得越狠
        double charge = Math.Min(2.2, 1.0 + count * 0.04);
        await Animate(charge, (t, _) =>
        {
            double p = t / charge;
            w.AScale.ScaleX = 1 + 0.35 * p + 0.04 * Math.Sin(2 * Math.PI * t * (5 + 14 * p));
            w.AScale.ScaleY = 1 - 0.15 * p;
            w.AMove.X = Size * 0.03 * Math.Sin(2 * Math.PI * t * (8 + 16 * p));
        }, ct);
        for (int i = 0; i < 10; i++) w.Particle("✦", Gold, R.NextDouble());
        w.Say("砰！", 1000);
        w.ResetTransform();

        // 扇形喷出：从左上到右上均匀分布角度，速度带点随机
        var born = new System.Collections.Generic.List<PetWindow> { w };
        for (int i = 0; i < count; i++)
        {
            double a = Math.PI * (0.15 + 0.7 * (count == 1 ? 0.5 : i / (double)(count - 1))); // 27°~153°
            double speed = Rand(900, 1500);
            var v = new Vector(-Math.Cos(a) * speed, -Math.Sin(a) * speed);
            var clone = Herd.Spawn(new Point(w.Left, w.Top), v, isClone: true);
            int dir = v.X >= 0 ? 1 : -1;
            _ = clone.Dispatcher.BeginInvoke(() => clone.SetDir(dir));
            born.Add(clone);
        }
        // 刚出生全部重叠在一起：彼此先不碰撞
        foreach (var a in born)
            foreach (var b in born)
                if (a != b) a.Brain.IgnoreCollision(b, 1.5);

        _throw = new Vector(0, -Rand(500, 800)); // 自己原地弹起
        await Fall(ct);
    }

    // ---------- 串门贴贴 ----------
    async Task Visit(CancellationToken ct)
    {
        var target = Herd.Nearest(w, p => p.Brain.CoopReady);
        if (target == null) { await Lazy(ct); return; }
        w.Say("去找" + (target.IsClone ? "分身" : "本体") + "玩", 1500);
        if (!await WalkTo(target, Size * 1.0, 8, ct)) return;
        if (!target.Brain.CoopReady) { w.Say("诶？跑了"); return; }
        target.Brain.Invite("greet", w);
        await Greet(target, ct);
    }

    /// <summary>两只面对面：互相蹭两下、冒爱心、说话。双方各自执行这一段。</summary>
    async Task Greet(PetWindow other, CancellationToken ct)
    {
        w.SetDir(other.BodyCenter.X > w.BodyCenter.X ? 1 : -1);
        w.Say(Greets[R.Next(Greets.Length)], 2000);
        double start = w.Left;
        await Animate(1.6, (t, _) =>
        {
            // 往对方方向蹭两下
            double bump = Math.Max(0, Math.Sin(2 * Math.PI * t / 0.8));
            // 内层坐标里 -X 永远是头的方向（翻转容器负责朝向）
            w.AMove.X = -Size * 0.08 * bump;
            w.AScale.ScaleX = 1 + 0.06 * bump; w.AScale.ScaleY = 1 - 0.06 * bump;
        }, ct);
        w.Particle("♥", Pink, 0.4); w.Particle("♥", Pink, 0.6);
        await Task.Delay(800, ct);
    }

    async Task GreetInvited(CancellationToken ct)
    {
        var other = _partner;
        if (other == null || !other.IsLoaded) return;
        await Task.Delay(300, ct); // 稍晚一点回应，更自然
        await Greet(other, ct);
    }

    // ---------- 叠罗汉：爬到别的猪身上睡觉 ----------
    async Task Pile(CancellationToken ct)
    {
        var target = Herd.Nearest(w, p => p.Brain.CoopReady);
        if (target == null) { await Lazy(ct); return; }
        if (!await WalkTo(target, Size * 0.9, 8, ct)) return;
        if (!target.Brain.CoopReady) return;

        target.Brain.Invite("beneath", w);
        // 起跳落到对方背上
        double x0 = w.Left, y0 = w.Top, x1 = target.Left, y1 = target.Top - Size * 0.55;
        await Animate(0.7, (t, _) =>
        {
            double p = t / 0.7;
            w.MoveRaw(x0 + (x1 - x0) * p, y0 + (y1 - y0) * p - Size * 0.8 * 4 * p * (1 - p));
        }, ct);
        w.MoveRaw(x1, y1);
        w.SetDir(target.Dir);
        await Squash(ct);

        // 在群友身上睡觉：自己闭眼，下面那只被压成 ××
        w.OverrideFrame = w.RestFrame;
        Forms.ClosedEyes(w.Props);
        w.Say("在群友身上睡觉", 2500);
        double tx = target.Left, ty = target.Top, nextZ = 1;
        bool supportMoved = false;
        try
        {
            await Animate(Rand(12, 20), (t, _) =>
            {
                if (supportMoved) return;
                double b = Math.Sin(2 * Math.PI * t / 3.5);
                w.AScale.ScaleX = 1 + 0.015 * b; w.AScale.ScaleY = 1 - 0.015 * b;
                if (t >= nextZ) { w.Particle("Z", Blue, w.Dir > 0 ? 0.85 : 0.15); nextZ = t + 1.6; }
                // 下面那只被拖走或动了：掉下来
                if (!target.IsLoaded || Math.Abs(target.Left - tx) > 3 || Math.Abs(target.Top - ty) > 3)
                    supportMoved = true;
            }, ct, () => supportMoved);
        }
        finally { Forms.Clear(w); }
        if (supportMoved)
        {
            w.Say("哎哟！", 1000);
            _throw = default;
            await Fall(ct);
            return;
        }

        // 睡醒跳下来，叫醒下面那只
        if (target.IsLoaded && target.Brain._current == "beneath") target.Brain.Play("lazy");
        _throw = new Vector(-w.Dir * 300, -600);
        await Fall(ct);
    }

    /// <summary>被压在下面：××眼，偶尔抽一下，直到上面那只离开。</summary>
    async Task Beneath(CancellationToken ct)
    {
        w.OverrideFrame = w.RestFrame;
        Forms.XEyes(w.Props);
        try
        {
            bool done = false;
            await Animate(40, (t, _) =>
            {
                w.AScale.ScaleX = 1.06; w.AScale.ScaleY = 0.94; // 被压扁一点
                // 上面那只走了就结束（给它 1 秒起跳时间）
                if (t > 1 && (_partner == null || !_partner.IsLoaded || _partner.Brain._current != "pile")) done = true;
            }, ct, () => done);
        }
        finally { Forms.Clear(w); }
    }

    // ---------- 追逐 ----------
    async Task Chase(CancellationToken ct)
    {
        var target = Herd.Nearest(w, p => p.Brain.CoopReady);
        if (target == null) { await Lazy(ct); return; }
        w.Say("来追我呀~", 1500);
        target.Brain.Invite("follow", w);
        // 背对追兵跑，碰边掉头
        w.SetDir(w.BodyCenter.X > target.BodyCenter.X ? 1 : -1);
        await Animate(Rand(5, 8), (t, dt) =>
        {
            double ph = Math.Sin(2 * Math.PI * t * K / 0.35);
            w.ARotate.Angle = 8 * ph;
            w.AMove.Y = -Math.Abs(ph) * Size * 0.08;
            Step(130 * K, dt);
        }, ct);
        if (target.IsLoaded && target.Brain._current == "follow") target.Brain.Play("lazy");
        w.Say("跑不动了……", 1500);
    }

    async Task Follow(CancellationToken ct)
    {
        var leader = _partner;
        if (leader == null) return;
        await Task.Delay(400, ct);
        w.Say("站住！", 1200);
        bool done = false;
        await Animate(10, (t, dt) =>
        {
            if (done) return;
            if (!leader.IsLoaded || leader.Brain._current != "chase") { done = true; return; }
            double dx = leader.BodyCenter.X - w.BodyCenter.X;
            if (Math.Abs(dx) > Size * 0.6) w.SetDir(dx > 0 ? 1 : -1);
            double ph = Math.Sin(2 * Math.PI * t * K / 0.35);
            w.ARotate.Angle = 8 * ph;
            w.AMove.Y = -Math.Abs(ph) * Size * 0.08;
            if (Math.Abs(dx) > Size * 0.7) Step(120 * K, dt);
        }, ct, () => done);
    }

    // ---------- 两猪对峙 ----------
    static readonly string[] Taunts = { "你瞅啥？", "看什么看！", "这是我的地盘！", "来啊！" };
    static readonly string[] Retorts = { "瞅你咋地！", "看你咋了！", "哼！", "谁怕谁！" };
    bool _duelCharge; // 发起方喊冲锋时置位，跟随方据此开冲

    /// <summary>怒眼 + 青筋，锁定 rest 帧。</summary>
    void PutOnAngryFace()
    {
        w.OverrideFrame = w.RestFrame;
        Forms.AngryEyes(w.Props);
        Forms.AngerMark(w.Props);
    }

    /// <summary>瞪眼跺脚：每 0.6 秒跺一下。</summary>
    void GlareStomp(double t)
    {
        double ph = (t % 0.6) / 0.6;
        double stomp = ph < 0.3 ? Math.Sin(ph / 0.3 * Math.PI) : 0;
        w.AMove.Y = -Size * 0.04 * stomp;
        w.AMove.X = Size * 0.03 * stomp; // 内层坐标 +X 是身后：跺脚时往后蹬
    }

    /// <summary>发起方：走到对面站定，瞪眼对骂，然后冲锋相撞。</summary>
    async Task Duel(CancellationToken ct)
    {
        var target = Herd.Nearest(w, p => p.Brain.CoopReady);
        if (target == null) { await Lazy(ct); return; }

        // 先拉开到合适的对峙距离（太近就后退，太远就走过去）
        double want = Size * 1.6;
        if (Math.Abs(target.BodyCenter.X - w.BodyCenter.X) > want && !await WalkTo(target, want, 8, ct)) return;
        if (!target.Brain.CoopReady) return;
        target.Brain._duelCharge = false;
        target.Brain.Invite("duel-b", w);

        try
        {
            w.SetDir(target.BodyCenter.X > w.BodyCenter.X ? 1 : -1);
            PutOnAngryFace();
            await Task.Delay(300, ct);
            w.Say(Taunts[R.Next(Taunts.Length)], 1800);

            // 对峙：跺脚、两猪之间噼里啪啦冒闪电
            double nextSpark = 0.5;
            await Animate(3.2, (t, _) =>
            {
                GlareStomp(t);
                if (t >= nextSpark)
                {
                    w.Particle("ϟ", Gold, w.Dir > 0 ? 1.15 : -0.15);
                    nextSpark = t + 0.35;
                }
            }, ct, () => !target.IsLoaded || target.Brain._current != "duel-b");
            if (!target.IsLoaded || target.Brain._current != "duel-b") return;

            // 冲锋！两只同时冲向对方，身体相碰时互相弹飞
            w.Say("冲啊！", 800);
            target.Brain._duelCharge = true;
            bool hit = false;
            await Animate(3, (t, dt) =>
            {
                if (hit) return;
                double ph = Math.Sin(2 * Math.PI * t / 0.25);
                w.ARotate.Angle = 8 * ph;
                w.AMove.Y = -Math.Abs(ph) * Size * 0.06;
                w.MoveTo(w.PosX + w.Dir * 420 * K * dt, w.PosY);
                if (Math.Abs(target.BodyCenter.X - w.BodyCenter.X) < Size * 0.8) hit = true;
            }, ct, () => hit || !target.IsLoaded);
            if (!hit) return;

            for (int i = 0; i < 6; i++) w.Particle("✦", Gold, w.Dir > 0 ? 0.9 + R.NextDouble() * 0.4 : -0.3 + R.NextDouble() * 0.4);
            bool iLose = R.Next(2) == 0;
            (iLose ? this : target.Brain).Damage(35);
            double myPower = iLose ? 900 : 450, hisPower = iLose ? 450 : 900;
            Forms.Clear(target);
            target.Brain.Knock(new Vector(w.Dir * hisPower, -hisPower * 0.9), iLose ? "我赢了！" : "呜……");
            Forms.Clear(w);
            _throw = new Vector(-w.Dir * myPower, -myPower * 0.9);
            w.Say(iLose ? "呜……" : "我赢了！", 1500);
            IgnoreCollision(target, 1);
            target.Brain.IgnoreCollision(w, 1);
            await Fall(ct);
        }
        finally { Forms.Clear(w); }
    }

    /// <summary>应战方：转身瞪回去、回嘴、跺脚，听到冲锋就冲；被撞飞由发起方处理。</summary>
    async Task DuelFollower(CancellationToken ct)
    {
        var leader = _partner;
        if (leader == null) return;
        try
        {
            w.SetDir(leader.BodyCenter.X > w.BodyCenter.X ? 1 : -1);
            PutOnAngryFace();
            await Task.Delay(1300, ct);
            w.Say(Retorts[R.Next(Retorts.Length)], 1800);
            bool over = false;
            await Animate(8, (t, dt) =>
            {
                if (!leader.IsLoaded || leader.Brain._current != "duel") { over = true; return; }
                if (!_duelCharge) { GlareStomp(t); return; }
                double ph = Math.Sin(2 * Math.PI * t / 0.25);
                w.ARotate.Angle = 8 * ph;
                w.AMove.Y = -Math.Abs(ph) * Size * 0.06;
                if (Math.Abs(leader.BodyCenter.X - w.BodyCenter.X) > Size * 0.8)
                    w.MoveTo(w.PosX + w.Dir * 420 * K * dt, w.PosY);
            }, ct, () => over);
        }
        finally { Forms.Clear(w); }
    }

    // ---------- 合体：克隆走回别的猪身上消失 ----------
    async Task Merge(CancellationToken ct)
    {
        var target = Herd.Nearest(w, p => p.Brain.CoopReady);
        if (!w.IsClone || target == null) { await Lazy(ct); return; }
        w.Say("合体！", 1500);
        if (!await WalkTo(target, Size * 0.5, 8, ct)) return;
        await MergeOut(ct);
        target.Brain.Play("jump");
    }

    /// <summary>缩小、转圈、冒星星然后消失。</summary>
    async Task MergeOut(CancellationToken ct)
    {
        if (!w.IsClone) { w.Say("我是本体，收不回去！"); return; }
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
        await Animate(0.6, (t, _) =>
        {
            double p = t / 0.6;
            w.AScale.ScaleX = w.AScale.ScaleY = 1 - p;
            w.ARotate.Angle = 540 * p;
        }, ct);
        for (int i = 0; i < 5; i++) w.Particle("✦", Gold, R.NextDouble());
        await Task.Delay(400, ct);
        w.Close();
    }

    // ---------- 物理碰撞 ----------
    readonly Dictionary<PetWindow, double> _hitCooldown = new();

    /// <summary>飞行中撞到别的猪：把动量传给对方，自己被弹开。</summary>
    static double Now => Environment.TickCount64 / 1000.0; // 全局时钟（秒），冷却跨多次下落有效

    /// <summary>暂时不与某只猪碰撞（例如刚分裂出来时两只重叠）。</summary>
    public void IgnoreCollision(PetWindow other, double seconds) => _hitCooldown[other] = Now + seconds;

    void CollideOthers(double x, double y, ref double vx, ref double vy)
    {
        double now = Now;
        var me = new Point(x + w.Width / 2, y + w.Height - Size / 2);
        foreach (var o in Herd.Others(w))
        {
            if (o.Brain._dragging) continue;
            if (_hitCooldown.TryGetValue(o, out var until) && now < until) continue;
            var d = o.BodyCenter - me;
            double dist = d.Length;
            if (dist > Size * 0.75 || dist < 1) continue;
            var n = d / dist;
            double approach = vx * n.X + vy * n.Y; // 朝对方的速度分量
            if (approach < 150) continue;
            _hitCooldown[o] = now + 0.4;
            o.Brain.IgnoreCollision(w, 0.4); // 对方也别立刻反撞回来
            o.Brain.Damage(Math.Max(0, approach - 600) / 25);
            Damage(Math.Max(0, approach - 600) / 45);
            // 等质量弹性碰撞（打折）：对方拿走大部分法向动量
            o.Brain.Knock(new Vector(n.X * approach * 0.8, n.Y * approach * 0.8 - 350));
            vx -= n.X * approach * 0.9;
            vy -= n.Y * approach * 0.9;
        }
    }

    /// <summary>被撞飞。</summary>
    public void Knock(Vector v, string? line = null)
    {
        if (_dragging) return;
        _throw = v;
        w.Say(line ?? (R.NextDouble() < 0.5 ? "哎哟！" : "谁撞我！"), 1200);
        Play("fall");
    }

    // ---------- 工具 ----------
    /// <summary>走到目标身边（身体中心水平距离小于 gap），超时返回 false。</summary>
    async Task<bool> WalkTo(PetWindow target, double gap, double timeout, CancellationToken ct)
    {
        Log($"WalkTo 目标x={target.BodyCenter.X:0} 我x={w.BodyCenter.X:0} gap={gap:0}");
        bool arrived = false;
        double speed = 160 * K; // 赶路比散步快
        // 超时按距离估算，至少给 timeout 秒
        timeout = Math.Max(timeout, Math.Abs(target.BodyCenter.X - w.BodyCenter.X) / speed + 3);
        await Animate(timeout, (t, dt) =>
        {
            if (arrived || !target.IsLoaded) return;
            double dx = target.BodyCenter.X - w.BodyCenter.X;
            if (Math.Abs(dx) <= gap) { arrived = true; w.ARotate.Angle = 0; w.AMove.Y = 0; return; }
            w.SetDir(dx > 0 ? 1 : -1);
            double ph = Math.Sin(2 * Math.PI * t * K / 0.4);
            w.ARotate.Angle = 7 * ph;
            w.AMove.Y = -Math.Abs(ph) * Size * 0.07;
            w.MoveTo(w.PosX + w.Dir * Math.Min(Math.Abs(dx) - gap + 1, speed * dt), w.PosY);
        }, ct, () => arrived);
        Log($"WalkTo 结束 arrived={arrived} 我x={w.BodyCenter.X:0} 目标x={target.BodyCenter.X:0}");
        return arrived;
    }

    /// <summary>当前环境下哪些合作动作可选。</summary>
    bool CoopAllowed(string action) => action switch
    {
        "clone" => Herd.Count < C.MaxPets,
        "visit" or "pile" or "chase" or "duel" => Herd.Others(w).Any(p => p.Brain.CoopReady),
        "merge" => w.IsClone && Herd.Count > 1,
        _ => true,
    };
}
