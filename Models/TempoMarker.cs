using System;

namespace GenshinLyrePlayer.Models;

/// <summary>
/// 一个曲速变化标签：在 <see cref="Tick"/>(MIDI tick) 处把 mpqn 切换为 <see cref="Mpqn"/>，
/// 对应的人类可读 BPM 由 <see cref="Bpm"/> 派生。
///
/// <see cref="Time"/>（秒）由 <see cref="Services.TempoManager"/> 根据所有先前的 Mpqn 段累加计算出来，
/// 会在某个 marker 的 BPM 被修改后重新写回，因此它是 <c>set</c> 而不是 <c>init</c>。
/// </summary>
public sealed class TempoMarker
{
    /// <summary>该 BPM 开始生效的 MIDI tick 位置（不会随编辑改变）。</summary>
    public long Tick { get; init; }

    /// <summary>每四分音符的微秒数 (microseconds per quarter note)。 BPM = 60_000_000 / Mpqn。</summary>
    public int Mpqn { get; set; }

    /// <summary>该 BPM 段开始的绝对时间（秒）。仅作展示和命中测试使用，会被 TempoManager 重算。</summary>
    public double Time { get; set; }

    /// <summary>每分钟拍数（派生属性，读写均经 <see cref="Mpqn"/>）。</summary>
    public double Bpm
    {
        get => Mpqn > 0 ? 60_000_000.0 / Mpqn : 0;
        set
        {
            if (value > 0) Mpqn = (int)Math.Round(60_000_000.0 / value);
        }
    }
}
