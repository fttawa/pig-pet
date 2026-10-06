using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PigPet;

/// <summary>
/// 食物悬浮栏：置顶的小工具条，点一下就在它下面掉一份对应的食物。
/// 按住左边的把手拖动位置（会记住），点 × 隐藏（托盘菜单里可以再打开）。
/// </summary>
public class FoodBar : Window
{
    public FoodBar()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "PigPetFoodBar";

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var grip = new TextBlock
        {
            Text = "⠿", FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x8A, 0x9A)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0),
            Cursor = Cursors.SizeAll, ToolTip = "拖动悬浮栏",
        };
        grip.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } SavePos(); };
        row.Children.Add(grip);

        foreach (var k in FoodKind.All)
        {
            var b = new Button
            {
                Content = new Image { Source = FoodWindow.ImageOf(k), Width = 30, Height = 30 },
                ToolTip = $"{k.Name}（饱食 +{k.Satiety}，回血 +{k.Heal}）",
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(3), Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Focusable = false,
            };
            RenderOptions.SetBitmapScalingMode((Image)b.Content, BitmapScalingMode.HighQuality);
            b.Click += (_, _) => DropBelow(b, k);
            row.Children.Add(b);
        }

        var close = new TextBlock
        {
            Text = "×", FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0x80, 0x88)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0),
            Cursor = Cursors.Hand, ToolTip = "隐藏（托盘菜单可重新打开）",
        };
        close.MouseLeftButtonUp += (_, _) => { var c = Config.Current.Clone(); c.FoodBar = false; Config.Save(c); };
        row.Children.Add(close);

        Content = new Border
        {
            Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xA3, 0xB5)), BorderThickness = new Thickness(1.5),
        };

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000); // 工具窗口、不抢焦点
        };
        Loaded += (_, _) => PlaceInitially();
    }

    void PlaceInitially()
    {
        var wa = SystemParameters.WorkArea;
        var c = Config.Current;
        double x = c.FoodBarX ?? wa.Right - ActualWidth - 40;
        double y = c.FoodBarY ?? wa.Top + 40;
        // 换了分辨率 / 显示器后别跑到屏幕外
        Left = Math.Clamp(x, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth);
        Top = Math.Clamp(y, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight);
    }

    void SavePos()
    {
        var c = Config.Current.Clone();
        c.FoodBarX = Left; c.FoodBarY = Top;
        Config.Save(c);
    }

    /// <summary>从按钮正下方掉一份食物。</summary>
    void DropBelow(Button b, FoodKind k)
    {
        var p = b.TranslatePoint(new Point(b.ActualWidth / 2, 0), this);
        FoodWorld.Drop(k, Left + p.X, y: Top + ActualHeight + 2);
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
