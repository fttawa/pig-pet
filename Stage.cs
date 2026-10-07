using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace PigPet;

/// <summary>舞台上能被鼠标点中的东西（猪、食物）：给出某个舞台坐标点是否落在它不透明的部分上。</summary>
public interface IStageItem
{
    bool HitTest(Point stagePoint, Visual stage);
}

/// <summary>
/// 舞台：每个显示器一个盖住工作区的透明置顶窗口，所有小猪和食物都画在里面。
/// 以前一只猪 / 一份食物就是一个窗口，数量一多合成开销大、新建窗口还会卡；现在只有一个窗口。
///
/// 透明：不用 AllowsTransparency（那会每帧把整屏内容读回 CPU 再提交），而是 DWM 合成的透明背景，保留显卡加速。
/// 点击：窗口平时完全穿透（WS_EX_TRANSPARENT），光标移到猪 / 食物的不透明像素上时才接收鼠标，
///       按住（拖动）期间保持接收，松手后恢复穿透。
/// 坐标：全部用全局 DIP（屏幕像素 / 系统缩放），舞台负责换算成自己画布上的位置。
/// </summary>
public class Stage : Window
{
    public static readonly List<Stage> All = new();
    /// <summary>屏幕像素 / DIP。</summary>
    public static double Scale { get; private set; } = 1;

    public Canvas Layer { get; } = new() { ClipToBounds = false };
    /// <summary>舞台范围（全局 DIP），即这块显示器的工作区。</summary>
    public Rect Area { get; private set; }

    static int _z;
    static DispatcherTimer? _hitTimer;
    bool _passThrough = true;
    IntPtr _hwnd;

    /// <summary>每只猪 / 每份食物一个小窗口（Config.WindowPerPig，启动时定下）。</summary>
    public static bool PerItem { get; private set; }
    /// <summary>每个东西自己的小窗口（PerItem 模式）。All 仍然是每个显示器一块，只用来算工作区，不显示。</summary>
    static readonly List<Stage> Own = new();
    static readonly Dictionary<FrameworkElement, Stage> _own = new();
    bool _isOwn;
    /// <summary>小窗口四周留的空白（转圈、贴墙倒挂、气泡会超出控件本身的范围）。</summary>
    double _pad;

    public static void Init()
    {
        PerItem = Config.Current.WindowPerPig;
        // 系统缩放：主屏物理宽度 / DIP 宽度
        Scale = WinForms.Screen.PrimaryScreen!.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        foreach (var s in WinForms.Screen.AllScreens)
        {
            var wa = s.WorkingArea;
            var st = new Stage(new Rect(wa.X / Scale, wa.Y / Scale, wa.Width / Scale, wa.Height / Scale));
            All.Add(st);
            if (!PerItem) st.Show();
        }
        _hitTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _hitTimer.Tick += (_, _) => UpdatePassThrough();
        _hitTimer.Start();
        Config.Changed += c => { foreach (var st in All.Concat(Own)) st.Topmost = c.AlwaysOnTop; };
    }

    Stage(Rect area)
    {
        Area = area;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = Config.Current.AlwaysOnTop;
        Background = Brushes.Transparent;
        Title = "PigPetStage";
        Left = area.X; Top = area.Y; Width = area.Width; Height = area.Height;
        Content = Layer;
        SourceInitialized += (_, _) => MakeOverlay();
    }

    void MakeOverlay()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // 背景透明：交给 DWM 按像素的 alpha 合成
        HwndSource.FromHwnd(_hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref m);
        // 工具窗口（不进 Alt+Tab）、不抢焦点、分层 + 穿透
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE)
            | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        // WPF 自己改窗口样式时（比如设置置顶）会按它缓存的样式整体覆盖，把分层 / 穿透标志冲掉，
        // 导致整个舞台挡住鼠标。所以拦截样式修改，强制保留这两个标志。
        // HwndSource 的钩子在 WPF 自己的处理之前执行，改了也会被它再冲掉，
        // 所以用系统的子类化：先让 WPF 处理完，最后再补上标志。
        _subclass = StyleSubclass;
        SetWindowSubclass(_hwnd, _subclass, UIntPtr.Zero, IntPtr.Zero);
        SetPassThrough(true);
        // 位置用像素精确设置一次（避免 DIP 取整造成一像素偏差）
        SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(Area.X * Scale), (int)Math.Round(Area.Y * Scale),
            (int)Math.Round(Area.Width * Scale), (int)Math.Round(Area.Height * Scale), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    int WantedExStyle(int ex) => (ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) is var e && _passThrough
        ? e | WS_EX_TRANSPARENT : e & ~WS_EX_TRANSPARENT;

    const int WM_STYLECHANGING = 0x7C;
    [StructLayout(LayoutKind.Sequential)] struct STYLESTRUCT { public int Old, New; }

    delegate IntPtr SubclassProc(IntPtr h, int msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    SubclassProc? _subclass; // 必须留着引用，否则委托被回收后窗口过程会崩
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, int msg, IntPtr wp, IntPtr lp);

    IntPtr StyleSubclass(IntPtr h, int msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        var r = DefSubclassProc(h, msg, wp, lp); // 先交给 WPF
        if (msg == WM_STYLECHANGING && wp.ToInt32() == GWL_EXSTYLE)
        {
            var ss = Marshal.PtrToStructure<STYLESTRUCT>(lp);
            int want = WantedExStyle(ss.New);
            if (want != ss.New) { ss.New = want; Marshal.StructureToPtr(ss, lp, false); }
        }
        return r;
    }

    void SetPassThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        _passThrough = on;
        // 按窗口的实际样式比较（不信自己缓存的状态），不一致就改
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE), want = WantedExStyle(ex);
        if (ex == want) return;
        bool wasLayered = (ex & WS_EX_LAYERED) != 0;
        SetWindowLong(_hwnd, GWL_EXSTYLE, want);
        if (!wasLayered) SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
    }

    /// <summary>按光标位置决定每个舞台要不要接收鼠标。</summary>
    static void UpdatePassThrough()
    {
        var p = CursorDip();
        bool through = Config.Current.ClickThrough;
        foreach (var st in All.Concat(Own))
        {
            if (st._hwnd == IntPtr.Zero) continue;
            bool hit = false;
            if (!through)
            {
                // 正按着（拖动中）就一直接收，不然光标一快就脱手
                if (Mouse.Captured is DependencyObject d && st.IsAncestorOf(d)) hit = true;
                else if (st.Area.Contains(p)) hit = st.HitAt(new Point(p.X - st.Area.X, p.Y - st.Area.Y));
            }
            st.SetPassThrough(!hit);
        }
    }

    /// <summary>调试：全局 DIP 点 p 是否点得中舞台上的东西。</summary>
    public static bool DebugHit(Point p) => (PerItem ? Own.Where(s => s.Area.Contains(p)) : new[] { For(p) })
        .Any(st => st.HitAt(new Point(p.X - st.Area.X, p.Y - st.Area.Y)));

    bool HitAt(Point local)
    {
        // 从最上层往下找
        foreach (var el in Layer.Children.OfType<UIElement>()
                     .Where(e => e.Visibility == Visibility.Visible)
                     .OrderByDescending(Panel.GetZIndex))
            if (el is IStageItem item && item.HitTest(local, Layer)) return true;
        return false;
    }

    // ---------- 摆放 ----------
    /// <summary>离这个点最近的舞台。</summary>
    public static Stage For(Point p)
    {
        Stage? best = null;
        double bestD = double.MaxValue;
        foreach (var st in All)
        {
            if (st.Area.Contains(p)) return st;
            double dx = Math.Max(0, Math.Max(st.Area.Left - p.X, p.X - st.Area.Right));
            double dy = Math.Max(0, Math.Max(st.Area.Top - p.Y, p.Y - st.Area.Bottom));
            if (dx * dx + dy * dy < bestD) { bestD = dx * dx + dy * dy; best = st; }
        }
        return best ?? All[0];
    }

    /// <summary>把元素放到全局 DIP 位置 (x, y)（左上角）。中心移到别的显示器时换到那块舞台上。</summary>
    public static void Place(FrameworkElement el, double x, double y)
    {
        double w = double.IsNaN(el.Width) ? 0 : el.Width, h = double.IsNaN(el.Height) ? 0 : el.Height;
        if (PerItem) { PlaceOwn(el, x, y, w, h); return; }
        var st = For(new Point(x + w / 2, y + h / 2));
        // 拖动中不换舞台（会丢掉鼠标捕获），松手后的下一次移动再换
        if (el.Parent != st.Layer && (el.Parent == null || !el.IsMouseCaptureWithin))
        {
            (el.Parent as Panel)?.Children.Remove(el);
            st.Layer.Children.Add(el);
        }
        if (el.Parent is Canvas c && c.Parent is Stage cur) st = cur;
        Canvas.SetLeft(el, x - st.Area.X);
        Canvas.SetTop(el, y - st.Area.Y);
    }

    public static void Remove(FrameworkElement el)
    {
        (el.Parent as Panel)?.Children.Remove(el);
        if (_own.Remove(el, out var st)) { Own.Remove(st); st.Close(); }
    }

    /// <summary>移到最上层（叠罗汉时上面的猪要盖住下面那只）。</summary>
    public static void BringToFront(UIElement el)
    {
        Panel.SetZIndex(el, ++_z);
        if (el is FrameworkElement fe && _own.TryGetValue(fe, out var st) && st._hwnd != IntPtr.Zero)
            SetWindowPos(st._hwnd, st.Topmost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- 一个东西一个窗口 ----------
    // 窗口只比东西本身大一圈，跟着它移动（只挪位置，不改大小，不会闪）；
    // 东西在窗口画布里的位置固定在 (pad, pad)。
    static void PlaceOwn(FrameworkElement el, double x, double y, double w, double h)
    {
        double pad = Math.Max(w, h) / 2;
        var area = new Rect(x - pad, y - pad, w + 2 * pad, h + 2 * pad);
        if (!_own.TryGetValue(el, out var st))
        {
            st = new Stage(area) { _isOwn = true, _pad = pad };
            _own[el] = st; Own.Add(st);
            (el.Parent as Panel)?.Children.Remove(el);
            st.Layer.Children.Add(el);
            Canvas.SetLeft(el, pad); Canvas.SetTop(el, pad);
            st.Show();
            return;
        }
        if (pad != st._pad) { st._pad = pad; Canvas.SetLeft(el, pad); Canvas.SetTop(el, pad); }
        st.MoveOwn(area);
    }

    void MoveOwn(Rect area)
    {
        bool resize = Math.Abs(area.Width - Area.Width) > 0.01 || Math.Abs(area.Height - Area.Height) > 0.01;
        Area = area;
        if (_hwnd == IntPtr.Zero) { Left = area.X; Top = area.Y; Width = area.Width; Height = area.Height; return; }
        SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(area.X * Scale), (int)Math.Round(area.Y * Scale),
            (int)Math.Round(area.Width * Scale), (int)Math.Round(area.Height * Scale),
            SWP_NOZORDER | SWP_NOACTIVATE | (resize ? 0 : SWP_NOSIZE));
    }

    /// <summary>光标位置（全局 DIP）。</summary>
    public static Point CursorDip()
    {
        GetCursorPos(out var c);
        return new Point(c.X / Scale, c.Y / Scale);
    }

    // ---------- Win32 ----------
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_ALPHA = 0x2, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
}
