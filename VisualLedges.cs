using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PigPet;

/// <summary>
/// 视觉平台：最大化 / 全屏窗口里没有窗口顶边可站，就“看画面”找能站的地方。
/// 截取小猪身体正下方的一条竖条，逐行比较上下两行的亮度，
/// 如果几乎整条宽度都有明显明暗分界（横贯身体的边缘），就认为这里能站——
/// 例如工具栏下沿、面板/卡片上沿、视频画面上沿。文字、图标的边缘零散，覆盖率不够会被过滤。
/// 截屏不带 CAPTUREBLT，分层窗口（所有小猪）不会被截进去。坐标为 DIP。
/// </summary>
public static class VisualLedges
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr sec, uint off);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    const uint SRCCOPY = 0x00CC0020;

    static double _dpi = 1;
    public static void SetDpi(double scale) => _dpi = scale > 0 ? scale : 1;

    /// <summary>
    /// 找小猪脚下横贯的边缘。现在的 Windows 截屏会把小猪自己也截进去，身体正下方脚底那几行被身体挡住，
    /// 所以不看正下方，而是看身体左右两侧紧挨着的两条竖条（窗口那里是透明的，能看到后面的画面）。
    /// requireBoth=true：两侧都有同一条边缘才算（用于落地，保证边缘横贯脚下）；
    /// false：任一侧有就算（用于站立，走到边缘尽头才掉）。
    /// </summary>
    /// <summary>一条边缘：Y（DIP）和方向（-1 = 往下由亮变暗，例如面板上沿；+1 = 由暗变亮，例如面板下沿）。</summary>
    public readonly record struct Ledge(double Y, int Sign);

    public static List<Ledge> ScanUnder(double cx, double size, double top, double bottom, bool requireBoth)
    {
        double inner = size * 0.47, outer = size * 0.75;
        var left = Scan(cx - outer, cx - inner, top, bottom);
        var right = Scan(cx + inner, cx + outer, top, bottom);
        // 一侧在屏幕外：只看另一侧
        if (cx - outer < 0) return right;
        if (cx + outer > System.Windows.SystemParameters.PrimaryScreenWidth) return left;
        static bool Same(Ledge a, Ledge b) => a.Sign == b.Sign && Math.Abs(a.Y - b.Y) < 3;
        if (!requireBoth)
        {
            var all = new List<Ledge>(left);
            foreach (var r in right) if (!all.Exists(l => Same(l, r))) all.Add(r);
            all.Sort((p, q) => p.Y.CompareTo(q.Y));
            return all;
        }
        var both = new List<Ledge>();
        foreach (var l in left) if (right.Exists(r => Same(l, r))) both.Add(l);
        return both;
    }

    /// <summary>
    /// 在 [left,right] × [top,bottom]（DIP）范围内找可站立的水平边缘，从上到下。
    /// </summary>
    public static List<Ledge> Scan(double left, double right, double top, double bottom)
    {
        var result = new List<Ledge>();
        int x = (int)Math.Round(left * _dpi), y = (int)Math.Round(top * _dpi);
        int w = (int)Math.Round((right - left) * _dpi), h = (int)Math.Round((bottom - top) * _dpi);
        if (w < 8 || h < 3) return result;

        var px = Capture(x, y, w, h);
        if (px == null) return result;

        double threshold = Config.Current.LedgeThreshold; // 亮度差阈值（0~255）
        double coverage = 0.85;                            // 至少这么多列都有分界才算“横贯”
        int lastHit = -10;
        var prev = new int[w];
        for (int c = 0; c < w; c++) prev[c] = Lum(px[c]);
        for (int row = 1; row < h; row++)
        {
            int hits = 0, sum = 0;
            for (int c = 0; c < w; c++)
            {
                int l = Lum(px[row * w + c]), d = l - prev[c];
                if (Math.Abs(d) >= threshold) { hits++; sum += d; }
                prev[c] = l;
            }
            if (hits >= w * coverage)
            {
                // 相邻几行都是边缘（粗边框、阴影）只算最上面那条
                if (row - lastHit > 3) result.Add(new Ledge(top + row / _dpi, sum < 0 ? -1 : 1));
                lastHit = row;
            }
        }
        return result;
    }

    static int Lum(int bgra) => (((bgra >> 16) & 0xFF) * 77 + ((bgra >> 8) & 0xFF) * 150 + (bgra & 0xFF) * 29) >> 8;

    /// <summary>用 GDI 截取屏幕一块区域（物理像素），返回 BGRA 像素，从上到下逐行。</summary>
    static int[]? Capture(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, // 负数 = 自上而下
                biPlanes = 1, biBitCount = 32,
            };
            bmp = CreateDIBSection(mem, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) return null;
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null; // 不带 CAPTUREBLT：不截分层窗口
            var px = new int[w * h];
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
