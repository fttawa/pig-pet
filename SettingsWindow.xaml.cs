using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PigPet;

public partial class SettingsWindow : Window
{
    readonly Dictionary<string, (CheckBox on, TextBox weight)> _acts = new();

    public SettingsWindow()
    {
        InitializeComponent();
        foreach (var name in Config.ActionNames)
        {
            int row = ActionsGrid.RowDefinitions.Count;
            ActionsGrid.RowDefinitions.Add(new RowDefinition());
            var cb = new CheckBox { Content = Config.Labels[name], Margin = new Thickness(0, 3, 0, 3) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var tb = new TextBox { Width = 40 };
            panel.Children.Add(new TextBlock { Text = "权重 ", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(tb);
            Grid.SetRow(cb, row); Grid.SetRow(panel, row); Grid.SetColumn(panel, 1);
            ActionsGrid.Children.Add(cb); ActionsGrid.Children.Add(panel);
            _acts[name] = (cb, tb);
        }

        foreach (var f in Forms.All)
            FormsPanel.Children.Add(new CheckBox { Content = f.Label, Tag = f.Id, Width = 130, Margin = new Thickness(0, 3, 0, 3) });

        Render(Config.Current);
        SaveBtn.Click += (_, _) => { Config.Save(Collect()); Render(Config.Current); Flash("已保存，立即生效 ✓"); };
        ResetBtn.Click += (_, _) => { Render(new Config()); Flash("已恢复默认，点保存生效"); };
    }

    void Render(Config c)
    {
        SizeS.Value = c.Size; OpacityS.Value = c.Opacity; SpeedS.Value = c.Speed;
        IntMin.Text = c.IntervalMin.ToString(); IntMax.Text = c.IntervalMax.ToString();
        SleepAfter.Text = c.SleepAfter.ToString();
        MaxPetsT.Text = c.MaxPets.ToString();
        FpsT.Text = c.FpsLimit.ToString();
        FpsInfo.Text = $"屏幕刷新率 {Perf.MonitorHz}Hz，当前动画帧率 {Perf.EffectiveFps}，硬件加速级别 {System.Windows.Media.RenderCapability.Tier >> 16}（2 = 完整加速）";
        HpC.IsChecked = c.HpEnabled; MaxHpS.Value = c.MaxHp; ShowHpC.IsChecked = c.ShowHpBar;
        HungerC.IsChecked = c.HungerEnabled; HungerS.Value = c.HungerRate; WeightC.IsChecked = c.WeightEnabled;
        StartWeightT.Text = c.StartWeight.ToString(); HeavyT.Text = c.HeavyWeight.ToString();
        GravityS.Value = c.Gravity; BounceS.Value = c.Bounce; FrictionS.Value = c.Friction; ThrowS.Value = c.ThrowStrength;
        TopC.IsChecked = c.AlwaysOnTop; CollideC.IsChecked = c.WindowCollision;
        VisualC.IsChecked = c.VisualLedges; LedgeS.Value = c.LedgeThreshold; ElementC.IsChecked = c.ElementLedges; ThroughC.IsChecked = c.ClickThrough; AutoC.IsChecked = c.AutoStart;
        BubbleC.IsChecked = c.ShowBubbles;
        BubblesT.Text = string.Join(Environment.NewLine, c.Bubbles);
        foreach (CheckBox cb in FormsPanel.Children)
            cb.IsChecked = !c.FormsEnabled.TryGetValue((string)cb.Tag, out var on) || on;
        foreach (var (name, ui) in _acts)
        {
            var a = c.Actions.TryGetValue(name, out var v) ? v : new ActionSetting();
            ui.on.IsChecked = a.Enabled; ui.weight.Text = a.Weight.ToString();
        }
    }

    static double Num(string s, double fallback, double min) => double.TryParse(s, out var v) ? Math.Max(min, v) : fallback;

    Config Collect()
    {
        var c = Config.Current.Clone();
        c.Size = SizeS.Value; c.Opacity = OpacityS.Value; c.Speed = SpeedS.Value;
        c.IntervalMin = Num(IntMin.Text, c.IntervalMin, 1);
        c.IntervalMax = Math.Max(c.IntervalMin, Num(IntMax.Text, c.IntervalMax, 1));
        c.SleepAfter = Num(SleepAfter.Text, c.SleepAfter, 10);
        c.MaxPets = (int)Math.Min(PetBrain.HardMaxPets, Num(MaxPetsT.Text, c.MaxPets, 1));
        c.FpsLimit = (int)Math.Min(360, Num(FpsT.Text, 0, 0));
        c.HpEnabled = HpC.IsChecked == true; c.MaxHp = MaxHpS.Value; c.ShowHpBar = ShowHpC.IsChecked == true;
        c.HungerEnabled = HungerC.IsChecked == true; c.HungerRate = HungerS.Value; c.WeightEnabled = WeightC.IsChecked == true;
        c.StartWeight = Math.Min(200, Num(StartWeightT.Text, c.StartWeight, 10)); c.HeavyWeight = Num(HeavyT.Text, c.HeavyWeight, 10);
        c.Gravity = GravityS.Value; c.Bounce = BounceS.Value; c.Friction = FrictionS.Value; c.ThrowStrength = ThrowS.Value;
        c.AlwaysOnTop = TopC.IsChecked == true; c.WindowCollision = CollideC.IsChecked == true;
        c.VisualLedges = VisualC.IsChecked == true; c.LedgeThreshold = LedgeS.Value; c.ElementLedges = ElementC.IsChecked == true; c.ClickThrough = ThroughC.IsChecked == true;
        c.AutoStart = AutoC.IsChecked == true; c.ShowBubbles = BubbleC.IsChecked == true;
        c.Bubbles = BubblesT.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var (name, ui) in _acts)
            c.Actions[name] = new ActionSetting { Enabled = ui.on.IsChecked == true, Weight = (int)Num(ui.weight.Text, 1, 0) };
        foreach (CheckBox cb in FormsPanel.Children) c.FormsEnabled[(string)cb.Tag] = cb.IsChecked == true;
        return c;
    }

    void Flash(string text)
    {
        Msg.Text = text;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { Msg.Text = ""; t.Stop(); };
        t.Start();
    }
}
