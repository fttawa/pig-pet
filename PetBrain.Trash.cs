using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualBasic.FileIO;

namespace PigPet;

/// <summary>
/// 回收站：把文件拖给小猪，二次确认后猪猪把它“吃掉”——其实是放进系统回收站，还能恢复。
/// </summary>
public partial class PetBrain
{
    int _ateFiles;

    /// <summary>文件拖到身上时的反应。</summary>
    public void FileHover() => w.Say(new[] { "这是给我吃的吗？", "闻起来像文件", "啊——" }[R.Next(3)], 1200);

    /// <summary>文件在猪身上松手：确认后放进回收站，然后啃掉。</summary>
    public void EatFiles(string[] paths)
    {
        paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return;
        string what = paths.Length == 1 ? $"“{Path.GetFileName(paths[0].TrimEnd('\\'))}”" : $"这 {paths.Length} 个文件";
        var r = MessageBox.Show($"真的要让猪猪吃掉{what}吗？\n\n（会放进回收站，后悔了还能从回收站找回来）",
            "小猪桌宠", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
        if (r != MessageBoxResult.Yes) { w.Say("那我不吃了", 1200); return; }

        int ok = 0;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p)) FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                ok++;
            }
            catch (Exception e) { Log($"吃文件失败 {p}：{e.Message}"); }
        }
        Log($"吃掉 {ok}/{paths.Length} 个文件");
        if (ok == 0) { w.Say("咬不动……", 1500); return; }
        _ateFiles = ok;
        Play("chomp", true);
    }

    /// <summary>啃文件的动画：低头啃几口，掉碎屑。</summary>
    async Task Chomp(CancellationToken ct)
    {
        int bites = Math.Clamp(2 + _ateFiles, 3, 8);
        w.Anim.RenderTransformOrigin = new Point(0.5, 0.85);
        for (int i = 0; i < bites; i++)
        {
            bool crumbed = false;
            await Animate(0.4 / K, (t, _) =>
            {
                double p = t * K / 0.4, nod = Math.Sin(Math.PI * Math.Min(1, p));
                w.ARotate.Angle = -12 * nod;
                w.AScale.ScaleX = 1 + 0.04 * nod; w.AScale.ScaleY = 1 - 0.04 * nod;
                if (!crumbed && p > 0.5)
                {
                    crumbed = true;
                    w.Particle("▪", Crumb, w.Dir > 0 ? 0.85 : 0.15);
                    w.Particle("·", Crumb, w.Dir > 0 ? 0.95 : 0.05);
                }
            }, ct);
            w.ARotate.Angle = 0;
            await Task.Delay(80, ct);
        }
        w.AScale.ScaleX = w.AScale.ScaleY = 1;
        w.Say(_ateFiles == 1 ? new[] { "嗝~ 有点硬", "纸片味的", "吃掉啦" }[R.Next(3)] : $"一口气吃了 {_ateFiles} 个！嗝~", 1600);
        Satiety = Math.Min(120, Satiety + 3 * _ateFiles);
        AddMood(3, 10);
        await Task.Delay(1200, ct);
    }
}
