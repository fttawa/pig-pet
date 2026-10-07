using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PigPet;

public class ActionSetting
{
    public bool Enabled { get; set; } = true;
    public int Weight { get; set; } = 1;
}

public class Config
{
    public double Size { get; set; } = 128;
    public double Opacity { get; set; } = 1;
    public double Speed { get; set; } = 1;
    public double IntervalMin { get; set; } = 15;
    public double IntervalMax { get; set; } = 40;
    public double SleepAfter { get; set; } = 180;
    public double Gravity { get; set; } = 2600;    // 重力 DIP/s²
    public double Bounce { get; set; } = 0.45;     // 反弹系数
    public double Friction { get; set; } = 2.5;    // 地面摩擦
    public double ThrowStrength { get; set; } = 1; // 甩出力度倍率
    public int MaxPets { get; set; } = 6;          // 猪群上限
    public bool WindowCollision { get; set; } = true; // 能站在其他程序的窗口上
    public bool VisualLedges { get; set; } = true;    // 全屏 / 最大化窗口里看画面找能站的边缘
    public bool ElementLedges { get; set; } = true;   // 优先用 UI Automation 读取元素位置
    public double LedgeThreshold { get; set; } = 40;  // 画面边缘的亮度差阈值，越小越容易站
    public int FpsLimit { get; set; }              // 动画帧率上限，0 = 自动（跟随屏幕，最高 60）
    public bool HpEnabled { get; set; } = true;    // 隐藏血条：摔太狠会死
    public double MaxHp { get; set; } = 100;       // 耐摔程度
    public double HpRegen { get; set; } = 3;       // 每秒回血
    public bool ShowHpBar { get; set; }            // 受伤时短暂显示血条
    public bool HungerEnabled { get; set; } = true;  // 会饿、会自己找吃的
    public double HungerRate { get; set; } = 4;      // 每分钟掉多少饱食度（满 100）
    public bool WeightEnabled { get; set; } = true;  // 体重：吃了长肉，越胖越抗揍、越难拎
    public double StartWeight { get; set; } = 30;    // 新猪的体重（kg）
    public bool RoamMode { get; set; }               // 满屏漫游模式：大部分时间沿屏幕四周爬
    public bool BoredClone { get; set; } = true;     // 无聊时自动分裂
    public double BoredAfter { get; set; } = 90;     // 兴奋值持续很低多少秒算无聊
    public int BoredCloneLimit { get; set; } = 1;    // 每次运行最多自动分裂几次，0 = 不限
    public bool WindowPerPig { get; set; } = true;   // 每只猪 / 每份食物一个小窗口（关掉 = 每个显示器一个整屏舞台），重启生效
    public bool FoodBar { get; set; } = true;        // 显示食物悬浮栏
    public double? FoodBarX { get; set; }            // 悬浮栏位置（DIP），null = 默认右上
    public double? FoodBarY { get; set; }
    public double HeavyWeight { get; set; } = 60;    // 超过这个重量（含背上的猪）拎着会手滑
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool AutoStart { get; set; }
    public bool ShowBubbles { get; set; } = true;
    public List<string> Bubbles { get; set; } = new() { "哼哼~", "好困…", "饿了", "摸摸我", "今天也要摸鱼" };

    public Dictionary<string, ActionSetting> Actions { get; set; } = DefaultActions();
    /// <summary>各表情包形态是否参与随机（缺省为开启）。</summary>
    public Dictionary<string, bool> FormsEnabled { get; set; } = new();

    public static readonly string[] ActionNames = { "lazy", "idle", "walk", "roll", "jump", "spin", "shake", "sleep", "form", "clone", "visit", "pile", "chase", "merge", "duel", "roam" };
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["lazy"] = "慵懒（官方动画）", ["idle"] = "呼吸", ["walk"] = "散步", ["roll"] = "翻滚",
        ["jump"] = "跳跃", ["spin"] = "转圈", ["shake"] = "抖动", ["sleep"] = "睡觉", ["form"] = "表情包形态",
        ["clone"] = "自我复制", ["visit"] = "串门贴贴", ["pile"] = "叠罗汉", ["chase"] = "追逐", ["merge"] = "合体（克隆回收）", ["duel"] = "两猪对峙", ["roam"] = "满屏走（爬墙、倒挂天花板）",
    };

    static Dictionary<string, ActionSetting> DefaultActions() => new()
    {
        ["lazy"] = new() { Weight = 8 }, ["idle"] = new() { Weight = 2 }, ["walk"] = new() { Weight = 3 },
        ["roll"] = new() { Weight = 1 }, ["jump"] = new() { Weight = 1 }, ["spin"] = new() { Weight = 1 },
        ["shake"] = new() { Weight = 1 }, ["sleep"] = new() { Weight = 1 }, ["form"] = new() { Weight = 3 },
        ["clone"] = new() { Weight = 1 }, ["visit"] = new() { Weight = 2 }, ["pile"] = new() { Weight = 1 },
        ["chase"] = new() { Weight = 1 }, ["merge"] = new() { Weight = 1 }, ["duel"] = new() { Weight = 1 }, ["roam"] = new() { Weight = 1 },
    };

    // ---------- 持久化 ----------
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PigPet");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Config Current { get; private set; } = Load();
    public static event Action<Config>? Changed;

    static Config Load()
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
            // 补齐新增的动作项
            foreach (var kv in DefaultActions()) c.Actions.TryAdd(kv.Key, kv.Value);
            return c;
        }
        catch { return new Config(); }
    }

    public static void Save(Config c)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(c, Json));
        Current = c;
        ApplyAutoStart(c.AutoStart);
        Changed?.Invoke(c);
    }

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this))!;

    static void ApplyAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (on) key.SetValue("PigPet", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("PigPet", false);
        }
        catch { /* 注册表不可写时忽略 */ }
    }
}
