using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 演奏输出抽象。Player 把"按下 / 抬起"具体落到哪里——
/// 是发到游戏窗口的键盘按键事件，还是本机 MIDI 合成器——由实现决定。
///
/// 设计要点：
/// <list type="bullet">
///   <item><see cref="ShouldPlay"/>：让输出自己决定哪些音符参与演奏（例如键盘 / 琴键试听
///         模式跳过不支持的音；原曲试听模式则全部参与）。Player 不再硬编码过滤条件。</item>
///   <item><see cref="GetVoiceId"/>：当下一个 Press 的"声部"已经在按下中（同一个琴键 /
///         同一个 MIDI 音高的连击），Player 会先 Release 再重新 Press 来产生重击效果。
///         这里用 voiceId 抽象表示，键盘模式按键字符、MIDI 模式按音高。</item>
/// </list>
/// </summary>
public interface INoteOutput
{
    /// <summary>该音符在本输出下是否会被演奏（true = 参与）。</summary>
    bool ShouldPlay(Note note);

    /// <summary>声部标识；同一个 voiceId 同时只能有一个处于"按下"状态。</summary>
    int GetVoiceId(Note note);

    /// <summary>按下 / 触发音符。Player 保证每一次 Press 之后最终会有一次 Release。</summary>
    void Press(Note note);

    /// <summary>抬起 / 结束音符。</summary>
    void Release(Note note);

    /// <summary>清理所有正在响 / 按下的音符（用于 Player 异常退出 / 取消时兜底）。</summary>
    void Reset();
}
