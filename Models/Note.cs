namespace GenshinLyrePlayer.Models;

/// <summary>
/// 单个音符的静态数据（已按当前移调计算）。
/// </summary>
public sealed class Note
{
    /// <summary>原始 MIDI 音高 (0-127)。</summary>
    public int OriginalPitch { get; init; }

    /// <summary>起始时间（秒）。</summary>
    public double Start { get; init; }

    /// <summary>持续时间（秒）。</summary>
    public double Duration { get; init; }

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
