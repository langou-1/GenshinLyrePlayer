using System.Collections.Generic;
using System.Linq;

namespace GenshinLyrePlayer.Models;

/// <summary>
/// 一个「乐器」定义：MIDI 音高 → 键盘按键 的映射 + 元数据。
/// 所有原神琴类乐器当前共用 C 大调自然音阶 21 键（三个八度）布局；
/// 如果未来有新乐器具有不同的键位，只需在 <see cref="Services.Instruments"/> 中注册即可。
/// </summary>
public sealed class Instrument
{
    /// <summary>内部 ID，例如 "old-lyre"。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名称，例如 "老旧的诗琴"。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>次级说明，例如 "蒙德 · C 大调 21 键"。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>MIDI 音高 → 键盘字符。</summary>
    public IReadOnlyDictionary<int, char> PitchToKey { get; init; } =
        new Dictionary<int, char>();

    /// <summary>映射中最小的 MIDI 音高（供 UI 高亮可演奏区使用）。</summary>
    public int MinPitch => PitchToKey.Count == 0 ? 0 : PitchToKey.Keys.Min();

    /// <summary>映射中最大的 MIDI 音高。</summary>
    public int MaxPitch => PitchToKey.Count == 0 ? 0 : PitchToKey.Keys.Max();

    /// <summary>该音高是否能在本乐器上直接弹奏。</summary>
    public bool IsSupported(int pitch) => PitchToKey.ContainsKey(pitch);

    /// <summary>获取按键字符，不支持时返回 null。</summary>
    public char? GetKey(int pitch) =>
        PitchToKey.TryGetValue(pitch, out var k) ? k : null;

    public override string ToString() => Name;
}
