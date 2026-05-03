using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 演奏线程。在指定起始时间（秒）开始向 <see cref="INoteOutput"/> 派发"按下 / 抬起"事件，
/// 跳过当前输出不支持的音（由 <see cref="INoteOutput.ShouldPlay"/> 决定）。
/// </summary>
public sealed class Player : IDisposable
{
    public event Action<double>? PlayheadChanged;   // 参数：当前时间（秒）
    public event Action? Finished;
    public event Action<int>? CountdownTick;        // 参数：剩余秒；0 表示结束

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsPlaying { get; private set; }

    public double Speed { get; set; } = 1.0; // 播放倍速
    /// <summary>
    /// 按键按下保持时长的"下限"（毫秒）。实际抬起时间等于音符结束时间，
    /// 以便实现根据音符时值的长按效果；当音符极短（短于该下限）时使用此值兜底，
    /// 防止部分按键事件丢失。
    /// </summary>
    public int HoldMs { get; set; } = 20;

    /// <summary>
    /// 启动演奏。
    /// </summary>
    /// <param name="output">具体输出（键盘 / MIDI 合成器）。Player 不持有它的所有权，
    /// 但会在演奏完成 / 取消时调用 <see cref="INoteOutput.Reset"/>，调用方需保证在 Player
    /// 整个 Run 过程中 output 都是可用的（典型做法：ViewModel 持有，Player 用完不释放）。</param>
    public void Play(IReadOnlyList<Note> allNotes, double fromSeconds, int countdownSeconds, INoteOutput output)
    {
        Stop();
        lock (_lock)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            IsPlaying = true;
            // 用局部变量捕获当前 notes / output，避免任务完成后 _task 字段继续通过闭包持有旧的引用
            var notes = allNotes;
            var sink = output;
            _task = Task.Run(() =>
            {
                try
                {
                    Run(notes, fromSeconds, countdownSeconds, sink, ct);
                }
                finally
                {
                    // 自然完成（非 Stop 触发）时清理字段，释放对闭包内 allNotes / output 的引用，
                    // 否则旧曲谱在切换曲谱后仍会被 Task 间接持有，造成内存持续增长。
                    lock (_lock)
                    {
                        if (ReferenceEquals(_cts, cts))
                        {
                            try { cts.Dispose(); } catch { /* ignored */ }
                            _cts = null;
                            _task = null;
                        }
                    }
                }
            }, ct);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _cts;
            task = _task;
            _cts = null;
            _task = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignored */ }
        try { task?.Wait(1500); } catch { /* ignored */ }
        cts.Dispose();
        IsPlaying = false;
    }

    private void Run(IReadOnlyList<Note> allNotes, double fromSeconds, int countdownSeconds, INoteOutput output, CancellationToken ct)
    {
        bool naturalFinish = false;
        try
        {
            // 倒计时
            for (int i = countdownSeconds; i > 0; i--)
            {
                if (ct.IsCancellationRequested) return;
                CountdownTick?.Invoke(i);
                Thread.Sleep(1000);
            }
            CountdownTick?.Invoke(0);

            // 让具体的 output 决定哪些音参与演奏（键盘 / 琴键试听跳过红色音；原曲试听全收）
            var upcoming = new List<Note>();
            foreach (var n in allNotes)
            {
                if (!output.ShouldPlay(n)) continue;
                if (n.Start < fromSeconds - 1e-6) continue;
                upcoming.Add(n);
            }

            var sw = Stopwatch.StartNew();
            // 虚拟播放时钟：elapsed = 实际时间 * Speed + fromSeconds
            double speed = Math.Max(0.25, Speed);

            // 活跃声部：需要在某个时刻抬起。voiceId 由 output 决定
            // （键盘=按键字符；MIDI=音高），用于"同声部连击时先抬再按"。
            var active = new List<(double releaseAt, int voiceId, Note note)>();
            int idx = 0;
            double nextPlayheadEmit = 0;

            while (!ct.IsCancellationRequested)
            {
                double now = fromSeconds + sw.Elapsed.TotalSeconds * speed;

                // 抬起到期声部
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (now >= active[i].releaseAt)
                    {
                        output.Release(active[i].note);
                        active.RemoveAt(i);
                    }
                }

                // 触发到期音符
                while (idx < upcoming.Count && upcoming[idx].Start <= now)
                {
                    var n = upcoming[idx++];
                    int voiceId = output.GetVoiceId(n);

                    // 若同一个声部仍处于按下状态，先抬起再重新按下，
                    // 确保游戏识别为一次新的音符触发，而不是被当作系统级自动重复。
                    // MIDI 模式下也需要这样做：同一音高重复 NoteOn 而没有 NoteOff，
                    // 在合成器里不会被听成重击。
                    for (int i = active.Count - 1; i >= 0; i--)
                    {
                        if (active[i].voiceId == voiceId)
                        {
                            output.Release(active[i].note);
                            active.RemoveAt(i);
                        }
                    }

                    output.Press(n);
                    // 抬起时间 = 音符结束时间（秒），以匹配音符时值实现长按；
                    // HoldMs 作为下限，避免极短音符没有可靠的按下/抬起间隔。
                    double minHold = Math.Max(0, HoldMs) / 1000.0;
                    double hold = Math.Max(minHold, n.Duration);
                    active.Add((now + hold, voiceId, n));
                }

                // 更新播放头 ~30Hz
                if (sw.Elapsed.TotalSeconds >= nextPlayheadEmit)
                {
                    PlayheadChanged?.Invoke(now);
                    nextPlayheadEmit += 1.0 / 30.0;
                }

                if (idx >= upcoming.Count && active.Count == 0) { naturalFinish = true; break; }

                // 精准但低占用的等待：根据下一个事件距离决定 sleep
                double nextEvent = double.PositiveInfinity;
                if (idx < upcoming.Count) nextEvent = Math.Min(nextEvent, upcoming[idx].Start);
                foreach (var a in active) if (a.releaseAt < nextEvent) nextEvent = a.releaseAt;

                double waitSec = (nextEvent - now) / speed;
                if (waitSec > 0.015) Thread.Sleep(5);
                else if (waitSec > 0.002) Thread.Sleep(1);
                else Thread.SpinWait(200);
            }

            // 清理：确保所有活跃声部被抬起
            foreach (var a in active) output.Release(a.note);
        }
        finally
        {
            IsPlaying = false;
            // 兜底：让合成器 / 键盘清理掉所有还在响 / 还按着的音
            try { output.Reset(); } catch { /* ignore */ }
            // 仅在自然播放完毕时通知"演奏结束"。
            // 主动 Stop() / Seek() 引发的取消不应触发 Finished，否则会被
            // 异步 dispatch 到 UI 线程后覆盖刚刚 Seek 重新启动播放时设置的 IsPlaying=true，
            // 造成播放按钮状态错乱。
            if (naturalFinish) Finished?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
