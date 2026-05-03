namespace GenshinLyrePlayer.Models;

/// <summary>
/// 演奏模式：决定 Player 把音符送到哪里、以及是否过滤"不可演奏"的音。
/// </summary>
public enum PlaybackMode
{
    /// <summary>把按键事件发送到当前焦点窗口（原神）。默认模式。</summary>
    Genshin = 0,

    /// <summary>本机用 Windows 内置合成器试听，按琴键映射后的音高（与游戏一致，跳过红色不支持音）。</summary>
    PreviewLyre = 1,

    /// <summary>本机用 Windows 内置合成器试听，按原 MIDI 音高完整演奏（无视琴键 / 移调限制）。</summary>
    PreviewOriginal = 2,
}

/// <summary>
/// 演奏模式的显示选项（供 ComboBox 绑定）。
/// </summary>
public sealed class PlaybackModeOption
{
    public PlaybackMode Mode { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public override string ToString() => Name;
}
