namespace GenshinLyrePlayer.Models;

/// <summary>
/// 一个曲速变化标签：在 <see cref="Time"/>(秒) 处把 BPM 切换为 <see cref="Bpm"/>。
/// 对应 MIDI 中的 SetTempo meta 事件。仅用于显示，不参与播放节拍计算
/// （播放仍由 <see cref="Services.MidiParser"/> 解析时把 tempo 折算进每个音符的绝对时间）。
/// </summary>
public sealed class TempoMarker
{
    /// <summary>该 BPM 开始生效的绝对时间（秒）。</summary>
    public double Time { get; init; }

    /// <summary>每分钟拍数。</summary>
    public double Bpm { get; init; }
}
