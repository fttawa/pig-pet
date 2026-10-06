using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PigPet;

/// <summary>
/// 表情包形态：道具与表情全部用矢量绘制，叠在官方 rest 帧上。
/// 坐标系 = 512×512 的 rest 帧（小猪朝左）：左眼(90,240) 右眼(210,265) 鼻子中心(115,310) 脚底 y≈470。
/// Back 层在身体后，Props 层在身体前（两者随朝向镜像），Front 层不镜像（放带文字的道具）。
/// </summary>
public static class Forms
{
    public record Form(string Id, string Label, string Text);

    public static readonly Form[] All =
    {
        new("stand", "站街", "挪一次五块"),
        new("benzene", "苯猪", "苯猪"),
        new("chopsticks", "我吃一点", "你好 我吃一点"),
        new("python", "猪吃蛇", "猪吃蛇"),
        new("crab", "猪吃螃蟹", "猪吃螃蟹"),
        new("coffee", "猪喝咖啡", "猪喝咖啡"),
        new("elephant", "猪装象", "大象……"),
        new("stack", "在群友身上睡觉", "在群友身上睡觉"),
        new("burger", "汉堡猪", "汉堡猪"),
        new("code", "猪写代码", "猪写代码"),
        new("token", "白吃 token 的猪", "你这白吃token的猪！"),
        new("cry", "哭哭猪", "哭哭猪"),
        new("angry", "生气猪", "哼！"),
        new("peek", "?!猪猪!?", "?!猪猪!?"),
        new("flat", "摊平猪", "摊平了……"),
        new("dead", "死猪", "（死了）"),
    };

    // 官方配色
    static readonly Brush Skin = Hex("#FFD2B1"), Ink = Hex("#2D2D2B"), Snout = Hex("#FF7D86");

    static Brush Hex(string c) { var b = (Brush)new BrushConverter().ConvertFromString(c)!; b.Freeze(); return b; }

    // ---------- 应用 / 清除 ----------
    // 当前小猪是否朝右（Back/Props 层随之镜像）
    static bool _mirrored;

    public static void Apply(PetWindow w, string id)
    {
        Clear(w);
        _mirrored = w.Dir > 0;
        switch (id)
        {
            case "stand": AngryBrows(w.Props); Sign(w.Front); break;
            case "benzene": Benzene(w.Back); break;
            case "chopsticks": Chopsticks(w.Props); break;
            case "python": AngryBrows(w.Props); Drool(w.Props); Logo(w.Props, "python", -120, 250, 150); break;
            // 翻过来后头在另一侧：蛇放在远离头的一边，免得挡住 ×× 眼
            case "bite": XEyes(w.Props, GraySkin); Logo(w.Front, "python", w.Dir > 0 ? 360 : -20, 110, 150, mirrorable: false); break;
            case "crab": Logo(w.Props, "ferris", -150, 380, 210); break;
            case "coffee": Logo(w.Props, "java", -110, 250, 120); break;
            case "elephant": Scallion(w.Props); ThoughtElephant(w.Props); break;
            case "stack": XEyes(w.Props); PigStack(w); break;
            case "burger": Burger(w.Props); break;
            case "code": SpiralEyes(w.Props); Laptop(w.Behind, w.Dir > 0); break;
            case "token": ContentEyes(w.Props); Bowl(w.Front, w.Dir > 0 ? 380 : -60); break;
            case "cry": Tears(w.Props); break;
            case "dead": XEyes(w.Props, GraySkin); break;
            case "angry": AngryEyes(w.Props); AngerMark(w.Props); break;
        }
    }

    public static void Clear(PetWindow w)
    {
        w.Back.Children.Clear(); w.Props.Children.Clear(); w.Front.Children.Clear(); w.Stack.Children.Clear();
        w.Behind.Children.Clear();
        w.OverrideFrame = null;
    }

    // ---------- 工具 ----------
    static T At<T>(Canvas c, T e, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e); return e;
    }

    static Path P(Canvas c, string data, Brush? fill, Brush? stroke = null, double th = 0)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data), Fill = fill, Stroke = stroke, StrokeThickness = th,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(p);
        return p;
    }

    static Canvas Group(Canvas parent, double x, double y, double scale = 1, double angle = 0)
    {
        var g = new Canvas();
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(scale, scale));
        tg.Children.Add(new RotateTransform(angle));
        g.RenderTransform = tg;
        return At(parent, g, x, y);
    }

    // ---------- 表情 ----------
    // 皮肤 #FFD2B1 经 ToGray 处理后的灰度，蛇咬猪时用它遮眼睛
    public static readonly Brush GraySkin = Hex("#D7D7D7");

    static void CoverEyes(Canvas c, Brush? skin = null)
    {
        P(c, "M90,240 m-24,0 a24,26 0 1,0 48,0 a24,26 0 1,0 -48,0", skin ?? Skin);
        P(c, "M210,265 m-24,0 a24,26 0 1,0 48,0 a24,26 0 1,0 -48,0", skin ?? Skin);
    }

    public static void ClosedEyes(Canvas c)
    {
        CoverEyes(c);
        P(c, "M72,236 Q90,256 108,236", null, Ink, 7);
        P(c, "M192,261 Q210,281 228,261", null, Ink, 7);
    }

    public static void XEyes(Canvas c, Brush? skin = null)
    {
        CoverEyes(c, skin);
        P(c, "M74,224 L106,256 M106,224 L74,256", null, Ink, 8);
        P(c, "M194,249 L226,281 M226,249 L194,281", null, Ink, 8);
    }

    static void AngryBrows(Canvas c)
    {
        // 内侧压低：左眼眉向右下，右眼眉向左下
        P(c, "M68,198 L110,214", null, Ink, 8);
        P(c, "M188,232 L230,220", null, Ink, 8);
    }

    static void Drool(Canvas c) =>
        P(c, "M150,360 C142,374 140,384 150,388 C160,384 158,374 150,360 Z", Hex("#6CB4F0"));

    // ---------- 道具 ----------
    /// <summary>站街：三块叠放的白木板挡在身前。</summary>
    static void Sign(Canvas c)
    {
        var board = Hex("#FAFAF7");
        var edge = Hex("#555555");
        for (int i = 0; i < 3; i++)
        {
            double y = 268 + i * 62;
            var r = new Border
            {
                Width = 360, Height = 68, Background = board, BorderBrush = edge, BorderThickness = new Thickness(4),
                CornerRadius = new CornerRadius(14, 14, 4, 4),
            };
            At(c, r, 170 - i * 4, y);
            // 木纹
            P(c, $"M{195 - i * 4},{y + 30} L{500 - i * 4},{y + 26} M{210 - i * 4},{y + 52} L{480 - i * 4},{y + 55}", null, Hex("#DDDDD5"), 3);
        }
        var stack = new StackPanel { Width = 360 };
        foreach (var (t, size) in new[] { ("站 街", 52.0), ("什么都不卖", 36.0), ("纯挡路，挪一次五块", 30.0) })
            stack.Children.Add(new TextBlock
            {
                Text = t, FontSize = size, FontWeight = FontWeights.Bold, Foreground = Brushes.Black,
                FontFamily = new FontFamily("Microsoft YaHei"), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 4),
            });
        At(c, stack, 166, 270);
    }

    /// <summary>苯环：脚下一个透视的六边形，三条内双键。</summary>
    static void Benzene(Canvas c)
    {
        P(c, "M40,452 L150,400 L370,400 L480,452 L370,504 L150,504 Z", Hex("#FFFFFF"), Ink, 9);
        P(c, "M82,450 L160,414 M360,414 L438,450 M335,488 L185,488", null, Ink, 9);
    }

    static void Chopsticks(Canvas c)
    {
        var red = Hex("#C8102E");
        P(c, "M10,480 L120,352", null, red, 13);
        P(c, "M30,492 L140,366", null, red, 13);
        P(c, "M100,378 L120,352 M120,392 L140,366", null, Ink, 14);
    }




    /// <summary>两根大葱插在鼻孔里。</summary>
    static void Scallion(Canvas c)
    {
        var white = Hex("#F2F5E6");
        var pale = Hex("#CFE6A0");
        var green = Hex("#4CA42C");
        var dark = Hex("#2E7D1F");
        foreach (var (nx, ny) in new[] { (93.0, 308.0), (128.0, 315.0) })
        {
            P(c, $"M{nx},{ny} L{nx - 95},{ny + 70}", null, white, 20);
            P(c, $"M{nx - 70},{ny + 52} L{nx - 125},{ny + 92}", null, pale, 19);
            P(c, $"M{nx - 118},{ny + 86} Q{nx - 170},{ny + 100} {nx - 205},{ny + 92}", null, green, 10);
            P(c, $"M{nx - 118},{ny + 88} Q{nx - 165},{ny + 120} {nx - 195},{ny + 135}", null, dark, 10);
            P(c, $"M{nx - 120},{ny + 90} Q{nx - 150},{ny + 135} {nx - 160},{ny + 160}", null, green, 10);
        }
    }

    /// <summary>头顶的想象泡泡，里面是一头蓝色大象。</summary>
    static void ThoughtElephant(Canvas c)
    {
        var g = Group(c, -40, -230);
        P(g, "M30,120 C-10,120 -10,60 30,60 C30,10 100,0 120,30 C150,-10 230,0 230,50 C280,50 280,120 240,130 C240,170 170,180 150,150 C120,185 50,175 50,140 C20,150 10,125 30,120 Z", Brushes.White, Ink, 6);
        P(g, "M150,200 m-12,0 a12,12 0 1,0 24,0 a12,12 0 1,0 -24,0", Brushes.White, Ink, 5);
        P(g, "M168,232 m-7,0 a7,7 0 1,0 14,0 a7,7 0 1,0 -14,0", Brushes.White, Ink, 4);
        var blue = Hex("#7B83C9");
        var deep = Hex("#4F5899");
        var e = Group(g, 50, 40);
        P(e, "M40,40 C40,10 140,10 150,40 L150,90 L40,90 Z", blue);                     // 身体
        P(e, "M48,86 L48,110 L66,110 L66,86 M120,86 L120,110 L138,110 L138,86", blue, blue, 2); // 腿
        P(e, "M44,36 m-30,0 a30,30 0 1,0 60,0 a30,30 0 1,0 -60,0", blue);              // 头
        P(e, "M20,52 C6,70 4,92 18,104 C24,108 30,104 26,96 C18,86 22,72 32,62", blue, deep, 3); // 鼻子
        P(e, "M48,22 C70,10 82,40 64,58 C56,62 48,54 50,44", deep);                      // 耳朵
        P(e, "M30,30 m-4,0 a4,4 0 1,0 8,0 a4,4 0 1,0 -8,0", Ink);
        P(e, "M150,46 C164,42 166,60 176,54", null, deep, 4);                           // 尾巴
        Logo(e, "php", 62, 30, 80);                                                     // 官方 PHP logo
    }

    // ---------- 官方 logo ----------
    static readonly Dictionary<string, BitmapImage> LogoCache = new();

    /// <summary>官方 logo 图片（assets/logos/*.png，由官网 SVG 渲染）：按宽度缩放放到 (x,y)。</summary>
    static void Logo(Canvas c, string name, double x, double y, double width, bool mirrorable = true)
    {
        if (!LogoCache.TryGetValue(name, out var bmp))
        {
            bmp = new BitmapImage(new Uri($"pack://application:,,,/assets/logos/{name}.png"));
            bmp.Freeze();
            LogoCache[name] = bmp;
        }
        var img = new Image { Source = bmp, Width = width, RenderTransformOrigin = new Point(0.5, 0.5) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        // 官方标志不能镜像：放在随朝向翻转的层里时再翻一次抵消
        if (mirrorable && _mirrored) img.RenderTransform = new ScaleTransform(-1, 1);
        At(c, img, x, y);
    }

    // ---------- 新形态 ----------
    /// <summary>汉堡猪：顶上一片面包，身前芝士、肉饼、生菜、底层面包。</summary>
    static void Burger(Canvas c)
    {
        var bun = Hex("#F4A85C"); var bunLight = Hex("#FCD9A0");
        // 顶层面包
        P(c, "M30,150 C30,40 482,40 482,150 C482,170 30,170 30,150 Z", bun);
        P(c, "M38,148 C120,175 392,175 474,148 C474,168 38,168 38,148 Z", bunLight);
        // 底层面包（压住腿）
        P(c, "M40,455 L472,455 C478,505 34,505 40,455 Z", bun);
        P(c, "M40,455 L472,455 C472,468 40,468 40,455 Z", bunLight);
        // 生菜：波浪边
        var pts = new List<string>();
        for (int i = 0; i <= 18; i++) pts.Add($"{30 + i * 25.3:0},{450 + (i % 2 == 0 ? 0 : 14)}");
        pts.Reverse();
        P(c, "M30,420 L482,420 L" + string.Join(" L", pts) + " Z", Hex("#8CC63F"));
        // 肉饼
        P(c, "M50,395 C50,375 462,375 462,395 L462,425 C462,445 50,445 50,425 Z", Hex("#E5677B"));
        foreach (var (dx, dy) in new[] { (110, 405), (190, 418), (300, 408), (390, 420) })
            P(c, $"M{dx},{dy} m-10,0 a10,5 0 1,0 20,0 a10,5 0 1,0 -20,0", Hex("#D14E63"));
        // 芝士：一片带尖角垂下来
        P(c, "M28,368 L484,360 L470,392 L300,400 L262,440 L230,400 L40,396 Z", Hex("#FFD23F"));
    }

    /// <summary>蚊香眼：遮住原眼，画白底黑色螺旋。</summary>
    static void SpiralEyes(Canvas c)
    {
        CoverEyes(c);
        foreach (var (ex, ey) in new[] { (90.0, 240.0), (210.0, 265.0) })
        {
            P(c, $"M{ex},{ey} m-26,0 a26,26 0 1,0 52,0 a26,26 0 1,0 -52,0", Brushes.White, Ink, 3);
            var sp = new System.Text.StringBuilder($"M{ex},{ey}");
            for (int i = 1; i <= 60; i++)
            {
                double a = i * 0.32, r = i * 0.36;
                sp.Append($" L{ex + Math.Cos(a) * r:0.#},{ey + Math.Sin(a) * r:0.#}");
            }
            P(c, sp.ToString(), null, Ink, 3.5);
        }
    }

    /// <summary>身后的笔记本电脑，屏幕上是表情包里那段错误百出的代码。</summary>
    static void Laptop(Canvas c, bool pigFacesRight)
    {
        // 放在小猪背后、脸那一侧的斜上方，像表情包里那样露出屏幕
        double x = pigFacesRight ? 300 : -235;
        var g = Group(c, x, -50, 0.72);
        P(g, "M0,0 L480,0 C492,0 496,6 496,16 L496,300 L-16,300 L-16,16 C-16,6 -12,0 0,0 Z", Hex("#2B2B2B")); // 屏幕外框
        P(g, "M-60,300 L556,300 L530,336 L-34,336 Z", Hex("#C9CDD2"));               // 键盘底座
        P(g, "M-60,300 L556,300 L552,308 L-56,308 Z", Hex("#A9AEB4"));
        At(g, new Border { Width = 476, Height = 276, Background = Brushes.White }, 4, 12);
        string[] code = { "#include 《stdio。h》", "int man()", "{", "    paintf(“ Hell world!” );", "    remake0;", "}" };
        var sp = new StackPanel { Width = 476 };
        for (int i = 0; i < code.Length; i++)
            sp.Children.Add(new TextBlock
            {
                Text = $" {i + 1} {code[i]}", FontFamily = new FontFamily("Consolas, Microsoft YaHei"), FontSize = 28,
                Foreground = Brushes.Black, Background = i == 5 ? Hex("#C8F4F8") : Brushes.Transparent,
            });
        At(g, sp, 4, 16);
        // 底部一排红色报错
        for (int i = 0; i < 4; i++) P(g, $"M20,{236 + i * 12} L300,{236 + i * 12}", null, Hex("#E53935"), 4);
    }

    /// <summary>心满意足的闭眼（四脚朝天时显示为 ︶）。</summary>
    static void ContentEyes(Canvas c)
    {
        CoverEyes(c);
        P(c, "M72,248 Q90,226 108,248", null, Ink, 7);
        P(c, "M192,273 Q210,251 228,273", null, Ink, 7);
    }

    /// <summary>空饭碗。</summary>
    static void Bowl(Canvas c, double x)
    {
        var g = Group(c, x, 400);
        P(g, "M0,20 C0,90 180,90 180,20 Z", Hex("#F7F7F7"), Ink, 5);
        P(g, "M0,20 C0,0 180,0 180,20 C180,36 0,36 0,20 Z", Hex("#FFFFFF"), Ink, 5);
        P(g, "M40,82 L140,82 L130,100 L50,100 Z", Hex("#7EC8C8"), Ink, 4);
    }

    /// <summary>生气的半月眼：上沿是向内压低的斜线，下半是圆弧。</summary>
    public static void AngryEyes(Canvas c)
    {
        CoverEyes(c);
        // 左眼（靠前）内侧在右边，右眼内侧在左边；内侧压低
        P(c, "M66,228 L114,244 A24,22 0 0,1 66,236 Z", Ink);
        P(c, "M186,262 L234,250 A24,22 0 0,1 186,270 Z", Ink);
    }

    /// <summary>头顶的红色青筋（💢）。</summary>
    public static void AngerMark(Canvas c)
    {
        var red = Hex("#E53935");
        var g = Group(c, 120, 50);
        P(g, "M0,22 Q14,18 18,4 M30,4 Q34,18 48,22 M48,34 Q34,38 30,52 M18,52 Q14,38 0,34", null, red, 9);
    }

    /// <summary>两道眼泪从眼睛流到下巴。</summary>
    static void Tears(Canvas c)
    {
        var tear = Hex("#9CD3F5");
        P(c, "M96,258 C98,300 94,330 100,372", null, tear, 20);
        P(c, "M206,283 C204,320 208,350 202,390", null, tear, 20);
    }

    /// <summary>叠罗汉：几只闭眼的群友压在本体上面睡觉。</summary>
    static void PigStack(PetWindow w)
    {
        if (w.RestFrame == null) return;
        // 群友只露出身体上半截（像表情包里趴着的一坨），下半截被下面那只挡住；
        // 间距让每只的眼睛和鼻子都露出来
        double size = w.PetSize, step = size * 0.28;
        double left = (w.Width - size) / 2, bottom = w.Height - size;
        for (int i = 1; i <= 4; i++)
        {
            var g = new Grid
            {
                Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5),
                Clip = new RectangleGeometry(new Rect(0, 0, size, size * 0.72), size * 0.2, size * 0.2),
            };
            g.RenderTransform = new ScaleTransform(w.Dir > 0 ? -1 : 1, 1);
            g.Children.Add(new Image { Source = w.RestFrame });
            var face = new Canvas { Width = 512, Height = 512 };
            ClosedEyes(face);
            g.Children.Add(new Viewbox { Child = face });
            Canvas.SetLeft(g, left + (i % 2 == 0 ? 0 : size * 0.02));
            Canvas.SetTop(g, bottom - step * i);
            w.Stack.Children.Add(g);
        }
    }

    // ---------- 灰色版本（蛇咬猪） ----------
    public static BitmapSource ToGray(BitmapSource src)
    {
        int wpx = src.PixelWidth, hpx = src.PixelHeight, stride = wpx * 4;
        var px = new byte[stride * hpx];
        new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0).CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            // 预乘 alpha 下直接按亮度混合，再整体提亮一点，接近表情包里的浅灰
            byte l = (byte)Math.Min(px[i + 3], (px[i] * 0.114 + px[i + 1] * 0.587 + px[i + 2] * 0.299) * 0.92 + px[i + 3] * 0.05);
            px[i] = px[i + 1] = px[i + 2] = l;
        }
        var bmp = BitmapSource.Create(wpx, hpx, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }
}
