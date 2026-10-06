using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace PigPet;

/// <summary>
/// 后台线程把官方 Lottie 动画预渲染成冻结的位图序列，播放时只切换 Image.Source，
/// 避免每帧在 UI 线程实时矢量渲染导致卡顿。
/// </summary>
public static class LottieFrames
{
    public const double Fps = 30;

    /// <summary>官方文件里 "rest" 标记所在时间（秒）：动画播完后停留的休息姿势。</summary>
    public static double RestSeconds { get; private set; } = 205 / 60.0;

    // 同一尺寸的帧序列所有小猪共用，多开不重复渲染、不重复占内存
    static readonly System.Collections.Generic.Dictionary<int, Task<BitmapSource[]>> Cache = new();

    /// <summary>内嵌的官方动画 JSON（assets/pig-lottie.json），单文件发布也能读到。</summary>
    static byte[] LoadJson()
    {
        var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/assets/pig-lottie.json"))!;
        using var ms = new MemoryStream();
        res.Stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static Task<BitmapSource[]> GetAsync(int pixelSize)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(pixelSize, out var t)) Cache[pixelSize] = t = RenderAsync(LoadJson(), pixelSize);
            return t;
        }
    }

    /// <summary>
    /// 多核并行渲染：Skottie 的动画对象不能多线程共用，所以每个工作线程各自加载一份动画、
    /// 各用一块画布，按帧号分段渲染。帧之间互不依赖，结果与单线程完全一致。
    /// </summary>
    static Task<BitmapSource[]> RenderAsync(byte[] json, int pixelSize) => Task.Run(() =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ReadRestMarker(json);
        int count;
        using (var probe = CreateAnimation(json)) count = Math.Max(1, (int)Math.Round(probe.Duration.TotalSeconds * Fps));

        var frames = new BitmapSource[count];
        int workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8); // 给 UI 线程留一个核
        if (int.TryParse(Environment.GetEnvironmentVariable("PIGPET_WORKERS"), out var wOverride) && wOverride > 0) workers = wOverride; // 调试：指定线程数
        int chunk = (count + workers - 1) / workers;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, wi =>
        {
            int from = wi * chunk, to = Math.Min(count, from + chunk);
            if (from >= to) return;
            using var anim = CreateAnimation(json);
            var info = new SKImageInfo(pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            var rect = new SKRect(0, 0, pixelSize, pixelSize);
            var buffer = new byte[pixelSize * pixelSize * 4];
            for (int i = from; i < to; i++)
            {
                anim.SeekFrameTime(TimeSpan.FromSeconds(i / Fps));
                canvas.Clear(SKColors.Transparent);
                anim.Render(canvas, rect);
                canvas.Flush();
                using var img = surface.Snapshot();
                unsafe
                {
                    fixed (byte* p = buffer)
                        img.ReadPixels(info, (IntPtr)p, pixelSize * 4, 0, 0);
                }
                var bmp = BitmapSource.Create(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32, null, buffer, pixelSize * 4);
                bmp.Freeze(); // 冻结后可跨线程交给 UI
                frames[i] = bmp;
            }
        });
        Perf.Log($"预渲染 {count} 帧 @{pixelSize}px，{workers} 线程，用时 {sw.ElapsedMilliseconds}ms");
        return frames;
    });

    static Animation CreateAnimation(byte[] json)
    {
        using var stream = new MemoryStream(json);
        if (!Animation.TryCreate(stream, out var anim) || anim == null)
            throw new InvalidDataException("无法解析 Lottie 文件");
        return anim;
    }

    static void ReadRestMarker(byte[] json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            double fr = doc.RootElement.GetProperty("fr").GetDouble();
            foreach (var m in doc.RootElement.GetProperty("markers").EnumerateArray())
                if (m.GetProperty("cm").GetString() == "rest") RestSeconds = m.GetProperty("tm").GetDouble() / fr;
        }
        catch { /* 没有标记就用默认值 */ }
    }
}
