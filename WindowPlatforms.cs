using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PigPet;

/// <summary>
/// 窗口碰撞体积：把其他程序窗口露出来的顶边当成可以站立的平台。
/// 按 Z 序从上到下枚举窗口，被上层窗口挡住的那段顶边会被剔除；
/// 最小化、最大化、隐藏（cloaked）、工具窗口和本程序自己的窗口都不算。
/// 坐标统一为 DIP。结果缓存 30ms，所有小猪共用。
/// </summary>
public static class WindowPlatforms
{
    /// <summary>平台：窗口句柄、露出来的顶边线段 [Left,Right]、高度 Y，以及整个窗口的左边界 WinLeft。</summary>
    public readonly record struct Platform(IntPtr Hwnd, double Left, double Right, double Y, double WinLeft);

    static List<Platform> _cache = new();
    // 最大化 / 全屏窗口的区域，以及当时压在它上面的窗口（这些地方由视觉识别决定能不能站）
    static List<(Rect Area, List<(double L, double T, double R, double B)> Above)> _visual = new();
    static readonly List<IntPtr> _visualHwnds = new();
    static long _cacheTick;
    static double _dpi = 1;

    /// <summary>设置窗口所在屏幕的缩放比例（像素 / DIP）。</summary>
    public static void SetDpi(double scale) => _dpi = scale > 0 ? scale : 1;

    public static IReadOnlyList<Platform> All
    {
        get
        {
            if (!Config.Current.WindowCollision) return Array.Empty<Platform>();
            long now = Environment.TickCount64;
            Refresh();
            return _cache;
        }
    }

    static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - _cacheTick > 30)
        {
            (_cache, _visual) = Scan();
            _cacheTick = now;
            UiaLedges.SetTargets(_visualHwnds.ToArray());
        }
    }

    /// <summary>这个点是否落在最大化 / 全屏窗口露出来的区域里（需要靠视觉识别判断能不能站）。</summary>
    public static bool IsVisualArea(double x, double y)
    {
        if (!Config.Current.WindowCollision || !Config.Current.VisualLedges) return false;
        Refresh();
        foreach (var (area, above) in _visual)
        {
            if (!area.Contains(x, y)) continue;
            bool covered = false;
            foreach (var a in above)
                if (x >= a.L && x <= a.R && y >= a.T && y <= a.B) { covered = true; break; }
            if (!covered) return true;
        }
        return false;
    }

    /// <summary>
    /// 找脚下的平台：身体横向范围 [left,right] 与平台重叠，且平台 Y 落在 [fromY, toY] 之间
    /// （下落时 from=上一帧脚底、to=这一帧脚底）。返回最高的那个。
    /// </summary>
    public static Platform? Find(double left, double right, double fromY, double toY)
    {
        Platform? best = null;
        foreach (var p in All)
        {
            if (p.Right <= left || p.Left >= right) continue;
            if (p.Y < fromY || p.Y > toY) continue;
            if (best == null || p.Y < best.Value.Y) best = p;
        }
        return best;
    }

    /// <summary>按窗口句柄找它现在的平台（用于判断站着的窗口是否移动 / 消失）。</summary>
    public static Platform? ByHwnd(IntPtr hwnd)
    {
        Platform? best = null;
        foreach (var p in All)
            if (p.Hwnd == hwnd && (best == null || p.Right - p.Left > best.Value.Right - best.Value.Left)) best = p;
        return best;
    }

    // ---------- 枚举 ----------
    delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    static readonly uint MyPid = (uint)Environment.ProcessId;
    static readonly HashSet<string> IgnoredClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    static (List<Platform>, List<(Rect, List<(double L, double T, double R, double B)>)>) Scan()
    {
        var visual = new List<(Rect, List<(double L, double T, double R, double B)>)>();
        _visualHwnds.Clear();
        double sw = System.Windows.SystemParameters.PrimaryScreenWidth, sh = System.Windows.SystemParameters.PrimaryScreenHeight;
        var wa = System.Windows.SystemParameters.WorkArea;
        var above = new List<(double L, double T, double R, double B)>(); // 已枚举（更靠上）的窗口
        var result = new List<Platform>();
        var cls = new StringBuilder(64);

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;
            double L = r.L / _dpi, T = r.T / _dpi, R = r.R / _dpi, B = r.B / _dpi;
            if (R - L < 80 || B - T < 40) return true; // 太小的不算（提示框、残影等）

            GetWindowThreadProcessId(h, out uint pid);
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            cls.Clear(); GetClassName(h, cls, cls.Capacity);
            bool solid = pid != MyPid && (ex & WS_EX_TOOLWINDOW) == 0 && !IgnoredClasses.Contains(cls.ToString());

            // 可以站的：普通、未最大化、顶边在工作区内且不贴着屏幕顶
            if (solid && !IsZoomed(h) && T > wa.Top + 8 && T < wa.Bottom - 20)
            {
                foreach (var seg in Visible(L, R, T, above))
                    if (seg.R - seg.L >= 40) result.Add(new Platform(h, seg.L, seg.R, T, L));
            }
            // 最大化或铺满整个屏幕（全屏）的窗口：记录区域，交给视觉识别
            bool fullscreen = L <= 1 && T <= 1 && R >= sw - 1 && B >= sh - 1;
            if (solid && (IsZoomed(h) || fullscreen))
            {
                visual.Add((new Rect(L, T, R - L, B - T), new List<(double L, double T, double R, double B)>(above)));
                _visualHwnds.Add(h);
            }
            // 不管能不能站，只要不是本程序的猪，它都会挡住下面窗口的顶边
            if (pid != MyPid && !IgnoredClasses.Contains(cls.ToString())) above.Add((L, T, R, B));
            return true;
        }, IntPtr.Zero);
        return (result, visual);
    }

    /// <summary>顶边 [l,r]@y 去掉被上层窗口挡住的部分，返回露出来的线段。</summary>
    static List<(double L, double R)> Visible(double l, double r, double y, List<(double L, double T, double R, double B)> above)
    {
        var segs = new List<(double L, double R)> { (l, r) };
        foreach (var a in above)
        {
            if (y < a.T - 1 || y > a.B) continue; // 这条顶边不在它的纵向范围里
            var next = new List<(double L, double R)>();
            foreach (var s in segs)
            {
                if (a.R <= s.L || a.L >= s.R) { next.Add(s); continue; }
                if (a.L > s.L) next.Add((s.L, a.L));
                if (a.R < s.R) next.Add((a.R, s.R));
            }
            segs = next;
            if (segs.Count == 0) break;
        }
        return segs;
    }
}
