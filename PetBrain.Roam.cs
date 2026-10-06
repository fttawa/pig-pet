using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PigPet;

/// <summary>
/// 满屏走：沿着屏幕四周走——地面 → 爬上墙 → 倒挂着走天花板 → 爬下另一面墙 → 回到地面。
/// 走到墙角就转身贴到下一面上（以脚底为圆心旋转 90°）。走够了、回到地面才停。
/// </summary>
public partial class PetBrain
{
    /// <summary>贴着的面的法线（从面指向屏幕里面）：地面 (0,-1)，右墙 (-1,0)，天花板 (0,1)，左墙 (1,0)。</summary>
    static double AngleOf(Vector n) => Math.Atan2(n.X, -n.Y) * 180 / Math.PI;

    async Task Roam(CancellationToken ct)
    {
        w.RefreshWorkArea();
        var wa = w.WorkArea();
        // 只从地面出发（站在窗口上时就普通散步）
        if (w.PosY < w.MaxY - 2) { await Walk(ct); return; }

        double S = Size, corner = S * 0.45, speed = 110 * K * Agility;
        double minDur = C.RoamMode ? Rand(40, 90) : Rand(15, 30);
        if (R.NextDouble() < 0.5) w.SetDir(-w.Dir);
        var n = new Vector(0, -1);          // 当前贴着的面（地面）
        var t = new Vector(w.Dir, 0);       // 前进方向
        var p = new Point(w.PosX + S, wa.Bottom); // 脚底（旋转圆心）
        double ang = 0;
        // 转角动画
        bool turning = false;
        Point p0 = p, p1 = p;
        double a0 = 0, a1 = 0, turnT = 0;
        bool done = false;
        if (C.RoamMode || R.NextDouble() < 0.4) w.Say(new[] { "去看看墙上有什么", "爬墙咯", "猪猪巡逻中" }[R.Next(3)], 1500);
        Log("开始满屏走");

        await Animate(minDur + 120, (time, dt) =>
        {
            if (done) return;
            if (turning)
            {
                turnT += dt;
                double q = Math.Min(1, turnT / 0.35), e = q * q * (3 - 2 * q);
                p = new Point(p0.X + (p1.X - p0.X) * e, p0.Y + (p1.Y - p0.Y) * e);
                ang = a0 + (a1 - a0) * e;
                if (q >= 1) turning = false;
            }
            else
            {
                p += t * speed * dt;
                // 离前面那面墙不到半个身位：转到那面墙上
                double ahead = t.X > 0.5 ? wa.Right - p.X : t.X < -0.5 ? p.X - wa.Left : t.Y > 0.5 ? wa.Bottom - p.Y : p.Y - wa.Top;
                if (ahead <= corner)
                {
                    var nn = -t;
                    p0 = p; p1 = p + t * ahead + n * corner;
                    a0 = ang;
                    double d = AngleOf(nn) - AngleOf(n);
                    while (d > 180) d -= 360;
                    while (d < -180) d += 360;
                    a1 = ang + d;
                    t = n; n = nn;
                    turning = true; turnT = 0;
                    Log($"转到{(n.Y < -0.5 ? "地面" : n.Y > 0.5 ? "天花板" : n.X < 0 ? "右墙" : "左墙")}上 脚底=({p1.X:0},{p1.Y:0}) 角度 {a1:0}");
                }
                // 走够了、回到地面就停
                if (time > minDur && n.Y < -0.5) { done = true; return; }
            }

            // 朝向：内层图片的“前方”是旋转后的 x 轴
            double rad = ang * Math.PI / 180;
            int dir = t.X * Math.Cos(rad) + t.Y * Math.Sin(rad) >= 0 ? 1 : -1;
            if (dir != w.Dir) w.SetDir(dir);
            // 走路的摇摆
            double ph = Math.Sin(2 * Math.PI * time * K / 0.45);
            w.ARotate.Angle = 5 * ph;
            w.AMove.Y = -Math.Abs(ph) * S * 0.05;
            w.SurfaceRotate.Angle = ang;
            w.MoveRaw(p.X - S, p.Y - 2 * S);
        }, ct, () => done);

        // 回到地面：摆正
        w.SurfaceRotate.Angle = 0;
        w.MoveRaw(w.PosX, w.MaxY);
        Log("满屏走结束");
    }

    /// <summary>
    /// 从贴墙状态切回普通状态（被拎走、被撞飞等打断时）：
    /// 去掉贴墙旋转，换算成身体中心不动、带同样角度的普通姿势，后面的物理照常接上。
    /// </summary>
    void LeaveSurface()
    {
        double ang = w.SurfaceRotate.Angle;
        if (Math.Abs(ang) < 0.01) return;
        double S = Size, rad = ang * Math.PI / 180;
        // 身体中心 = 脚底 + 法线方向半个身位
        var pivot = new Point(w.PosX + S, w.PosY + 2 * S);
        var center = pivot + new Vector(Math.Sin(rad), -Math.Cos(rad)) * (S * 0.5);
        w.SurfaceRotate.Angle = 0;
        w.MoveRaw(center.X - S, center.Y - 1.5 * S);
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.5);
        w.ARotate.Angle = w.Dir > 0 ? -ang : ang; // 内层在镜像容器里
        Log($"从墙上下来（角度 {ang:0}）");
    }
}
