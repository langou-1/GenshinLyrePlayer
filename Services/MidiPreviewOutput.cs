using System;
using GenshinLyrePlayer.Models;
using NAudio.Midi;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 用 Windows 内置的 GS Wavetable Synth（设备 0）合成 MIDI 音播放出来——本机试听用。
/// 不会向游戏窗口发送任何键盘事件。
///
/// 两种试听口味，由 <see cref="UseOriginalPitch"/> 决定：
/// <list type="bullet">
///   <item><c>false</c>（琴键试听）：跳过不可演奏音，按 <see cref="Note.EffectivePitch"/>（移调后）发声。
///         听到的内容跟在游戏里听到的一致。</item>
///   <item><c>true</c>（原曲试听）：忽略琴键 / 移调限制，按 <see cref="Note.OriginalPitch"/> 完整演奏原 MIDI。</item>
/// </list>
/// </summary>
public sealed class MidiPreviewOutput : INoteOutput, IDisposable
{
    /// <summary>固定输出到 GM 通道 1（NAudio 的 channel 是 1 起算）。</summary>
    private const int Channel = 1;

    private readonly object _lock = new();
    private MidiOut? _midi;

    public bool UseOriginalPitch { get; }

    /// <summary>当前正在使用的 GM Program (0..127)。</summary>
    public int Program { get; private set; }

    public MidiPreviewOutput(bool useOriginalPitch, int gmProgram)
    {
        UseOriginalPitch = useOriginalPitch;

        if (MidiOut.NumberOfDevices <= 0)
            throw new InvalidOperationException(
                "未检测到可用的 MIDI 输出设备（Microsoft GS Wavetable Synth）。");

        // 设备 0 在 Windows 上一般就是 Microsoft GS Wavetable Synth。
        _midi = new MidiOut(0);
        SetProgram(gmProgram);
    }

    public void SetProgram(int gmProgram)
    {
        gmProgram = Math.Clamp(gmProgram, 0, 127);
        lock (_lock)
        {
            Program = gmProgram;
            if (_midi == null) return;
            var pc = new PatchChangeEvent(0, Channel, gmProgram);
            _midi.Send(pc.GetAsShortMessage());
        }
    }

    public bool ShouldPlay(Note note) => UseOriginalPitch ? true : note.Supported;

    public int GetVoiceId(Note note) =>
        UseOriginalPitch ? note.OriginalPitch : note.EffectivePitch;

    public void Press(Note note)
    {
        int pitch = ResolvePitch(note);
        if (pitch < 0) return;
        // MIDI 规定 NoteOn 力度=0 等价 NoteOff，所以兜底 1。
        int velocity = note.Velocity > 0 ? Math.Clamp(note.Velocity, 1, 127) : 100;
        lock (_lock)
        {
            if (_midi == null) return;
            var on = new NoteOnEvent(0, Channel, pitch, velocity, 0);
            _midi.Send(on.GetAsShortMessage());
        }
    }

    public void Release(Note note)
    {
        int pitch = ResolvePitch(note);
        if (pitch < 0) return;
        lock (_lock)
        {
            if (_midi == null) return;
            var off = new NoteEvent(0, Channel, MidiCommandCode.NoteOff, pitch, 0);
            _midi.Send(off.GetAsShortMessage());
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_midi == null) return;
            // CC 123 = All Notes Off：让合成器立即停止本通道所有正在响的音。
            var allOff = new ControlChangeEvent(0, Channel, MidiController.AllNotesOff, 0);
            _midi.Send(allOff.GetAsShortMessage());
        }
    }

    private int ResolvePitch(Note note)
    {
        int p = UseOriginalPitch ? note.OriginalPitch : note.EffectivePitch;
        if (p < 0 || p > 127) return -1;
        return p;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_midi == null) return;
            try
            {
                var allOff = new ControlChangeEvent(0, Channel, MidiController.AllNotesOff, 0);
                _midi.Send(allOff.GetAsShortMessage());
            }
            catch { /* 设备已无效就忽略 */ }
            try { _midi.Dispose(); } catch { /* ignore */ }
            _midi = null;
        }
    }
}
