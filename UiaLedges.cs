using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace PigPet;

/// <summary>
/// 元素平台：用 UI Automation 读取最大化 / 全屏窗口里各个可见元素的位置，
/// 把元素的上边沿当成能站的平台（按钮、输入框、面板、列表、图片、网页区块……）。
/// 比看画面更准：没有颜色分界也认得出，也不会被图片里的线条误导。
/// 读取跨进程、可能较慢，所以在后台 MTA 线程每 0.25 秒刷新一次；拿不到元素的窗口（游戏、视频等）交给看画面识别。
/// 坐标为 DIP。
/// </summary>
public static class UiaLedges
{
    public readonly record struct Edge(double Left, double Right, double Y);

    /// <summary>一个窗口读到的元素边沿。列表按 Z 序从上到下排列。</summary>
    sealed record WindowEdges(Rect Window, List<Edge> Edges, bool Covered);

    static volatile List<WindowEdges> _windows = new();
    static volatile IntPtr[] _targets = Array.Empty<IntPtr>();
    static double _dpi = 1;
    static Thread? _worker;

    public static void SetDpi(double scale) => _dpi = scale > 0 ? scale : 1;

    /// <summary>由窗口扫描告知当前有哪些最大化 / 全屏窗口需要读取元素。</summary>
    public static void SetTargets(IntPtr[] hwnds)
    {
        _targets = hwnds;
        if (_worker == null && hwnds.Length > 0)
        {
            _worker = new Thread(Loop) { IsBackground = true, Name = "UIA 元素读取" };
            _worker.SetApartmentState(ApartmentState.MTA); // UIA 客户端建议在非 UI 的 MTA 线程调用
            _worker.Start();
        }
    }

    /// <summary>这个点所在的窗口是否成功读到了元素（读到了就以元素为准，不再看画面）。</summary>
    public static bool Covers(double x, double y) => TopAt(x, y)?.Covered == true;

    /// <summary>这个点上最上层的那个（被读取的）窗口：下面被挡住的窗口里的元素不算。</summary>
    static WindowEdges? TopAt(double x, double y)
    {
        foreach (var w in _windows) if (w.Window.Contains(x, y)) return w;
        return null;
    }

    /// <summary>找和身体横向重叠至少一半、Y 落在 [fromY,toY] 的元素上边沿，返回最高的。</summary>
    public static Edge? Find(double left, double right, double fromY, double toY)
    {
        double need = (right - left) * 0.5, cx = (left + right) / 2;
        Edge? best = null;
        foreach (var win in _windows)
        foreach (var e in win.Edges)
        {
            if (e.Y < fromY || e.Y > toY) continue;
            if (Math.Min(e.Right, right) - Math.Max(e.Left, left) < need) continue;
            if (TopAt(cx, e.Y) != win) continue; // 这个位置被更上层的窗口挡住了
            if (best == null || e.Y < best.Value.Y) best = e;
        }
        return best;
    }

    // 不适合站的元素类型：文字、链接这类会把每一行字都变成平台；滚动条、分隔线、提示框等也不要
    static readonly HashSet<ControlType> Skip = new()
    {
        ControlType.Text, ControlType.Hyperlink, ControlType.ScrollBar, ControlType.Thumb, ControlType.Separator,
        ControlType.ToolTip, ControlType.TitleBar, ControlType.Window, ControlType.MenuItem,
    };

    static void Loop()
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.TreeScope = TreeScope.Element;
        var visible = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);

        while (true)
        {
            var targets = _targets;
            if (targets.Length == 0 || !Config.Current.ElementLedges) { _windows = new(); Thread.Sleep(500); continue; }

            var windows = new List<WindowEdges>();
            var sw = Stopwatch.StartNew();
            foreach (var hwnd in targets.Take(3))
            {
                try
                {
                    AutomationElementCollection all;
                    Rect win;
                    using (cache.Activate())
                    {
                        var root = AutomationElement.FromHandle(hwnd);
                        win = Scale(root.Cached.BoundingRectangle);
                        all = root.FindAll(TreeScope.Descendants, visible);
                    }
                    var edges = new List<Edge>();
                    foreach (AutomationElement el in all)
                    {
                        if (Skip.Contains(el.Cached.ControlType)) continue;
                        var r = Scale(el.Cached.BoundingRectangle);
                        if (r.IsEmpty || r.Width < 40 || r.Height < 12) continue;
                        if (r.Top <= win.Top + 2 || r.Top >= win.Bottom - 8) continue; // 贴着窗口顶 / 底的大容器不算
                        double l = Math.Max(r.Left, win.Left), rr = Math.Min(r.Right, win.Right);
                        if (rr - l >= 40) edges.Add(new Edge(l, rr, r.Top));
                    }
                    // 读到的元素太少就当没读到，交给看画面
                    windows.Add(new WindowEdges(win, edges, edges.Count >= 3));
                    Perf.Log($"UIA 读取 0x{hwnd.ToInt64():X}：{all.Count} 个元素，{edges.Count} 条边沿，用时 {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) { Perf.Log($"UIA 读取失败：{ex.GetType().Name} {ex.Message}"); }
            }
            _windows = windows;
            // 读一次越慢，间隔越长，避免拖慢目标程序
            Thread.Sleep((int)Math.Clamp(250 + sw.ElapsedMilliseconds, 250, 2000));
        }
    }

    static Rect Scale(Rect r) => r.IsEmpty ? r : new Rect(r.X / _dpi, r.Y / _dpi, r.Width / _dpi, r.Height / _dpi);
}
