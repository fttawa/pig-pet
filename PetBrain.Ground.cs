using System;
using System.Windows.Threading;

namespace PigPet;

/// <summary>
/// 站立时的地面巡检（约 30Hz）：站在窗口上时跟着窗口移动；脚下空了就掉下去。
/// </summary>
public partial class PetBrain
{
    IntPtr _support;       // 正站在哪个窗口上（0 = 任务栏上方的地面或不在窗口上）
    /// <summary>表示“站在全屏画面里识别出的边缘上”，不对应任何窗口。</summary>
    static readonly IntPtr VisualSupport = new(-1);
    /// <summary>表示“站在 UI Automation 读到的元素上边沿上”。</summary>
    static readonly IntPtr ElementSupport = new(-2);
    int _ledgeMiss;
    int _ledgeSign = -1; // 站着的画面边缘的方向（见 VisualLedges.Ledge.Sign）
    double _supportWinLeft;
    DispatcherTimer? _ground;
    double _lastGroundTick = Now;

    /// <summary>站在地面或某个窗口上。</summary>
    public bool IsGrounded => w.PosY >= w.MaxY - 2 || _support != IntPtr.Zero;

    void SetSupport(IntPtr hwnd)
    {
        if (hwnd != _support)
            Log(hwnd == IntPtr.Zero ? "不在窗口上"
                : hwnd == VisualSupport ? $"站到画面边缘上，y={w.PosY:0}"
                : hwnd == ElementSupport ? $"站到元素上边沿上，y={w.PosY:0}"
                : hwnd == PigSupport ? "站到猪背上"
                : $"站到窗口 0x{hwnd.ToInt64():X} 上，y={w.PosY:0}");
        _ledgeMiss = 0;
        _support = hwnd;
        if (hwnd != IntPtr.Zero && WindowPlatforms.ByHwnd(hwnd) is { } p) _supportWinLeft = p.WinLeft;
    }

    void InitGround()
    {
        _ground = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _ground.Tick += (_, _) => GroundTick();
        _ground.Start();
    }

    /// <summary>这些动作自己处理位置（飞行、被拎着、叠罗汉等），巡检不插手。</summary>
    bool MovesOnItsOwn => _dragging || _paused || _flying || _current is "fall" or "drag" or "clone" or "roam" or "merge-out" or "duel"
                          || _current.StartsWith("throw:") || _current.StartsWith("burst:");

    void GroundTick()
    {
        double now = Now;
        CrushTick(Math.Min(0.2, now - _lastGroundTick));
        _lastGroundTick = now;
        if (MovesOnItsOwn || !w.IsLoaded) return;
        if (_support == PigSupport) return; // 在猪背上：位置由 RideTick 逐帧跟随
        if (w.PosY >= w.MaxY - 2) { _support = IntPtr.Zero; return; } // 在任务栏上方的地面上

        double feet = Size * 0.08, feetY = w.PosY + w.Height - feet;
        double cx = w.PosX + w.Width / 2, l = cx - Size * 0.3, r = cx + Size * 0.3;

        // 站在元素上边沿上：元素随页面滚动就跟着走（元素位置每 0.25 秒刷新，所以防抖放宽到约 0.4 秒）
        if (_support == ElementSupport)
        {
            if (WindowPlatforms.IsVisualArea(cx, feetY) && UiaLedges.Find(l, r, feetY - 70, feetY + 70) is { } el)
            {
                _ledgeMiss = 0;
                double dy = el.Y - feetY;
                if (Math.Abs(dy) > 1)
                {
                    // 平滑靠过去，而不是每次刷新跳一下
                    double step = Math.Abs(dy) < 3 ? dy : dy * 0.5;
                    w.MoveRaw(w.PosX, w.PosY + step);
                    if (Math.Abs(dy) > 5) Log($"跟随元素移动 dy={dy:0}");
                }
                return;
            }
            if (++_ledgeMiss < 12) return;
        }

        // 站在画面边缘上：边缘上下移动（页面滚动）就跟着走，连续几次找不到才掉
        if (_support == VisualSupport)
        {
            if (WindowPlatforms.IsVisualArea(cx, feetY))
            {
                var ls = VisualLedges.ScanUnder(cx, Size, feetY - 70, feetY + 70, requireBoth: false); // 一次滚动可能挪好几十像素
                // 只跟同方向的边缘：否则面板上移时可能误跟到更近的面板下沿
                ls.RemoveAll(lg => lg.Sign != _ledgeSign);
                if (ls.Count > 0)
                {
                    double best = ls[0].Y;
                    foreach (var lg in ls) if (Math.Abs(lg.Y - feetY) < Math.Abs(best - feetY)) best = lg.Y;
                    _ledgeMiss = 0;
                    if (Math.Abs(best - feetY) > 1)
                    {
                        w.MoveRaw(w.PosX, w.PosY + best - feetY);
                        Log($"跟随画面边缘移动 dy={best - feetY:0}");
                    }
                    return;
                }
            }
            if (++_ledgeMiss < 3) return; // 防抖：画面闪一下不至于立刻掉
        }

        // 站着的窗口还在：跟着它移动（窗口被拖动 / 改变大小）
        if (_support != IntPtr.Zero && _support != VisualSupport && _support != ElementSupport)
        {
            foreach (var p in WindowPlatforms.All)
            {
                if (p.Hwnd != _support) continue;
                double dx = p.WinLeft - _supportWinLeft, dy = p.Y - feetY;
                // 只在线段仍托着身体时跟随（考虑水平平移后的位置）
                if (p.Right <= l + dx || p.Left >= r + dx || Math.Abs(dy) > 400) continue;
                _supportWinLeft = p.WinLeft;
                if (Math.Abs(dx) > 0.3 || Math.Abs(dy) > 0.3)
                {
                    w.MoveRaw(w.PosX + dx, w.PosY + dy);
                    Log($"跟随窗口移动 dx={dx:0} dy={dy:0} → ({w.PosX:0},{w.PosY:0})");
                }
                return;
            }
        }

        // 换到了别的窗口上（例如走到相邻窗口的顶边）
        if (WindowPlatforms.Find(l, r, feetY - 3, feetY + 3) is { } q) { SetSupport(q.Hwnd); return; }
        // 走到了另一个元素的上边沿上
        if (WindowPlatforms.IsVisualArea(cx, feetY) && UiaLedges.Covers(cx, feetY))
        {
            if (UiaLedges.Find(l, r, feetY - 4, feetY + 4) != null) { SetSupport(ElementSupport); return; }
        }
        // 走到了全屏画面里的另一条边缘上
        else
        if (WindowPlatforms.IsVisualArea(cx, feetY) && VisualLedges.ScanUnder(cx, Size, feetY - 4, feetY + 4, requireBoth: true) is { Count: > 0 } here)
        {
            _ledgeSign = here[0].Sign;
            SetSupport(VisualSupport);
            return;
        }

        // 脚下空了：掉下去
        _support = IntPtr.Zero;
        Log("脚下没有支撑，掉落");
        _throw = default;
        Play("fall");
    }
}
