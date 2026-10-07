using System;
using System.Runtime.InteropServices;

namespace PigPet;

/// <summary>性能相关：屏幕刷新率、动画帧间隔、调试日志。</summary>
public static class Perf
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
    const int VREFRESH = 116;

    /// <summary>主屏刷新率（Hz），读不到时按 60。</summary>
    public static int MonitorHz { get; } = ReadHz();

    static int ReadHz()
    {
        var dc = GetDC(IntPtr.Zero);
        try { int hz = GetDeviceCaps(dc, VREFRESH); return hz > 1 ? hz : 60; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>实际使用的动画帧率：设置了上限就取较小值。</summary>
    public static int EffectiveFps
    {
        get
        {
            int limit = Config.Current.FpsLimit;
            // 0 = 自动：跟随屏幕但最高 60 帧（高刷屏上跑满 144/240 帧会给核显和桌面合成带来不小负担）
            return limit > 0 ? Math.Min(limit, MonitorHz) : Math.Min(60, MonitorHz);
        }
    }

    /// <summary>两次逻辑帧之间的最小间隔，留 1.5ms 余量避免和屏幕刷新节拍错开而丢帧。</summary>
    public static TimeSpan FrameInterval => TimeSpan.FromMilliseconds(Math.Max(0, 1000.0 / EffectiveFps - 1.5));

    static readonly string? LogPath = Environment.GetEnvironmentVariable("PIGPET_LOG");

    public static void Log(string msg)
    {
        if (LogPath == null) return;
        try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}" + Environment.NewLine); }
        catch { }
    }
}
