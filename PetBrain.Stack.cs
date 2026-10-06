using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PigPet;

/// <summary>
/// 叠罗汉：把一只猪放 / 扔到别的猪背上就站在上面，可以一直往上叠。
/// 上面的猪跟着下面那只移动；下面那只被拎走、甩飞、翻滚时，上面的会被一起带飞或颠下来。
/// 太重会伤到下面的猪：砸上去时按体重扣血，背上总重超过自身两倍会被慢慢压伤。
/// </summary>
public partial class PetBrain
{
    /// <summary>表示“站在另一只猪的背上”。</summary>
    static readonly IntPtr PigSupport = new(-3);
    PetWindow? _base;          // 站在谁背上
    bool _flying;              // 正在物理飞行中（被扔、掉落）
    double _lastBaseX, _lastBaseY, _lastRideT, _velX, _velY;
    Vector _baseVel;
    double _crush, _bump, _bumpT = 9, _nextCrushTalk;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public bool IsRiding => _support == PigSupport && _base != null && _base.IsLoaded;
    public PetWindow? Base => IsRiding ? _base : null;
    IEnumerable<PetWindow> Riders => Herd.Pets.Where(p => p.Brain.Base == w);
    public bool HasRider => Riders.Any();

    /// <summary>背上（包括更上面）所有猪的总重（kg）。</summary>
    public double LoadAbove => Riders.Sum(r => r.Brain.Weight + r.Brain.LoadAbove);

    /// <summary>脚底到背的高度：上面的猪站在这里。被压扁时跟着变矮。</summary>
    public double BackHeight => Size * 0.55 * w.BodyScale.ScaleY;

    /// <summary>能让别的猪站上来：站稳了、没在飞、没被拎着。</summary>
    bool CanCarry => w.IsLoaded && !_dragging && !_flying && IsGrounded;

    /// <summary>自己是否（直接或间接）站在 target 背上。</summary>
    bool StandsOn(PetWindow target)
    {
        for (var b = Base; b != null; b = b.Brain.Base) if (b == target) return true;
        return false;
    }

    /// <summary>下落途中脚底穿过了哪只猪的背（取最高的那只）。</summary>
    PetWindow? FindPigBelow(double cx, double prevFeet, double feetNow)
    {
        PetWindow? best = null;
        double bestY = double.MaxValue, feet = Size * 0.08;
        foreach (var o in Herd.Others(w))
        {
            var b = o.Brain;
            if (!b.CanCarry || b.StandsOn(w) || b.HasRider) continue;
            if (Math.Abs(o.PosX + o.Width / 2 - cx) > Size * 0.45) continue;
            double back = o.PosY + o.Height - feet - b.BackHeight;
            if (prevFeet <= back + 2 && feetNow >= back && back < bestY) { best = o; bestY = back; }
        }
        return best;
    }

    void StartRiding(PetWindow under)
    {
        _support = PigSupport;
        _base = under;
        _lastBaseX = _velX = under.PosX; _lastBaseY = _velY = under.PosY;
        _lastRideT = Clock.Elapsed.TotalSeconds;
        _baseVel = default;
        w.BringToFront();
        Log($"站到猪{under.Brain._id}背上（这一摞 {StackDepth + 1} 层）");
    }

    int StackDepth { get { int n = 0; for (var b = Base; b != null; b = b.Brain.Base) n++; return n; } }

    /// <summary>从背上掉下去 / 被带飞。</summary>
    void LeaveBack(Vector v, string? line = null)
    {
        _support = IntPtr.Zero;
        _base = null;
        _throw = v;
        if (line != null) w.Say(line, 1000);
        Play("fall");
    }

    /// <summary>砸到下面的猪：按体重扣它的血、把它压扁一下。</summary>
    void LandOn(PetWindow under, double vy)
    {
        var b = under.Brain;
        double dmg = Math.Max(0, vy - 700) / 25 * Math.Pow(Mass, 1.5) + Math.Max(0, Mass / b.Mass - 1.5) * 8;
        b.Bump(Math.Min(0.35, 0.08 + vy / 6000 * Mass));
        if (dmg > 0)
        {
            Log($"砸到猪{b._id} 速度 {vy:0} 伤害 {dmg:0}");
            if (b.Damage(dmg)) b.CrushedToDeath();
            else if (dmg > 10) under.Say(Mass > 1.6 ? "好重！！" : "哎哟！", 900);
        }
    }

    /// <summary>被砸 / 被压时身体扁一下再弹回。</summary>
    public void Bump(double amount) { _bump = Math.Max(_bump * Math.Exp(-8 * _bumpT), amount); _bumpT = 0; }

    void CrushedToDeath()
    {
        Log("被压死了");
        w.Say("压……扁……了", 1200);
        _throw = default;
        Play("fall"); // 物理里会发现已经死了：翻成四脚朝天，过会儿诈尸
    }

    /// <summary>每个渲染帧：更新胖瘦和压扁，背上的猪跟着下面那只移动。</summary>
    public void RideTick()
    {
        double now = Clock.Elapsed.TotalSeconds;
        UpdateBodyScale(now);
        if (_support != PigSupport || _dragging || _flying || _paused) return;
        var under = _base;
        if (under == null || !under.IsLoaded) { LeaveBack(default); return; }
        var ub = under.Brain;

        double bx = under.PosX, by = under.PosY;
        // 估算下面那只的速度（至少隔 15ms 采样一次，避免一帧多次渲染时 dt≈0 算出离谱的值）
        if (now - _lastRideT >= 0.015)
        {
            double dt = now - _lastRideT;
            var v = new Vector((bx - _velX) / dt, (by - _velY) / dt);
            _baseVel = dt > 0.1 ? v : _baseVel * 0.4 + v * 0.6;
            _velX = bx; _velY = by; _lastRideT = now;
        }
        // 下面那只飞出去了（被甩、被撞、弹起来、翻滚）：一起带飞。
        // 只是轻轻往下落（松手放下、脚下塌了）就继续站在背上，跟着一起落地
        if (ub._flying || ub._current.StartsWith("throw:"))
        {
            double tilt = Math.Abs(Math.IEEERemainder(under.ARotate.Angle, 360));
            if (Math.Abs(_baseVel.X) > 250 || _baseVel.Y < -300 || tilt > 25)
            {
                IgnoreCollision(under, 0.6);
                ub.IgnoreCollision(w, 0.6);
                LeaveBack(_baseVel + new Vector(R.Next(-80, 80), -120));
                return;
            }
        }
        // 下面那只翻滚、蹦跳、冲锋：被颠下来
        if (ub._current is "roll" or "spin" or "jump" or "roam" or "chase" or "follow" or "duel" or "duel-b" or "clone" or "merge-out"
            || ub._current.StartsWith("burst:"))
        {
            LeaveBack(new Vector(R.Next(2) == 0 ? -280 : 280, -650), "哇！");
            return;
        }
        // 保留自己在背上的相对位置（自己在背上走动也算），高度贴着背
        double nx = w.PosX + (bx - _lastBaseX), ny = by - ub.BackHeight;
        _lastBaseX = bx; _lastBaseY = by;
        // 走出了背的范围：掉下去
        if (Math.Abs(nx + w.Width / 2 - (bx + under.Width / 2)) > Size * 0.5)
        {
            LeaveBack(new Vector(w.Dir * 150, 0));
            return;
        }
        if (Math.Abs(nx - w.PosX) > 0.01 || Math.Abs(ny - w.PosY) > 0.01) w.MoveRaw(nx, ny);
    }

    double _scaleX = 1, _scaleY = 1;

    /// <summary>身体缩放 = 胖瘦 × 背上重量压扁 × 被砸的一下。</summary>
    void UpdateBodyScale(double now)
    {
        double dt = Math.Min(0.05, now - _lastScaleT);
        _lastScaleT = now;
        _bumpT += dt;
        double fat = C.WeightEnabled ? Math.Clamp((Weight - BaseWeight) / 90, -0.2, 1) : 0;
        double bump = _bump * Math.Exp(-8 * _bumpT) * Math.Cos(2 * Math.PI * 3 * _bumpT);
        double sq = _crush + bump;
        double sx = (1 + 0.35 * fat) * (1 + sq * 0.6), sy = (1 + 0.04 * fat) * (1 - sq);
        if (Math.Abs(sx - _scaleX) < 0.001 && Math.Abs(sy - _scaleY) < 0.001) return;
        _scaleX = sx; _scaleY = sy;
        w.BodyScale.ScaleX = sx;
        w.BodyScale.ScaleY = sy;
    }

    double _lastScaleT;

    /// <summary>约 30Hz：背上越重压得越扁，超过自身体重两倍开始掉血。</summary>
    void CrushTick(double dt)
    {
        double load = HasRider ? LoadAbove : 0;
        double target = Math.Clamp(load / Math.Max(10, Weight) * 0.06, 0, 0.3);
        _crush += (target - _crush) * Math.Min(1, dt * 6);
        if (!C.WeightEnabled || load <= 0 || IsDead || _flying) return;
        double over = load - Weight * 2;
        if (over <= 0) return;
        if (Now >= _nextCrushTalk) { _nextCrushTalk = Now + Rand(3, 6); Log($"被压着 背上 {load:0}kg 剩余血 {Hp:0}"); w.Say(over > Weight ? "要压扁了！" : "好重……", 1200); }
        if (Damage(over * 0.15 * dt, quiet: true)) CrushedToDeath();
    }

    /// <summary>从背上跳下来。</summary>
    Task HopOff(CancellationToken ct)
    {
        if (!IsRiding) return Task.CompletedTask;
        int side = R.Next(2) == 0 ? -1 : 1;
        w.SetDir(side);
        _support = IntPtr.Zero;
        _base = null;
        _throw = new Vector(side * Rand(250, 400), -Rand(550, 750));
        return Fall(ct);
    }
}
