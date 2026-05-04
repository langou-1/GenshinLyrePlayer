using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// 同一声部连续触发之间，强制留给游戏识别 Release → Press 的"安静间隙"（实时毫秒）。
    /// <para>
    /// 起因：原神等通过 DirectInput / RawInput 读键的游戏，如果上一次 KeyUp 紧接着同一键的
    /// KeyDown，可能来不及处理 KeyUp，把后一次 KeyDown 当作"持续按住"而漏掉一次音符触发。
    /// 这里通过预扫描 upcoming 找到每个音符之后同一声部（同物理键 / 同音高）的下一次起音，
    /// 主动把当前音符的抬起时间提前到 <c>nextStart - ReleaseGapMs</c>，强行制造一段无键事件的间隙。
    /// </para>
    /// 由于 upcoming 是跨轨道按 Start 全局排序的，同一物理键不论来自哪条轨道都共用一个声部——
    /// 即使 Track A 还在长按 Q、中途 Track B 又要在 Q 上重击，A 的抬起也会被自动提前到 B 之前
    /// ReleaseGapMs 处。Mute / Solo 通过"启动播放时的可发声轨道快照"参与预扫描，因此用户在
    /// 未演奏时改 Mute、再点播放即可让截断决策跟随最新的轨道集合。
    /// 没有同声部后继音的音符不受影响——仍按音符时值释放。
    /// <para>
    /// 注意：本值依赖 Run() 在执行期间通过 timeBeginPeriod(1) 把 Windows 计时器分辨率
    /// 提升到 1ms，否则默认 ~15.6ms 的 Sleep 粒度可能让单次 Sleep 同时跨过 release 与
    /// press 两个事件，导致它们在同一次循环 iter 内背靠背 SendInput——本字段失效。
    /// </para>
    /// </summary>
    public int ReleaseGapMs { get; set; } = 30;

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

    /// <summary>
    /// winmm: 把系统 multimedia timer 分辨率提升到指定毫秒。
    /// Run() 期间使用，让 <see cref="Thread.Sleep(int)"/> 真正接近毫秒级精度，
    /// 而不是默认的 ~15.6ms（64Hz）粒度——这是保证 ReleaseGapMs 在实际执行中
    /// 不被 Sleep 超调吞掉的关键。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private void Run(IReadOnlyList<Note> allNotes, double fromSeconds, int countdownSeconds, INoteOutput output, CancellationToken ct)
    {
        bool naturalFinish = false;
        // 提升计时器分辨率到 1ms。整个 Run() 期间生效，finally 中成对回退。
        // 进程级生效，对 GC / Tier0 编译等其他线程行为也有微小影响（功耗略升），
        // 但只在演奏期间提升，演奏结束立刻还原，可接受。
        bool timerRaised = false;
        try
        {
            timerRaised = TimeBeginPeriod(1) == 0; // 0 == TIMERR_NOERROR
        }
        catch
        {
            // winmm.dll 任何加载/调用失败都不影响演奏逻辑，只是精度退化。
        }
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

            // 让具体的 output 决定哪些音参与演奏（键盘 / 琴键试听跳过红色音；原曲试听全收）。
            // 调用方（MainWindowViewModel）已经把所有可发声轨道按 Start 合并排序后传进来，
            // 这里只做"按输出过滤 + 起播位置裁剪"，不再重新排序。
            var upcoming = new List<Note>();
            foreach (var n in allNotes)
            {
                if (!output.ShouldPlay(n)) continue;
                if (n.Start < fromSeconds - 1e-6) continue;
                upcoming.Add(n);
            }

            // 预扫描：对每个 upcoming[i]，记录 i 之后同一 voiceId 下一次的 Start，
            // 没有则 +∞。Press 时用它把抬起时间主动提前到 nextStart - ReleaseGap，
            // 给游戏留出稳定可见的 KeyUp → KeyDown 间隙（详见 ReleaseGapMs 注释）。
            // O(N) 一次性建表；voiceId 由 output 决定（键盘=按键字符 / MIDI=音高），
            // 整个 Run 期间不变。upcoming 已是全局时间序，"下一次同声部"是
            // 跨轨道意义下的下一次——天然处理"另一条轨道在同键长按中途又来打一下"。
            var nextSameVoiceStart = new double[upcoming.Count];
            {
                var lastSeen = new Dictionary<int, double>();
                for (int i = upcoming.Count - 1; i >= 0; i--)
                {
                    int v = output.GetVoiceId(upcoming[i]);
                    nextSameVoiceStart[i] = lastSeen.TryGetValue(v, out var s) ? s : double.PositiveInfinity;
                    lastSeen[v] = upcoming[i].Start;
                }
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
                    int currentIdx = idx;
                    var n = upcoming[idx++];
                    int voiceId = output.GetVoiceId(n);

                    // 若同一个声部仍处于按下状态，先抬起再重新按下，
                    // 确保游戏识别为一次新的音符触发，而不是被当作系统级自动重复。
                    // MIDI 模式下也需要这样做：同一音高重复 NoteOn 而没有 NoteOff，
                    // 在合成器里不会被听成重击。
                    // 注：在 ReleaseGapMs > 0 的常规情况下，下面的 lookahead 会让上一个
                    // 同声部音符的 releaseAt 早于本次 Press，"抬起到期声部"那一段已经把
                    // 它从 active 里移走；这里仅作为 ReleaseGapMs=0 或浮点边界的兜底。
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
                    double releaseAt = now + hold;

                    // Lookahead：若同声部稍后还会被按，把抬起时间提前到 nextStart - gap，
                    // 强行留出实时间隙。ReleaseGapMs 是"实时毫秒"，换算到曲谱时间需乘以 speed，
                    // 这样不论倍速多少，游戏看到的 KeyUp → KeyDown 真空期都是恒定的真实长度。
                    double nextSv = nextSameVoiceStart[currentIdx];
                    if (!double.IsPositiveInfinity(nextSv))
                    {
                        double gapScored = Math.Max(0, ReleaseGapMs) / 1000.0 * speed;
                        double mustReleaseBy = nextSv - gapScored;
                        if (mustReleaseBy < releaseAt) releaseAt = mustReleaseBy;
                    }
                    active.Add((releaseAt, voiceId, n));
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
            // 还原计时器分辨率，避免本进程长期占用高频 multimedia timer。
            if (timerRaised)
            {
                try { TimeEndPeriod(1); } catch { /* ignore */ }
            }
            // 仅在自然播放完毕时通知"演奏结束"。
            // 主动 Stop() / Seek() 引发的取消不应触发 Finished，否则会被
            // 异步 dispatch 到 UI 线程后覆盖刚刚 Seek 重新启动播放时设置的 IsPlaying=true，
            // 造成播放按钮状态错乱。
            if (naturalFinish) Finished?.Invoke();
        }
    }

    public void Dispose() => Stop();
}
