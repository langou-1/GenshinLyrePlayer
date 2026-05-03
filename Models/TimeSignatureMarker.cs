namespace GenshinLyrePlayer.Models;

/// <summary>
/// 一个 MIDI 拍号标签：从 <see cref="Tick"/> 起、到下一个标签之前所有 bar 都使用本
/// <see cref="Numerator"/> / <see cref="Denominator"/>（如 4/4、3/4、6/8）。
///
/// <see cref="BarIndex"/> 是从曲首到本标签所累计的 bar 序号（0 起算），由
/// <see cref="Services.TimeSignatureManager"/> 在初始化时填入；用于在 PianoRoll
/// 上把任意 bar 序号回算成 tick。
/// </summary>
public sealed class TimeSignatureMarker
{
    /// <summary>本拍号生效的起始 tick。第一个标签必然位于 0。</summary>
    public long Tick { get; init; }

    /// <summary>分子（每小节多少拍）。</summary>
    public int Numerator { get; init; } = 4;

    /// <summary>实际分母（4 = 四分音符；8 = 八分音符）。已从 MIDI 的 2^x 编码展开。</summary>
    public int Denominator { get; init; } = 4;

    /// <summary>本标签起算的累计 bar 序号（0 起算）。由 TimeSignatureManager 计算。</summary>
    public int BarIndex { get; set; }
}
