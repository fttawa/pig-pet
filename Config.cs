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
    public bool HpEnabled { get; set; } = true;    // 隐藏血条：摔太狠会死
    public double MaxHp { get; set; } = 100;       // 耐摔程度
    public double HpRegen { get; set; } = 3;       // 每秒回血
    public bool ShowHpBar { get; set; }            // 受伤时短暂显示血条
    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool AutoStart { get; set; }
    public bool ShowBubbles { get; set; } = true;
    public List<string> Bubbles { get; set; } = new() { "哼哼~", "好困…", "饿了", "摸摸我", "今天也要摸鱼" };

    public Dictionary<string, ActionSetting> Actions { get; set; } = DefaultActions();
    /// <summary>各表情包形态是否参与随机（缺省为开启）。</summary>
    public Dictionary<string, bool> FormsEnabled { get; set; } = new();

    public static readonly string[] ActionNames = { "lazy", "idle", "walk", "roll", "jump", "spin", "shake", "sleep", "form", "clone", "visit", "pile", "chase", "merge", "duel" };
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["lazy"] = "慵懒（官方动画）", ["idle"] = "呼吸", ["walk"] = "散步", ["roll"] = "翻滚",
        ["jump"] = "跳跃", ["spin"] = "转圈", ["shake"] = "抖动", ["sleep"] = "睡觉", ["form"] = "表情包形态",
        ["clone"] = "自我复制", ["visit"] = "串门贴贴", ["pile"] = "叠罗汉", ["chase"] = "追逐", ["merge"] = "合体（克隆回收）", ["duel"] = "两猪对峙",
    };

    static Dictionary<string, ActionSetting> DefaultActions() => new()
    {
        ["lazy"] = new() { Weight = 8 }, ["idle"] = new() { Weight = 2 }, ["walk"] = new() { Weight = 3 },
        ["roll"] = new() { Weight = 1 }, ["jump"] = new() { Weight = 1 }, ["spin"] = new() { Weight = 1 },
        ["shake"] = new() { Weight = 1 }, ["sleep"] = new() { Weight = 1 }, ["form"] = new() { Weight = 3 },
        ["clone"] = new() { Weight = 1 }, ["visit"] = new() { Weight = 2 }, ["pile"] = new() { Weight = 1 },
        ["chase"] = new() { Weight = 1 }, ["merge"] = new() { Weight = 1 }, ["duel"] = new() { Weight = 1 },
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
