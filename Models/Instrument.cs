using System.Collections.Generic;
using System.Linq;

namespace GenshinLyrePlayer.Models;

/// <summary>
/// 一个「乐器组」：一组共享相同键位映射（MIDI 音高 → 键盘按键）的原神乐器。
/// 组内的乐器在游戏里按键体验完全等价（例如「风物之诗琴」与「镜花之琴」同属
/// C 大调 21 键组），因此在 UI 与自动移调逻辑中都以组为单位进行选择与切换。
/// 如果未来新增乐器具有新的键位，只需在 <see cref="Services.Instruments"/> 中
/// 注册一个新组即可。
/// </summary>
public sealed class InstrumentGroup
{
    /// <summary>内部 ID，例如 "c-major-lyres"。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名称，例如 "风物之诗琴 / 镜花之琴"。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>次级说明，例如 "C 大调 21 键（3 个八度）"。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>组内包含的具体乐器显示名（用于 UI 展示，可为空）。</summary>
    public IReadOnlyList<string> MemberNames { get; init; } = new List<string>();

    /// <summary>MIDI 音高 → 键盘字符。组内所有乐器共用此映射。</summary>
    public IReadOnlyDictionary<int, char> PitchToKey { get; init; } =
        new Dictionary<int, char>();

    /// <summary>映射中最小的 MIDI 音高（供 UI 高亮可演奏区使用）。</summary>
    public int MinPitch => PitchToKey.Count == 0 ? 0 : PitchToKey.Keys.Min();

    /// <summary>映射中最大的 MIDI 音高。</summary>
    public int MaxPitch => PitchToKey.Count == 0 ? 0 : PitchToKey.Keys.Max();

    /// <summary>该音高是否能在本组乐器上直接弹奏。</summary>
    public bool IsSupported(int pitch) => PitchToKey.ContainsKey(pitch);

    /// <summary>获取按键字符，不支持时返回 null。</summary>
    public char? GetKey(int pitch) =>
        PitchToKey.TryGetValue(pitch, out var k) ? k : null;

    /// <summary>"风物之诗琴、镜花之琴" 这样拼接的成员名字符串（UI 展示用）。</summary>
    public string MembersText => MemberNames.Count == 0 ? string.Empty : string.Join("、", MemberNames);

    public override string ToString() => Name;
}
