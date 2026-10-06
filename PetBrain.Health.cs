using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PigPet;

/// <summary>
/// 隐藏血条：撞击扣血、一段时间不受伤慢慢回血。
/// 血量归零的那一刻当场死亡（保留动量继续翻滚），停下后四脚朝天抽搐，再诈尸爬起来。
/// </summary>
public partial class PetBrain
{
    public double Hp { get; private set; } = double.NaN;
    double _lastDamage;
    DispatcherTimer? _regen;

    double MaxHp => Math.Max(1, C.MaxHp);
    public bool IsDead => C.HpEnabled && Hp <= 0;

    void InitHealth()
    {
        Hp = MaxHp;
        _regen = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _regen.Tick += (_, _) =>
        {
            if (Hp > MaxHp) Hp = MaxHp; // 改小了上限
            // 活着、3 秒内没受伤，才回血
            if (Hp > 0 && Hp < MaxHp && Now - _lastDamage > 3) Hp = Math.Min(MaxHp, Hp + C.HpRegen * 0.5);
        };
        _regen.Start();
    }

    void StopHealth() => _regen?.Stop();

    static bool TryParseVector(string s, out Vector v)
    {
        var p = s.Split(',');
        v = default;
        if (p.Length != 2 || !double.TryParse(p[0], out var x) || !double.TryParse(p[1], out var y)) return false;
        v = new Vector(x, y);
        return true;
    }

    Task ThrowAndFall(Vector v, CancellationToken ct) { _throw = v; return Fall(ct); }

    /// <summary>按撞击速度（DIP/s）换算伤害：轻碰不扣血，越狠扣得越多。</summary>
    static double ImpactDamage(double impact) => Math.Max(0, impact - 1000) / 22;

    /// <summary>扣血。返回这一下是否致命。</summary>
    public bool Damage(double amount)
    {
        if (!C.HpEnabled || amount <= 0 || Hp <= 0) return false;
        Hp = Math.Max(0, Hp - amount);
        Log($"受伤 -{amount:0} 剩余 {Hp:0}/{MaxHp:0}");
        _lastDamage = Now;
        if (C.ShowHpBar) w.ShowHp(Hp / MaxHp);
        if (Hp > 0 && amount > 15 && R.NextDouble() < 0.4) w.Say(Hp / MaxHp < 0.3 ? "快不行了……" : "好痛！", 900);
        return Hp <= 0;
    }

    /// <summary>当场变成死猪：灰色、×× 眼。物理照常进行，保留动量。</summary>
    void BecomeDead()
    {
        Log($"当场死亡 top={w.Top:0}");
        Forms.Clear(w);
        if (w.RestFrame != null) w.OverrideFrame = Forms.ToGray(w.RestFrame);
        Forms.XEyes(w.Props, Forms.GraySkin);
        w.Say("啊——", 800);
        for (int i = 0; i < 4; i++) w.Particle("✦", Gold, R.NextDouble());
    }

    /// <summary>
    /// 尸体停稳后：顺着最近的方向翻成四脚朝天，抽搐一会儿，再诈尸翻身爬起来。
    /// screenAng 为停下时的屏幕角度。
    /// </summary>
    async Task DeadSettleAndRevive(double screenAng, CancellationToken ct)
    {
        // 翻到最近的“肚皮朝天”角度（180° + 360°k）
        double target = 180 + 360 * Math.Round((screenAng - 180) / 360);
        double from = screenAng, my0 = w.AMove.Y;
        await Animate(0.5, (t, _) =>
        {
            double p = t / 0.5, e = 1 - Math.Pow(1 - p, 3);
            double a = from + (target - from) * e;
            w.ARotate.Angle = w.Dir > 0 ? -a : a;
            w.AMove.Y = my0 + (-Size * 0.06 - my0) * e;
            w.AScale.ScaleX = w.AScale.ScaleY = 1;
        }, ct);

        await Form("dead", ct, Rand(2.5, 4)); // 抽搐（结束时恢复正常颜色）

        // 诈尸：一蹦翻回来
        Hp = MaxHp * 0.5;
        Log("诈尸复活");
        w.Say("诈尸！", 1200);
        await Animate(0.6, (t, _) =>
        {
            double p = t / 0.6, e = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2;
            w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
            w.ARotate.Angle = 180 + 180 * e;
            w.AMove.Y = -Size * 0.06 * (1 - e) - Size * 0.5 * Math.Sin(Math.PI * p);
        }, ct);
        w.ResetTransform();
        await Squash(ct);
    }
}
