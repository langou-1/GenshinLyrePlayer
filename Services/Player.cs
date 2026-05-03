using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 演奏线程。在指定起始时间（秒）开始发送键盘事件，跳过不支持的音。
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

    public void Play(IReadOnlyList<Note> allNotes, double fromSeconds, int countdownSeconds)
    {
        Stop();
        lock (_lock)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            IsPlaying = true;
            // 用局部变量捕获当前 notes，避免任务完成后 _task 字段继续通过闭包持有旧的音符列表
            var notes = allNotes;
            _task = Task.Run(() =>
            {
                try
                {
                    Run(notes, fromSeconds, countdownSeconds, ct);
                }
                finally
                {
                    // 自然完成（非 Stop 触发）时清理字段，释放对闭包内 allNotes 的引用，
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

    private void Run(IReadOnlyList<Note> allNotes, double fromSeconds, int countdownSeconds, CancellationToken ct)
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

            // 只取支持且在起始时间之后的音
            var upcoming = new List<Note>();
            foreach (var n in allNotes)
            {
                if (!n.Supported || n.Key is null) continue;
                if (n.Start < fromSeconds - 1e-6) continue;
                upcoming.Add(n);
            }

            var sw = Stopwatch.StartNew();
            // 虚拟播放时钟：elapsed = 实际时间 * Speed + fromSeconds
            double speed = Math.Max(0.25, Speed);

            // 活跃按键：需要在某个时刻抬起
            var active = new List<(double releaseAt, char key)>();
            int idx = 0;
            double nextPlayheadEmit = 0;

            while (!ct.IsCancellationRequested)
            {
                double now = fromSeconds + sw.Elapsed.TotalSeconds * speed;

                // 抬起到期按键
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (now >= active[i].releaseAt)
                    {
                        KeyboardSimulator.Release(active[i].key);
                        active.RemoveAt(i);
                    }
                }

                // 触发到期音符
                while (idx < upcoming.Count && upcoming[idx].Start <= now)
                {
                    var n = upcoming[idx++];
                    if (n.Key is char k)
                    {
                        // 若同一按键仍处于按下状态，先抬起再重新按下，
                        // 确保游戏识别为一次新的音符触发，而不是被当作系统级自动重复。
                        for (int i = active.Count - 1; i >= 0; i--)
                        {
                            if (active[i].key == k)
                            {
                                KeyboardSimulator.Release(k);
                                active.RemoveAt(i);
                            }
                        }
                        KeyboardSimulator.Press(k);
                        // 抬起时间 = 音符结束时间（秒），以匹配音符时值实现长按；
                        // HoldMs 作为下限，避免极短音符没有可靠的按下/抬起间隔。
                        double minHold = Math.Max(0, HoldMs) / 1000.0;
                        double hold = Math.Max(minHold, n.Duration);
                        active.Add((now + hold, k));
                    }
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

            // 清理：确保所有活跃按键被抬起
            foreach (var a in active) KeyboardSimulator.Release(a.key);
        }
        finally
        {
            IsPlaying = false;
            // 仅在自然播放完毕时通知"演奏结束"。
            // 主动 Stop() / Seek() 引发的取消不应触发 Finished，否则会被
            // 异步 dispatch 到 UI 线程后覆盖刚刚 Seek 重新启动播放时设置的 IsPlaying=true，
            // 造成播放按钮状态错乱。
            if (naturalFinish) Finished?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
