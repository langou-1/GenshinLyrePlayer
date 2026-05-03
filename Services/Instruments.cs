using System.Collections.Generic;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 原神所有可用「诗琴」类乐器的注册表。
///
/// 键盘布局（所有琴都相同的物理布局）：
///   高音:  Q W E R T Y U
///   中音:  A S D F G H J
///   低音:  Z X C V B N M
///
/// 但不同琴的每个按键所对应的 MIDI 音高并不一样：
///
///   ▸ 老旧的诗琴（图中所示）：
///       高音 (Q-U): C5  Db5  Eb5  F5  G5  Ab5  Bb5   （近似 C 弗里几亚调式）
///       中音 (A-J): C4  D4   Eb4  F4  G4  A4   Bb4   （近似 C 多里安 / Bb 大调）
///       低音 (Z-M): C3  D3   Eb3  F3  G3  A3   Bb3
///
///   ▸ 风物之诗琴 / 镜花之琴（现代蒙德 / 稻妻琴）：
///       三个八度都是标准 C 大调白键：C  D  E  F  G  A  B
///
/// 因此不同乐器能直接演奏的 MIDI 音高不同，自动移调在当前乐器无法覆盖时，
/// 切换到其它乐器可能得到更好的结果。
/// </summary>
public static class Instruments
{
    // —— 布局 1：老旧的诗琴（独特调式）——
    private static IReadOnlyDictionary<int, char> OldLyreKeys { get; } = new Dictionary<int, char>
    {
        // 低音: C3 D3 Eb3 F3 G3 A3 Bb3
        { 48, 'Z' }, { 50, 'X' }, { 51, 'C' }, { 53, 'V' }, { 55, 'B' }, { 57, 'N' }, { 58, 'M' },
        // 中音: C4 D4 Eb4 F4 G4 A4 Bb4
        { 60, 'A' }, { 62, 'S' }, { 63, 'D' }, { 65, 'F' }, { 67, 'G' }, { 69, 'H' }, { 70, 'J' },
        // 高音: C5 Db5 Eb5 F5 G5 Ab5 Bb5
        { 72, 'Q' }, { 73, 'W' }, { 75, 'E' }, { 77, 'R' }, { 79, 'T' }, { 80, 'Y' }, { 82, 'U' },
    };

    // —— 布局 2：标准 21 键 C 大调（风物之诗琴 / 镜花之琴共用）——
    private static IReadOnlyDictionary<int, char> CMajor21Keys { get; } = new Dictionary<int, char>
    {
        // 低音 C3(48) - B3(59)
        { 48, 'Z' }, { 50, 'X' }, { 52, 'C' }, { 53, 'V' }, { 55, 'B' }, { 57, 'N' }, { 59, 'M' },
        // 中音 C4(60) - B4(71)
        { 60, 'A' }, { 62, 'S' }, { 64, 'D' }, { 65, 'F' }, { 67, 'G' }, { 69, 'H' }, { 71, 'J' },
        // 高音 C5(72) - B5(83)
        { 72, 'Q' }, { 74, 'W' }, { 76, 'E' }, { 77, 'R' }, { 79, 'T' }, { 81, 'Y' }, { 83, 'U' },
    };

    /// <summary>老旧的诗琴（默认）：非 C 大调调式，高音区含 Db/Ab。</summary>
    public static readonly Instrument OldLyre = new()
    {
        Id = "old-lyre",
        Name = "老旧的诗琴",
        Description = "蒙德 · 高音 C Phrygian / 中低音 C Dorian（Bb 大调）",
        PitchToKey = OldLyreKeys,
    };

    /// <summary>风物之诗琴：标准 C 大调 21 键。</summary>
    public static readonly Instrument FloralZither = new()
    {
        Id = "floral-zither",
        Name = "风物之诗琴",
        Description = "蒙德 · C 大调 21 键（3 个八度）",
        PitchToKey = CMajor21Keys,
    };

    /// <summary>镜花之琴：标准 C 大调 21 键。</summary>
    public static readonly Instrument VintageLyre = new()
    {
        Id = "vintage-lyre",
        Name = "镜花之琴",
        Description = "稻妻 · C 大调 21 键（3 个八度）",
        PitchToKey = CMajor21Keys,
    };

    /// <summary>所有可选乐器。列表顺序决定自动移调的扫描顺序（得分相同则偏向靠前者）。</summary>
    public static readonly IReadOnlyList<Instrument> All = new[]
    {
        OldLyre,
        FloralZither,
        VintageLyre,
    };

    /// <summary>默认乐器：老旧的诗琴。</summary>
    public static Instrument Default => OldLyre;

    /// <summary>C 大调键位的通用唱名标签，仅用于调试/显示；老旧的诗琴会返回空串。</summary>
    public static string GetStandardLabel(int pitch)
    {
        var name = pitch switch
        {
            48 or 60 or 72 => "do",
            50 or 62 or 74 => "re",
            52 or 64 or 76 => "mi",
            53 or 65 or 77 => "fa",
            55 or 67 or 79 => "so",
            57 or 69 or 81 => "la",
            59 or 71 or 83 => "ti",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var oct = pitch < 60 ? "低" : pitch < 72 ? "中" : "高";
        return $"{oct}{name}";
    }
}
