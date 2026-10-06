using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PigPet;

/// <summary>“一键分裂”的自定义数量输入框。</summary>
public static class CountDialog
{
    public static int? Ask()
    {
        var box = new TextBox { Text = "8", Width = 80, Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var ok = new Button { Content = "分裂！", Width = 70, IsDefault = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        row.Children.Add(new TextBlock { Text = $"分裂出几只（1~{PetBrain.HardMaxPets - Herd.Count}）：", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        row.Children.Add(ok);

        var win = new Window
        {
            Title = "一键分裂", Content = row, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei"), Topmost = true,
        };
        int? result = null;
        ok.Click += (_, _) =>
        {
            if (int.TryParse(box.Text, out var n) && n > 0) { result = n; win.Close(); }
            else box.SelectAll();
        };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        win.ShowDialog();
        return result;
    }
}
