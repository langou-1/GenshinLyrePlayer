using System;
using System.Collections.Generic;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 解析字母谱 DSL 文本并生成与 <see cref="MidiParser.ParseResult"/> 兼容的轨道数据。
///
/// DSL 语法：
///   字母          — 演奏对应音符，时值十六分音符
///   空格          — 休止，时值十六分音符
///   (ABC)        — 和弦，括号内音符同时按下，时值十六分音符
///   [ABC]        — 三十二分音符组，每个元素时值三十二分音符
///   {ABC}        — 六十四分音符组，每个元素时值六十四分音符
///   &lt;ABC&gt;        — 三连音组，3 个音在一个十六分音符内平分
///   -            — 延音，延长前一个音符的时值（无前导音符时视为休止）
///   \ / 换行     — 视觉分隔符，忽略
///
/// 括号可任意嵌套，时值由直接外层括号决定。
///
/// 键位映射（风物之诗琴 / 镜花之琴 C 大调 21 键）：
///   高音: Q(C5) W(D5) E(E5) R(F5) T(G5) Y(A5) U(B5)
///   中音: A(C4) S(D4) D(E4) F(F4) G(G4) H(A4) J(B4)
///   低音: Z(C3) X(D3) C(E3) V(F3) B(G3) N(A3) M(B3)
/// </summary>
public static class LetterScoreParser
{
    // ===== 时值常量（PPQ=480, BPM=120）=====

    /// <summary>十六分音符（字母/空格/() 和弦的默认时值）</summary>
    private const long Q = 120;
    /// <summary>三十二分音符（[] 括号内每元素时值）</summary>
    private const long E = 60;
    /// <summary>六十四分音符（{} 括号内每元素时值）</summary>
    private const long S = 30;
    /// <summary>三连音（&lt;&gt; 括号内每元素时值，120/3 = 40）</summary>
    private const long T = 40;

    private const int DefaultPpq = 480;
    private const double DefaultBpm = 120;

    /// <summary>字母 → MIDI 音高。</summary>
    private static readonly Dictionary<char, int> KeyToPitch = new()
    {
        { 'Q', 72 }, { 'W', 74 }, { 'E', 76 }, { 'R', 77 }, { 'T', 79 }, { 'Y', 81 }, { 'U', 83 },
        { 'A', 60 }, { 'S', 62 }, { 'D', 64 }, { 'F', 65 }, { 'G', 67 }, { 'H', 69 }, { 'J', 71 },
        { 'Z', 48 }, { 'X', 50 }, { 'C', 52 }, { 'V', 53 }, { 'B', 55 }, { 'N', 57 }, { 'M', 59 },
    };

    // ===== 公共 API =====

    public static MidiParser.ParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("字母谱文本不能为空");

        var notes = new List<Note>();
        long currentTick = 0;
        int pos = 0;

        ParseElements(text, ref pos, ref currentTick, notes, Q, '\0');

        if (notes.Count == 0)
            throw new ArgumentException(
                "字母谱中没有找到有效的音符（有效字母: Q W E R T Y U A S D F G H J Z X C V B N M）");

        return BuildResult(notes, currentTick);
    }

    // ================================================================
    //  递归解析
    // ================================================================

    /// <summary>
    /// 解析元素序列，直到遇到 <paramref name="stopChar"/> 或文本结束。
    /// </summary>
    private static void ParseElements(string text, ref int pos, ref long currentTick,
        List<Note> notes, long baseTick, char stopChar)
    {
        while (pos < text.Length)
        {
            char ch = text[pos];

            if (ch == stopChar)
                break;

            // 视觉分隔符：忽略
            if (IsSeparator(ch))
            {
                pos++;
                continue;
            }

            // 空格：休止
            if (ch == ' ')
            {
                pos++;
                currentTick += baseTick;
                continue;
            }

            // 延音：延长前一个音符，无前导音符则视为休止
            if (ch == '-')
            {
                pos++;
                if (!ExtendNotesEndingAt(notes, currentTick, baseTick))
                {
                    // 无音符可延长，视为休止
                }
                currentTick += baseTick;
                continue;
            }

            // 和弦 ()
            if (ch == '(')
            {
                pos++;
                long chordStart = currentTick;
                ParseChord(text, ref pos, ref currentTick, notes, baseTick);
                if (pos < text.Length && text[pos] == ')')
                    pos++;
                continue;
            }

            // 八分音符组 []
            if (ch == '[')
            {
                pos++;
                ParseElements(text, ref pos, ref currentTick, notes, E, ']');
                if (pos < text.Length && text[pos] == ']')
                    pos++;
                continue;
            }

            // 十六分音符组 {}
            if (ch == '{')
            {
                pos++;
                ParseElements(text, ref pos, ref currentTick, notes, S, '}');
                if (pos < text.Length && text[pos] == '}')
                    pos++;
                continue;
            }

            // 三连音组 <>
            if (ch == '<')
            {
                pos++;
                ParseElements(text, ref pos, ref currentTick, notes, T, '>');
                if (pos < text.Length && text[pos] == '>')
                    pos++;
                continue;
            }

            // 普通音符
            char upper = char.ToUpperInvariant(ch);
            if (KeyToPitch.TryGetValue(upper, out int pitch))
            {
                pos++;
                int dashes = CountDashes(text, ref pos);
                long dur = baseTick * (1 + dashes);
                notes.Add(CreateNote(pitch, currentTick, dur));
                currentTick += dur;
            }
            else
            {
                pos++; // 忽略无效字符
            }
        }
    }

    /// <summary>
    /// 解析和弦内容。和弦内所有音符同时开始（共享 chordStartTick），
    /// 但可通过 <c>-</c> 延长个别音符、嵌套括号产生先后关系。
    /// </summary>
    private static void ParseChord(string text, ref int pos, ref long currentTick,
        List<Note> notes, long baseTick)
    {
        long chordStart = currentTick;
        var chordNotes = new List<Note>();
        long innerTick = 0; // 和弦内部虚拟时钟，用于跟踪延长和嵌套组的时间位置

        while (pos < text.Length && text[pos] != ')')
        {
            char ch = text[pos];

            // 分隔符：忽略
            if (IsSeparator(ch))
            {
                pos++;
                continue;
            }

            // 和弦内空格：忽略
            if (ch == ' ')
            {
                pos++;
                continue;
            }

            // 和弦内延音：延长前一个和弦内音符
            if (ch == '-')
            {
                pos++;
                ExtendLastChordNote(chordNotes, baseTick);
                innerTick = MaxChordNoteEnd(chordNotes);
                continue;
            }

            // 和弦内嵌套八分音符组 [...]
            if (ch == '[')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, E, ']');
                if (pos < text.Length && text[pos] == ']')
                    pos++;
                continue;
            }

            // 和弦内嵌套十六分音符组 {...}
            if (ch == '{')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, S, '}');
                if (pos < text.Length && text[pos] == '}')
                    pos++;
                continue;
            }

            // 和弦内嵌套三连音组 <...>
            if (ch == '<')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, T, '>');
                if (pos < text.Length && text[pos] == '>')
                    pos++;
                continue;
            }

            // 和弦内嵌套子和弦 (...)
            if (ch == '(')
            {
                pos++;
                long subChordStart = innerTick;
                var subChordNotes = new List<Note>();
                long subInnerTick = 0;

                ParseChordContent(text, ref pos, ref subInnerTick, subChordNotes, baseTick, ')');
                if (pos < text.Length && text[pos] == ')')
                    pos++;

                foreach (var sn in subChordNotes)
                {
                    chordNotes.Add(CreateNote(sn.OriginalPitch, sn.StartTick + subChordStart, sn.DurationTick));
                }
                innerTick = Math.Max(innerTick, subChordStart + subInnerTick);
                continue;
            }

            // 普通音符（和弦内所有单音符同时开始于 offset 0）
            char upper = char.ToUpperInvariant(ch);
            if (KeyToPitch.TryGetValue(upper, out int pitch))
            {
                pos++;
                int dashes = CountDashes(text, ref pos);
                long dur = baseTick * (1 + dashes);
                chordNotes.Add(CreateNote(pitch, 0, dur));
                innerTick = Math.Max(innerTick, dur);
                continue;
            }

            // 无效字符
            pos++;
        }

        // 和弦后可能紧跟的 -
        int postDashes = CountDashes(text, ref pos);
        long extraTicks = postDashes * baseTick;
        long chordDuration = innerTick + extraTicks;

        // 将所有和弦音符 shift 到 chordStart，并加 extraTicks
        foreach (var cn in chordNotes)
        {
            notes.Add(CreateNote(cn.OriginalPitch, cn.StartTick + chordStart, cn.DurationTick + extraTicks));
        }

        currentTick = chordStart + chordDuration;
    }

    /// <summary>
    /// 解析和弦内嵌套的时值组（[...]、{...}、&lt;...&gt;）。
    /// 组内元素相对起始偏移由 <paramref name="innerTick"/> 跟踪。
    /// </summary>
    private static void ParseNestedInChord(string text, ref int pos, ref long innerTick,
        List<Note> chordNotes, long baseTick, char stopChar)
    {
        while (pos < text.Length && text[pos] != stopChar)
        {
            char ch = text[pos];

            if (IsSeparator(ch)) { pos++; continue; }

            if (ch == ' ')
            {
                pos++;
                innerTick += baseTick;
                continue;
            }

            if (ch == '-')
            {
                pos++;
                ExtendLastChordNote(chordNotes, baseTick);
                innerTick = MaxChordNoteEnd(chordNotes);
                continue;
            }

            // 递归嵌套
            if (ch == '(')
            {
                pos++;
                long subStart = innerTick;
                var subNotes = new List<Note>();
                long subTick = 0;
                ParseChordContent(text, ref pos, ref subTick, subNotes, baseTick, ')');
                if (pos < text.Length && text[pos] == ')') pos++;

                foreach (var sn in subNotes)
                {
                    chordNotes.Add(CreateNote(sn.OriginalPitch, sn.StartTick + subStart, sn.DurationTick));
                }
                innerTick = Math.Max(innerTick, subStart + subTick);
                continue;
            }

            if (ch == '[')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, E, ']');
                if (pos < text.Length && text[pos] == ']') pos++;
                continue;
            }

            if (ch == '{')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, S, '}');
                if (pos < text.Length && text[pos] == '}') pos++;
                continue;
            }

            if (ch == '<')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, T, '>');
                if (pos < text.Length && text[pos] == '>') pos++;
                continue;
            }

            // 普通音符（括号内顺序演奏）
            char upper = char.ToUpperInvariant(ch);
            if (KeyToPitch.TryGetValue(upper, out int pitch))
            {
                pos++;
                int dashes = CountDashes(text, ref pos);
                long dur = baseTick * (1 + dashes);
                chordNotes.Add(CreateNote(pitch, innerTick, dur));
                innerTick += dur;
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// 解析和弦内容（用于子和弦），收集到 chordNotes 列表中。
    /// 与 ParseChord 类似，但直接操作传入的列表，使用相对偏移。
    /// </summary>
    private static void ParseChordContent(string text, ref int pos, ref long innerTick,
        List<Note> chordNotes, long baseTick, char stopChar)
    {
        while (pos < text.Length && text[pos] != stopChar)
        {
            char ch = text[pos];

            if (IsSeparator(ch)) { pos++; continue; }
            if (ch == ' ') { pos++; continue; }

            if (ch == '-')
            {
                pos++;
                ExtendLastChordNote(chordNotes, baseTick);
                innerTick = MaxChordNoteEnd(chordNotes);
                continue;
            }

            if (ch == '[')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, E, ']');
                if (pos < text.Length && text[pos] == ']') pos++;
                continue;
            }

            if (ch == '{')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, S, '}');
                if (pos < text.Length && text[pos] == '}') pos++;
                continue;
            }

            if (ch == '<')
            {
                pos++;
                ParseNestedInChord(text, ref pos, ref innerTick, chordNotes, T, '>');
                if (pos < text.Length && text[pos] == '>') pos++;
                continue;
            }

            if (ch == '(')
            {
                pos++;
                long subStart = innerTick;
                var subNotes = new List<Note>();
                long subTick = 0;
                ParseChordContent(text, ref pos, ref subTick, subNotes, baseTick, ')');
                if (pos < text.Length && text[pos] == ')') pos++;

                foreach (var sn in subNotes)
                {
                    chordNotes.Add(CreateNote(sn.OriginalPitch, sn.StartTick + subStart, sn.DurationTick));
                }
                innerTick = Math.Max(innerTick, subStart + subTick);
                continue;
            }

            char upper = char.ToUpperInvariant(ch);
            if (KeyToPitch.TryGetValue(upper, out int pitch))
            {
                pos++;
                int dashes = CountDashes(text, ref pos);
                long dur = baseTick * (1 + dashes);
                chordNotes.Add(CreateNote(pitch, 0, dur));
                innerTick = Math.Max(innerTick, dur);
            }
            else
            {
                pos++;
            }
        }
    }

    // ================================================================
    //  辅助方法
    // ================================================================

    private static bool IsSeparator(char ch)
        => ch == '\\' || ch == '/' || ch == '\r' || ch == '\n' || ch == '~';

    /// <summary>消费连续的 <c>-</c>，返回个数。</summary>
    private static int CountDashes(string text, ref int pos)
    {
        int count = 0;
        while (pos < text.Length && text[pos] == '-')
        {
            count++;
            pos++;
        }
        return count;
    }

    /// <summary>
    /// 找到所有结束 tick 等于 <paramref name="tick"/> 的音符，
    /// 将其 DurationTick 延长 <paramref name="amount"/>。返回是否找到。
    /// </summary>
    private static bool ExtendNotesEndingAt(List<Note> notes, long tick, long amount)
    {
        bool found = false;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            long endTick = notes[i].StartTick + notes[i].DurationTick;
            if (endTick == tick)
            {
                notes[i] = CreateNote(notes[i].OriginalPitch, notes[i].StartTick, notes[i].DurationTick + amount);
                found = true;
            }
            else if (endTick < tick)
            {
                break;
            }
        }
        return found;
    }

    /// <summary>返回和弦内所有音符的最远结束偏移。</summary>
    private static long MaxChordNoteEnd(List<Note> chordNotes)
    {
        long maxEnd = 0;
        foreach (var n in chordNotes)
            maxEnd = Math.Max(maxEnd, n.StartTick + n.DurationTick);
        return maxEnd;
    }

    /// <summary>延长和弦内最后一个添加的音符。</summary>
    private static void ExtendLastChordNote(List<Note> chordNotes, long amount)
    {
        if (chordNotes.Count == 0) return;
        var last = chordNotes[^1];
        chordNotes[^1] = CreateNote(last.OriginalPitch, last.StartTick, last.DurationTick + amount);
    }

    private static Note CreateNote(int pitch, long startTick, long durationTick)
    {
        return new Note
        {
            OriginalPitch = pitch,
            StartTick = startTick,
            DurationTick = durationTick,
            Channel = 0,
            Velocity = 100,
        };
    }

    // ================================================================
    //  结果构建
    // ================================================================

    private static MidiParser.ParseResult BuildResult(List<Note> notes, long maxEndTick)
    {
        int mpqn = (int)Math.Round(60_000_000.0 / DefaultBpm);
        var tempoManager = new TempoManager(DefaultPpq, new[] { (0L, mpqn) });
        var timeSignatureManager = new TimeSignatureManager(DefaultPpq, new[] { (0L, 4, 4) });

        foreach (var n in notes)
        {
            n.Start = tempoManager.TickToSeconds(n.StartTick);
            double end = tempoManager.TickToSeconds(n.StartTick + n.DurationTick);
            n.Duration = Math.Max(0.02, end - n.Start);
        }

        MidiParser.ApplyTranspose(notes, 0, Instruments.Default);
        double totalDuration = tempoManager.TickToSeconds(maxEndTick);

        var track = new MidiTrack
        {
            Index = 0,
            Name = "字母谱",
            Notes = notes,
            ColorArgb = 0xFF5AC8F0,
        };

        return new MidiParser.ParseResult
        {
            Tracks = new List<MidiTrack> { track },
            TempoManager = tempoManager,
            TimeSignatureManager = timeSignatureManager,
            MaxEndTick = maxEndTick,
            TotalDuration = totalDuration,
            FileName = "字母谱导入",
        };
    }
}
