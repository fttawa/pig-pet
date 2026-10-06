using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace PigPet;

/// <summary>
/// 喂食与体重：饱食度随时间下降，饿了会自己去找吃的（够不着就跳上去、走到边上跳下去），
/// 一口一口吃完回饱食度、回血、长肉。越胖血越厚、越难拎、砸到别的猪越疼。
/// </summary>
public partial class PetBrain
{
    /// <summary>标准体重（kg）：这个体重的猪质量为 1、血量为设置里的上限。</summary>
    const double BaseWeight = 30;

    public double Satiety { get; private set; } = 70;
    public double Weight { get; private set; } = Config.Current.StartWeight;
    DispatcherTimer? _hunger;
    double _nextHungryTalk, _giveUpFoodUntil;

    /// <summary>相对质量（标准猪 = 1）。关掉体重系统时恒为 1。</summary>
    public double Mass => C.WeightEnabled ? Weight / BaseWeight : 1;
    /// <summary>自己加上背上所有猪的总质量（拎起来的手感）。</summary>
    double CarryMass => C.WeightEnabled ? (Weight + LoadAbove) / BaseWeight : 1;
    /// <summary>越胖越慢。</summary>
    double Agility => Math.Clamp(1.25 - 0.25 * Mass, 0.5, 1.1);
    /// <summary>越胖血越厚。</summary>
    double HpScale => C.WeightEnabled ? Math.Clamp(0.5 + 0.5 * Weight / BaseWeight, 0.6, 3) : 1;

    bool Hungry => C.HungerEnabled && Satiety < 40;
    bool Starving => C.HungerEnabled && Satiety < 15;
    /// <summary>还吃得下：用户投喂的东西，不饿也会去吃，撑了就不吃。</summary>
    bool CanEat => Satiety < 100 && Now >= _giveUpFoodUntil;

    void InitFood()
    {
        _hunger = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hunger.Tick += (_, _) => HungerTick();
        _hunger.Start();
    }

    void HungerTick()
    {
        if (!C.HungerEnabled || _paused) return;
        Satiety = Math.Max(0, Satiety - C.HungerRate / 60);
        // 饿着会慢慢掉肉
        if (Satiety < 20) AddWeight(-0.01);

        if (Hungry && Now >= _nextHungryTalk && !_dragging && !_flying)
        {
            _nextHungryTalk = Now + Rand(30, 60);
            w.Say(Starving ? "要饿扁了……" : new[] { "饿了……", "好饿啊", "有吃的吗", "肚子咕咕叫" }[R.Next(4)], 2000);
        }
        // 饿了又闲着，场上有吃的就去吃
        if (Hungry && CanEat && Interruptible && _current != "eat" && FoodWorld.AnyFor(w)) Play("eat");
    }

    /// <summary>增减体重，血量按比例跟着变（不因为长胖凭空掉血百分比）。</summary>
    void AddWeight(double kg)
    {
        if (!C.WeightEnabled || kg == 0) return;
        double before = MaxHp;
        Weight = Math.Clamp(Weight + kg, 10, 200);
        if (Hp > 0) Hp = Math.Min(MaxHp, Hp * MaxHp / before);
    }

    /// <summary>手头的事可以放下去吃东西。</summary>
    bool Interruptible => !_dragging && !_paused && !_flying &&
        (_current is "" or "lazy" or "idle" or "walk" or "sleep" or "shake" or "eat" || _current.StartsWith("form"));

    /// <summary>新食物出现：交给离得最近、吃得下、手头有空的猪。</summary>
    public static void AssignFood(FoodWindow f)
    {
        if (f.ClaimedBy != null) { f.ClaimedBy.Brain.NoticeFood(); return; } // 指定给某只猪的
        var pig = Herd.Pets.Where(p => p.Brain.CanEat && p.Brain.Interruptible && p.Brain._current != "eat")
                           .OrderBy(p => Math.Abs(p.BodyCenter.X - f.Center.X)).FirstOrDefault();
        if (pig == null) return;
        f.ClaimedBy = pig;
        pig.Brain.NoticeFood();
    }

    /// <summary>认领的食物出现 / 被挪了位置：去吃（正在吃就重新找路）。</summary>
    public void NoticeFood()
    {
        if (!CanEat || !Interruptible) return;
        Play("eat");
    }

    /// <summary>托盘“查看状态”：头顶报一下体重、饱食度、血量。</summary>
    public void SayStatus()
    {
        string hp = C.HpEnabled ? $" · 血 {Math.Max(0, Hp):0}/{MaxHp:0}" : "";
        w.Say($"{Weight:0.#}kg · 饱食 {Satiety:0}{hp}", 4000);
    }

    // ---------- 吃 ----------
    static readonly Color Crumb = Color.FromRgb(0xC8, 0x8A, 0x3E);

    async Task Eat(CancellationToken ct)
    {
        bool greeted = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!CanEat) { w.Say("吃不下了", 1500); return; }
            var f = FoodWorld.Claim(w);
            if (f == null) { if (!greeted) w.Say("没有吃的……", 1500); return; }
            if (!greeted) { greeted = true; w.Say(Hungry ? "开饭！" : "有好吃的！", 1200); }

            // 食物还在往下掉：先跑到它下面等着
            if (!f.Grounded)
            {
                Log($"等食物落地 y={f.Bottom:0}");
                await WalkToX(() => f.Center.X, Size * 0.2, 4, ct, () => f.Grounded || f.Gone);
                await Animate(5, (_, _) => { }, ct, () => f.Grounded || f.Gone);
                attempt--; // 等落地不算一次尝试
                continue;
            }

            double feetY = w.PosY + w.Height - Size * 0.08, rise = feetY - f.Bottom;
            Log($"找吃的#{attempt} {f.Kind.Name} 食物({f.Center.X:0},{f.Bottom:0}) 落地={f.Grounded} 我({BodyX:0},{feetY:0}) 高差={rise:0}");
            int side = f.Center.X >= w.PosX + w.Width / 2 ? 1 : -1;
            double standX() => f.Center.X - side * Size * 0.42; // 嘴对着食物时身体中心的位置

            if (IsRiding || rise > Size * 0.25)
            {
                // 食物在高处（或者自己正站在别的猪背上）：跳过去
                if (!IsRiding && Math.Abs(f.Center.X - BodyX) > Size * 3)
                    await WalkToX(() => f.Center.X - Math.Sign(f.Center.X - BodyX) * Size * 1.5, Size * 0.3, 8, ct);
                feetY = w.PosY + w.Height - Size * 0.08;
                w.Say("嘿咻！", 800);
                _throw = Ballistic(standX() - BodyX, feetY - f.Bottom);
                await Fall(ct);
                continue;
            }
            if (rise < -Size * 0.25)
            {
                // 食物在低处：走到所站窗口的边上掉下去（掉下去时这个动作会被打断，落地后接着找吃的）
                if (WindowPlatforms.ByHwnd(_support) is { } p)
                {
                    // 食物在窗口外侧就往那边走；在窗口正下方就走最近的一边
                    bool right = f.Center.X < p.Left || f.Center.X > p.Right ? side > 0 : p.Right - BodyX < BodyX - p.Left;
                    double edge = right ? p.Right + Size * 0.35 : p.Left - Size * 0.35;
                    await WalkToX(() => edge, 2, 8, ct);
                }
                else
                {
                    // 站在画面边缘 / 元素上：往食物那边蹦下去
                    _throw = new Vector(side * 250, -450);
                    await Fall(ct);
                }
                continue;
            }

            // 同一高度：走过去
            if (!await WalkToX(standX, Size * 0.06, 8, ct, () => f.Gone || Math.Abs(f.Bottom - (w.PosY + w.Height - Size * 0.08)) > Size * 0.3))
                continue;
            if (await Munch(f, ct))
            {
                // 吃完一份：还饿、还有吃的就接着找下一份
                if (!CanEat || !FoodWorld.AnyFor(w) || !Hungry) return;
                attempt = -1;
            }
        }
        // 找了半天够不着：放弃一会儿，别一直原地打转
        foreach (var f in FoodWorld.Items) if (f.ClaimedBy == w) f.ClaimedBy = null;
        if (FoodWorld.AnyFor(w) && CanEat) { w.Say("够不着……", 1500); _giveUpFoodUntil = Now + 15; }
    }

    /// <summary>一口一口吃。吃完返回 true；食物被拿走返回 false。</summary>
    async Task<bool> Munch(FoodWindow f, CancellationToken ct)
    {
        w.SetDir(f.Center.X >= BodyX ? 1 : -1);
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.85);
        while (!f.Gone)
        {
            // 食物被拖走了
            double mouth = BodyX + w.Dir * Size * 0.42, feetY = w.PosY + w.Height - Size * 0.08;
            if (Math.Abs(f.Center.X - mouth) > Size * 0.35 || Math.Abs(f.Bottom - feetY) > Size * 0.3) return false;

            bool bitten = false;
            // 低头啃一口再抬头：原图头朝左，负角度是低头
            await Animate(0.5 / K, (t, _) =>
            {
                double p = t * K / 0.5, nod = Math.Sin(Math.PI * Math.Min(1, p));
                w.ARotate.Angle = -12 * nod;
                w.AScale.ScaleX = 1 + 0.03 * nod; w.AScale.ScaleY = 1 - 0.03 * nod;
                if (!bitten && p > 0.5)
                {
                    bitten = true;
                    w.Particle("·", Crumb, w.Dir > 0 ? 0.9 : 0.1);
                    w.Particle("•", Crumb, w.Dir > 0 ? 0.8 : 0.2);
                    if (f.Bite()) Finish(f.Kind);
                }
            }, ct);
            w.ARotate.Angle = 0;
            await Task.Delay(120, ct);
        }
        if (f.Gone && Satiety >= 100)
        {
            // 吃撑了：打个嗝，四脚朝天躺一会儿
            w.Say("嗝~ 吃撑了", 1500);
            await Form("token", ct, Rand(5, 8));
        }
        return f.Gone;
    }

    void Finish(FoodKind kind)
    {
        bool stuffed = Satiety > 90;
        Satiety = Math.Min(120, Satiety + kind.Satiety);
        AddWeight(kind.Satiety * 0.1 * (stuffed ? 2 : 1)); // 撑着还吃长得更快
        if (C.HpEnabled && Hp > 0) Hp = Math.Min(MaxHp, Hp + kind.Heal);
        Log($"吃完{kind.Name}：饱食 {Satiety:0}，体重 {Weight:0.0}kg，血 {Hp:0}/{MaxHp:0}");
        w.Say(new[] { "好吃！", "真香", "哼哼~ 满足", $"{kind.Name}！" }[R.Next(4)], 1500);
        w.Particle("♥", Pink, 0.5);
    }

    /// <summary>投喂：在自己前面掉一份食物（调试用 --play feed:apple）。</summary>
    async Task Feed(string id, CancellationToken ct)
    {
        var kind = FoodKind.ById(id) ?? FoodKind.Random();
        FoodWorld.Drop(kind, BodyX + w.Dir * Size, w);
        await Eat(ct);
    }

    // ---------- 移动工具 ----------
    double BodyX => w.PosX + w.Width / 2;

    /// <summary>
    /// 跳到相对位置 (dx, 高 rise) 需要的初速度：先升到比目标高半个身位的顶点，再落到目标上。
    /// </summary>
    Vector Ballistic(double dx, double rise)
    {
        double g = C.Gravity, h = Math.Max(rise, 0) + Size * 0.5;
        double vy = -Math.Sqrt(2 * g * h), up = -vy / g, down = Math.Sqrt(2 * (h - rise) / g);
        return new Vector(dx / (up + down), vy);
    }

    /// <summary>横着走到身体中心 x = target()（可随时间变化），到了返回 true。</summary>
    async Task<bool> WalkToX(Func<double> target, double tol, double timeout, CancellationToken ct, Func<bool>? abort = null)
    {
        bool arrived = false, stop = false;
        double speed = 150 * K * Agility;
        var b = w.PhysicsBounds();
        await Animate(timeout, (t, dt) =>
        {
            if (arrived || stop) return;
            if (abort?.Invoke() == true) { stop = true; return; }
            double dx = target() - BodyX;
            // 目标在屏幕外够不着：走到边上就算到了
            bool atWall = (dx < 0 && w.PosX <= b.Left + 0.5) || (dx > 0 && w.PosX >= b.Right - 0.5);
            if (Math.Abs(dx) <= tol || atWall) { arrived = true; w.ARotate.Angle = 0; w.AMove.Y = 0; return; }
            w.SetDir(dx > 0 ? 1 : -1);
            double ph = Math.Sin(2 * Math.PI * t * K / 0.4);
            w.ARotate.Angle = 7 * ph;
            w.AMove.Y = -Math.Abs(ph) * Size * 0.07;
            w.MoveRaw(Math.Clamp(w.PosX + w.Dir * Math.Min(Math.Abs(dx), speed * dt), b.Left, b.Right), w.PosY);
        }, ct, () => arrived || stop);
        return arrived;
    }
}
