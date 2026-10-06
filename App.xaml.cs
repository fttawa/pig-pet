using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace PigPet;

public partial class App : System.Windows.Application
{
    Mutex? _mutex;
    WinForms.NotifyIcon? _tray;
    SettingsWindow? _settings;
    bool _paused;
    FoodBar? _foodBar;

    /// <summary>按配置显示 / 隐藏食物悬浮栏。</summary>
    void SyncFoodBar()
    {
        if (Config.Current.FoodBar && _foodBar == null)
        {
            _foodBar = new FoodBar();
            _foodBar.Closed += (_, _) => _foodBar = null;
            _foodBar.Show();
        }
        else if (!Config.Current.FoodBar && _foodBar != null) _foodBar.Close();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 兜底：界面线程上的意外异常记日志，不让整个桌宠闪退
        DispatcherUnhandledException += (_, ex) =>
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PigPet");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Exception}{Environment.NewLine}");
            }
            catch { }
            ex.Handled = true;
        };
        _mutex = new Mutex(true, "PigPet.SingleInstance", out bool first);
        if (!first)
        {
            // 多开：让已运行的实例再加一只小猪，自己退出
            int pi = Array.IndexOf(e.Args, "--play");
            Herd.AskRunningInstance(pi >= 0 && pi + 1 < e.Args.Length ? "play " + e.Args[pi + 1] : "spawn");
            Shutdown();
            return;
        }

        // 记录渲染级别（2 = 完整硬件加速），便于排查性能
        var log = Environment.GetEnvironmentVariable("PIGPET_LOG");
        if (log != null)
            System.IO.File.AppendAllText(log, $"渲染级别 Tier={System.Windows.Media.RenderCapability.Tier >> 16} " +
                $"渲染模式={System.Windows.Media.RenderOptions.ProcessRenderMode}" + Environment.NewLine);
        Herd.SettingsRequested += OpenSettings;
        FoodWorld.Spawned += PetBrain.AssignFood;
        FoodWorld.Prewarm();
        var pet = Herd.Spawn();

        // 开发用：dotnet run -- --snapshot <目录>，把每个形态渲染成 PNG 便于检查
        int si = Array.IndexOf(e.Args, "--snapshot");
        if (si >= 0 && si + 1 < e.Args.Length) { _ = pet.Snapshot(e.Args[si + 1]).ContinueWith(_ => Dispatcher.Invoke(Shutdown)); return; }

        Herd.Listen(this);
        _tray = new WinForms.NotifyIcon { Icon = LoadIcon(), Text = "小猪桌宠", Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        BuildMenu();
        SyncFoodBar();
        Config.Changed += _ => { BuildMenu(); SyncFoodBar(); };
        Herd.Changed += () => Dispatcher.BeginInvoke(BuildMenu);
    }

    static Icon LoadIcon()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/assets/pig_1f416.png"))!;
        using var bmp = new Bitmap(res.Stream);
        using var small = new Bitmap(bmp, 32, 32);
        return Icon.FromHandle(small.GetHicon());
    }

    /// <summary>托盘里的动作/形态对所有小猪生效。</summary>
    static void PlayAll(string action)
    {
        foreach (var p in Herd.Pets.ToList()) p.Brain.Play(action, true);
    }

    void BuildMenu()
    {
        if (_tray == null) return;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());

        var acts = new WinForms.ToolStripMenuItem("动作");
        foreach (var a in Config.ActionNames)
            acts.DropDownItems.Add(Config.Labels[a], null, (_, _) => PlayAll(a));
        menu.Items.Add(acts);

        var forms = new WinForms.ToolStripMenuItem("形态");
        foreach (var f in Forms.All)
            forms.DropDownItems.Add(f.Label, null, (_, _) => PlayAll("form:" + f.Id));
        forms.DropDownItems.Add("蛇咬猪", null, (_, _) => PlayAll("form:bite"));
        menu.Items.Add(forms);

        var feed = new WinForms.ToolStripMenuItem("喂食");
        foreach (var k in FoodKind.All)
            feed.DropDownItems.Add($"{k.Name}（饱食 +{k.Satiety}）", null, (_, _) => FoodWorld.Drop(k));
        feed.DropDownItems.Add(new WinForms.ToolStripSeparator());
        feed.DropDownItems.Add("随机投喂", null, (_, _) => FoodWorld.Drop(FoodKind.Random()));
        feed.DropDownItems.Add("每只猪一份", null, (_, _) =>
        {
            foreach (var p in Herd.Pets.ToList()) FoodWorld.Drop(FoodKind.Random(), p.BodyCenter.X + p.Dir * p.PetSize, p);
        });
        var bar = new WinForms.ToolStripMenuItem("食物悬浮栏") { Checked = Config.Current.FoodBar };
        bar.Click += (_, _) => { var c = Config.Current.Clone(); c.FoodBar = !c.FoodBar; Config.Save(c); };
        feed.DropDownItems.Add(bar);
        menu.Items.Add(feed);

        var herd = new WinForms.ToolStripMenuItem($"猪群（{Herd.Count}/{Config.Current.MaxPets}）");
        herd.DropDownItems.Add("再来一只（从天而降）", null, (_, _) => Herd.SpawnFromSky());
        herd.DropDownItems.Add("让一只分裂", null, (_, _) => Herd.Pets.LastOrDefault()?.Brain.Play("clone", true));
        var burst = new WinForms.ToolStripMenuItem("一键分裂");
        foreach (var n in new[] { 2, 5, 10, 20 })
            burst.DropDownItems.Add($"分裂出 {n} 只", null, (_, _) => Burst(n));
        burst.DropDownItems.Add("自定义数量…", null, (_, _) => { if (CountDialog.Ask() is int n) Burst(n); });
        herd.DropDownItems.Add(burst);
        herd.DropDownItems.Add("查看状态（体重 / 饱食 / 血量）", null, (_, _) => PlayAll("status"));
        herd.DropDownItems.Add("收回所有克隆", null, (_, _) => Herd.RemoveClones());
        menu.Items.Add(herd);

        menu.Items.Add(_paused ? "继续" : "暂停", null, (_, _) =>
        {
            _paused = !_paused;
            foreach (var p in Herd.Pets) p.Brain.SetPaused(_paused);
            BuildMenu();
        });

        var through = new WinForms.ToolStripMenuItem("点击穿透") { Checked = Config.Current.ClickThrough };
        through.Click += (_, _) => { var c = Config.Current.Clone(); c.ClickThrough = !c.ClickThrough; Config.Save(c); };
        menu.Items.Add(through);

        menu.Items.Add("回到屏幕右下角", null, (_, _) => Herd.Pets.FirstOrDefault()?.ResetPosition());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Quit());

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>让第一只（本体）一次分裂出 n 只。</summary>
    static void Burst(int n) => Herd.Pets.FirstOrDefault()?.Brain.Play($"burst:{n}", true);

    void OpenSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    void Quit()
    {
        _tray!.Visible = false;
        _tray.Dispose();
        Shutdown();
    }
}
