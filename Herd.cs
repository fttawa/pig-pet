using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace PigPet;

/// <summary>
/// 猪群：同一进程里管理所有小猪窗口。
/// 再次运行 exe（多开）时，新进程通过命名管道通知已运行的实例“再来一只”，然后自己退出。
/// </summary>
public static class Herd
{
    public static readonly List<PetWindow> Pets = new();
    public static event Action? Changed;
    public static event Action? SettingsRequested;

    public static int Count => Pets.Count;

    /// <summary>生成一只小猪。pos 为窗口左上角（DIP），null 表示默认位置。</summary>
    public static PetWindow Spawn(Point? pos = null, Vector velocity = default, bool isClone = false)
    {
        var p = new PetWindow(pos, velocity, isClone);
        p.SettingsRequested += () => SettingsRequested?.Invoke();
        p.Closed += (_, _) => { Pets.Remove(p); Changed?.Invoke(); };
        Pets.Add(p);
        p.Show();
        Changed?.Invoke();
        return p;
    }

    /// <summary>多开时新来的小猪：从屏幕顶部随机位置掉下来。</summary>
    public static void SpawnFromSky()
    {
        if (Count >= Config.Current.MaxPets) { Pets.FirstOrDefault()?.Say("猪满啦！"); return; }
        var wa = SystemParameters.WorkArea;
        double size = Config.Current.Size * 2;
        var x = wa.Left + Random.Shared.NextDouble() * Math.Max(0, wa.Width - size);
        Spawn(new Point(x, wa.Top), new Vector(Random.Shared.Next(-300, 300), 0));
    }

    /// <summary>别的小猪（排除自己）。</summary>
    public static IEnumerable<PetWindow> Others(PetWindow self) => Pets.Where(p => p != self && p.IsLoaded);

    public static PetWindow? Nearest(PetWindow self, Func<PetWindow, bool>? filter = null) =>
        Others(self).Where(p => filter == null || filter(p))
            .OrderBy(p => (p.BodyCenter - self.BodyCenter).Length).FirstOrDefault();

    public static void RemoveClones()
    {
        foreach (var p in Pets.Where(p => p.IsClone).ToList()) p.Brain.Play("merge-out", true);
    }

    // ---------- 多开：命名管道 ----------
    const string PipeName = "PigPet.Herd";

    /// <summary>已有实例在运行时，请它再生成一只；成功返回 true。</summary>
    public static bool AskRunningInstance(string command = "spawn")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var w = new StreamWriter(client);
            w.WriteLine(command);
            return true;
        }
        catch { return false; }
    }

    public static void Listen(Application app)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var cmd = await r.ReadLineAsync() ?? "";
                    app.Dispatcher.Invoke(() =>
                    {
                        if (cmd == "spawn") SpawnFromSky();
                        // play <动作>：让最后一只执行，例如 PigPet.exe --play clone
                        // play pause / play resume：全部暂停 / 继续（调试用）
                        else if (cmd is "play pause" or "play resume") foreach (var p in Pets) p.Brain.SetPaused(cmd == "play pause");
                        // play hittest：在第一只猪周围打点，记录哪些点点得中（调试舞台的点击判断）
                        else if (cmd == "play hittest" && Pets.FirstOrDefault() is { } pig)
                        {
                            var c = pig.BodyCenter;
                            foreach (var (dx, dy, what) in new[] { (0.0, 0.0, "身体中心"), (0.0, -0.75, "头顶上方"), (0.9, 0.0, "右侧外面"), (-0.25, 0.1, "脸"), (0.0, 0.47, "脚下") })
                                Perf.Log($"命中测试 {what} ({c.X + dx * pig.PetSize:0},{c.Y + dy * pig.PetSize:0}) → {Stage.DebugHit(new Point(c.X + dx * pig.PetSize, c.Y + dy * pig.PetSize))}");
                        }
                        else if (cmd.StartsWith("play ")) Pets.LastOrDefault()?.Brain.Play(cmd[5..], true);
                    });
                }
                catch { await Task.Delay(500); }
            }
        });
    }
}
