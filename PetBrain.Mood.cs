using System;
using System.Linq;

namespace PigPet;

/// <summary>
/// 心情：快乐值和兴奋值（都是 0~100）。
/// 快乐值：被摸、吃东西、和别的猪贴贴会涨；饿、受伤、血少会掉。太低会哭，很高时偶尔冒爱心。
/// 兴奋值：被甩、摔、翻滚、追逐、对峙会涨，随时间慢慢消退；兴奋时更爱选活跃的动作。
/// 兴奋值长时间很低就觉得无聊：自己分裂一只陪自己玩（达到猪群上限就去找别的猪玩）。
/// </summary>
public partial class PetBrain
{
    public double Happiness { get; private set; } = 70;
    public double Excitement { get; private set; } = 30;
    double _boredFor, _nextBoredTalk;

    bool Bored => Excitement < 15;

    /// <summary>调整心情（正数涨、负数掉）。</summary>
    public void AddMood(double happy, double excite)
    {
        Happiness = Math.Clamp(Happiness + happy, 0, 100);
        Excitement = Math.Clamp(Excitement + excite, 0, 100);
        if (excite > 5) _boredFor = 0;
    }

    /// <summary>刚分裂出来：两只都很兴奋。</summary>
    public void SetMood(double happy, double excite) { Happiness = happy; Excitement = excite; _boredFor = 0; }

    /// <summary>每秒一次。</summary>
    void MoodTick()
    {
        if (_paused) return;
        // 兴奋慢慢消退（越兴奋退得越快）
        Excitement = Math.Max(0, Excitement - (0.6 + Excitement * 0.012));
        // 快乐：饿了、血少了会掉；吃饱又没受伤就慢慢回到 60 左右
        double dh = 0;
        if (Starving) dh -= 0.4; else if (Hungry) dh -= 0.15;
        if (C.HpEnabled && Hp < MaxHp * 0.3) dh -= 0.2;
        if (dh == 0 && Happiness < 60) dh = 0.05;
        Happiness = Math.Clamp(Happiness + dh, 0, 100);

        // 无聊计时：闲着、兴奋值又低
        bool idle = !_dragging && !_flying && _current is "" or "lazy" or "idle" or "walk" or "sleep";
        _boredFor = Bored && idle ? _boredFor + 1 : Math.Max(0, _boredFor - 2);
        if (_boredFor > C.BoredAfter * 0.5 && Now >= _nextBoredTalk && idle && _current != "sleep")
        {
            _nextBoredTalk = Now + Rand(25, 45);
            w.Say(new[] { "好无聊……", "没意思", "有人陪我玩吗", "发呆中……" }[R.Next(4)], 2000);
        }
        if (_boredFor >= C.BoredAfter && idle && Interruptible && IsGrounded && !IsRiding && !HasRider) BoredomStrikes();
    }

    /// <summary>无聊到了极点：分裂一只陪自己玩；分不了就去找别的猪玩。</summary>
    void BoredomStrikes()
    {
        _boredFor = 0;
        if (C.BoredClone && Herd.Count < C.MaxPets && (C.BoredCloneLimit <= 0 || _boredClones < C.BoredCloneLimit))
        {
            _boredClones++;
            Log($"无聊到分裂（兴奋 {Excitement:0}，快乐 {Happiness:0}）");
            w.Say("好无聊……分个身陪我玩", 1800);
            _boredClone = true;
            Play("clone");
            return;
        }
        var play = new[] { "chase", "visit", "duel" }.Where(a => CoopAllowed(a)).ToArray();
        if (play.Length > 0)
        {
            w.Say("无聊！找人玩去", 1500);
            Play(play[R.Next(play.Length)]);
        }
        else Play(new[] { "roll", "jump", "spin" }[R.Next(3)]);
    }

    bool _boredClone;
    /// <summary>这次运行里所有猪一共因为无聊自动分裂了几次。</summary>
    static int _boredClones;

    /// <summary>做某个动作时心情的变化。</summary>
    void MoodForAction(string name)
    {
        switch (name)
        {
            case "roll" or "jump" or "spin": AddMood(1, 8); break;
            case "chase" or "follow" or "duel" or "duel-b": AddMood(0, 20); break;
            case "visit" or "greet": AddMood(8, 10); break;
            case "pile" or "beneath": AddMood(4, 6); break;
            case "clone": AddMood(3, 35); break;
            case "sleep": AddMood(2, -10); break;
        }
    }

    /// <summary>按心情调整随机动作：兴奋时更爱动，不开心时可能哭一会儿。</summary>
    string? MoodChoice()
    {
        if (Happiness < 25 && R.NextDouble() < 0.35) return "form:cry";
        if (Excitement > 60 && R.NextDouble() < 0.5)
        {
            var lively = new[] { "roll", "jump", "spin", "chase", "duel" }
                .Where(a => C.Actions.TryGetValue(a, out var s) && s.Enabled && CoopAllowed(a) && StackAllowed(a)).ToArray();
            if (lively.Length > 0) return lively[R.Next(lively.Length)];
        }
        if (Happiness > 85 && R.NextDouble() < 0.3) { w.Particle("♥", Pink, 0.4); w.Particle("♥", Pink, 0.6); }
        return null;
    }
}
