namespace GenshinLyrePlayer.Models;

/// <summary>
/// 单个音符的静态数据（已按当前移调计算）。
///
/// 时间字段说明（重要）：
/// - <see cref="StartTick"/> / <see cref="DurationTick"/> 是音符在 MIDI 文件里的"权威时间"，
///   不会随用户编辑曲速而改变；
/// - <see cref="Start"/> / <see cref="Duration"/>（秒）是由 <see cref="Services.TempoManager"/>
///   根据 tick + 当前曲速派生出来的，会在用户编辑 BPM 后被重新写回，所以是 <c>set</c> 而非
///   <c>init</c>。Player / 缩略图 / 钢琴卷帘统一使用秒来播放和绘制。
/// </summary>
public sealed class Note
{
    /// <summary>原始 MIDI 音高 (0-127)。</summary>
    public int OriginalPitch { get; init; }

    /// <summary>原始 MIDI tick 起点（PPQ 单位由 <see cref="Services.TempoManager.Ppq"/> 决定）。</summary>
    public long StartTick { get; init; }

    /// <summary>原始 MIDI tick 时长，>= 0。</summary>
    public long DurationTick { get; init; }

    /// <summary>派生的起始时间（秒），由 TempoManager 计算并写入。</summary>
    public double Start { get; set; }

    /// <summary>派生的持续时间（秒），由 TempoManager 计算并写入。</summary>
    public double Duration { get; set; }

    /// <summary>通道（仅用于调试/显示）。</summary>
    public int Channel { get; init; }

    /// <summary>力度 0–127。</summary>
    public int Velocity { get; init; }

    // —— 下列字段在移调后刷新 ——

    /// <summary>应用移调后最终触发的 MIDI 音高。</summary>
    public int EffectivePitch { get; set; }

    /// <summary>是否能在琴上演奏。</summary>
    public bool Supported { get; set; }

    /// <summary>若 Supported 为 true，对应的键盘按键字符。</summary>
    public char? Key { get; set; }

    public double End => Start + Duration;
}
