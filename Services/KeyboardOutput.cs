using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 把音符转换为键盘按键事件，注入到当前焦点窗口（原神等游戏）。
/// 只播放已成功映射到键盘按键（<see cref="Note.Supported"/> 且 <see cref="Note.Key"/> 非空）的音符。
/// </summary>
public sealed class KeyboardOutput : INoteOutput
{
    public bool ShouldPlay(Note note) => note.Supported && note.Key.HasValue;

    /// <summary>声部 = 键盘按键字符。同一按键的连击会触发"先抬起再按下"。</summary>
    public int GetVoiceId(Note note) =>
        note.Key.HasValue ? char.ToUpperInvariant(note.Key.Value) : -1;

    public void Press(Note note)
    {
        if (note.Key is char k) KeyboardSimulator.Press(k);
    }

    public void Release(Note note)
    {
        if (note.Key is char k) KeyboardSimulator.Release(k);
    }

    public void Reset()
    {
        // 键盘模式下没有"全局静音"指令——Player 自己维护活跃按键列表并显式 Release 即可。
    }
}
