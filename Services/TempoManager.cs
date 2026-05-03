using System;
using System.Collections.Generic;
using System.Linq;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 整首曲子的曲速管理：维护一个按 <see cref="TempoMarker.Tick"/> 升序的标签列表，并提供
/// tick ⇄ 秒 互转 + 编辑某个标签 BPM 后重算所有 <see cref="TempoMarker.Time"/> 的能力。
///
/// 用法：
/// 1. 解析阶段从 MIDI 中收集每个 SetTempo 事件 → 构造一个 TempoManager；
/// 2. 调用 <see cref="TickToSeconds"/> 把每个 <see cref="Note"/> 的 <see cref="Note.StartTick"/> /
///    <see cref="Note.DurationTick"/> 折算成秒，写入 <see cref="Note.Start"/> / <see cref="Note.Duration"/>；
/// 3. 用户在速度轨上修改某个 BPM → <see cref="SetBpm"/> 会重算受影响的 <c>Time</c> 并触发
///    <see cref="Changed"/>，订阅者（ViewModel）随后再次刷新所有 Note 的秒时间。
/// </summary>
public sealed class TempoManager
{
    /// <summary>每四分音符的 tick 数（PPQ）。来自 MIDI Header，全程不变。</summary>
    public int Ppq { get; }

    private readonly List<TempoMarker> _markers;

    /// <summary>按 Tick 升序排列的曲速标签。第一个标签必然位于 Tick = 0。</summary>
    public IReadOnlyList<TempoMarker> Markers => _markers;

    /// <summary>当任意 marker 的 BPM 被改写后触发。订阅者通常会重算所有 Note 的秒时间并刷新 UI。</summary>
    public event Action? Changed;

    public TempoManager(int ppq, IEnumerable<(long Tick, int Mpqn)>? raw = null)
    {
        Ppq = ppq <= 0 ? 480 : ppq;

        _markers = (raw ?? Array.Empty<(long, int)>())
            .Where(t => t.Mpqn > 0)
            .OrderBy(t => t.Tick)
            .Select(t => new TempoMarker { Tick = Math.Max(0, t.Tick), Mpqn = t.Mpqn })
            .ToList();

        // 必须保证起点 (Tick=0) 一定有一个 marker，否则 TickToSeconds 在曲首没有 tempo 段可用。
        if (_markers.Count == 0 || _markers[0].Tick != 0)
        {
            int firstMpqn = _markers.Count > 0 ? _markers[0].Mpqn : 500_000; // 默认 120 BPM
            _markers.Insert(0, new TempoMarker { Tick = 0, Mpqn = firstMpqn });
        }

        // 同一 BPM 连续出现（包括同一 tick 上多个 SetTempo 事件）：保留更早的，去掉后面的，
        // 让速度轨上的标签更整洁。
        for (int i = _markers.Count - 1; i > 0; i--)
        {
            if (_markers[i].Mpqn == _markers[i - 1].Mpqn)
                _markers.RemoveAt(i);
        }

        Recompute();
    }

    /// <summary>把任意 tick 折算成绝对时间（秒）。</summary>
    public double TickToSeconds(long tick)
    {
        if (tick <= 0 || _markers.Count == 0) return 0;
        int i = 0;
        for (int k = 1; k < _markers.Count; k++)
        {
            if (_markers[k].Tick > tick) break;
            i = k;
        }
        var m = _markers[i];
        return m.Time + (tick - m.Tick) * (m.Mpqn / 1_000_000.0) / Ppq;
    }

    /// <summary>把秒折算回 tick（用于保持播放头/视野跨越曲速编辑前后的"音乐意义"位置不变）。</summary>
    public long SecondsToTick(double seconds)
    {
        if (seconds <= 0 || _markers.Count == 0) return 0;
        int i = 0;
        for (int k = 1; k < _markers.Count; k++)
        {
            if (_markers[k].Time > seconds) break;
            i = k;
        }
        var m = _markers[i];
        double dt = seconds - m.Time;
        return m.Tick + (long)Math.Round(dt * 1_000_000.0 * Ppq / m.Mpqn);
    }

    /// <summary>
    /// 把指定 marker 的 BPM 改成 <paramref name="newBpm"/>（自动 clamp 到 10–960）。
    /// 若 BPM 实际有变更：重算所有 marker 的 Time，并触发 <see cref="Changed"/>，返回 true；
    /// 若值未变化或 marker 不在列表里：返回 false。
    /// </summary>
    public bool SetBpm(TempoMarker marker, double newBpm)
    {
        if (marker == null || !_markers.Contains(marker)) return false;
        newBpm = Math.Clamp(newBpm, 10, 960);
        int newMpqn = Math.Max(1, (int)Math.Round(60_000_000.0 / newBpm));
        if (newMpqn == marker.Mpqn) return false;

        marker.Mpqn = newMpqn;
        Recompute();
        Changed?.Invoke();
        return true;
    }

    /// <summary>根据当前每个 marker 的 Mpqn 重新累加 Time。</summary>
    private void Recompute()
    {
        if (_markers.Count == 0) return;
        _markers[0].Time = 0;
        double sec = 0;
        for (int i = 1; i < _markers.Count; i++)
        {
            sec += (_markers[i].Tick - _markers[i - 1].Tick)
                   * (_markers[i - 1].Mpqn / 1_000_000.0)
                   / Ppq;
            _markers[i].Time = sec;
        }
    }
}
