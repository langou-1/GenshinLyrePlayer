using System;
using System.Collections.Generic;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 解析字母谱文本并生成与 <see cref="MidiParser.ParseResult"/> 兼容的轨道数据。
///
/// 支持四种格式，自动检测：
///
/// <b>简易格式</b>（无时值符号）：
///   每个字母占一拍，括号内为和弦。例如：<c>EWWQWQQU</c>、<c>(DACN)SSAAFDS</c>
///
/// <b>高级格式</b>（含时值符号 <c>~ . ; : ,</c>）：
///   音符/和弦后跟时值符号指定持续时间，多个时值符号可叠加。
///   时值关系：<c>~ = .. = ;;;; = :::::::: = ,,,,,,</c>
///   琶音：<c>(琶BADGQ)</c> 表示快速依次弹奏。
///   例如：<c>(NJE).Q.H~(JE).Q.H~</c>
///
/// <b>斜杠格式</b>（<c>/</c> 分隔拍子）：
///   <c>/</c> 将文本划分为等长拍子，拍内音符按数量平均分配时值。
///   例如：<c>(SW) H /N (HH)Q/(ZG)H(AG)F/</c>
///
/// <b>短横线格式</b>（<c>-</c> 延长音符）：
///   每个音符/和弦占一个时间槽（十六分音符）。<c>-</c> 延长前一个音符一个时间槽；
///   无前置音符的 <c>-</c> 视为休止。<c>[...]</c> 内的音符在一个时间槽内快速弹奏。
///   例如：<c>(VAH)--D-G-(VAH)--H-E-</c>
///
/// 键位映射（风物之诗琴 / 镜花之琴 C 大调 21 键）：
///   高音: Q(C5) W(D5) E(E5) R(F5) T(G5) Y(A5) U(B5)
///   中音: A(C4) S(D4) D(E4) F(F4) G(G4) H(A4) J(B4)
///   低音: Z(C3) X(D3) C(E3) V(F3) B(G3) N(A3) M(B3)
/// </summary>
public static class LetterScoreParser
{
    // ===== 常量 =====

    private const int DefaultPpq = 480;
    private const double DefaultBpm = 120;

    // ----- 高级格式时值 -----
    private const long TickTilde = 240;
    private const long TickDot   = 120;
    private const long TickSemi  = 60;
    private const long TickColon = 30;
    private const long TickComma = 40;
    private const long SimpleDefaultTicks   = TickTilde;
    private const long AdvancedDefaultTicks = TickDot;
    private const long ArpeggioStep = 30;

    // ----- 斜杠格式 -----
    private const long SlashBeatTicks = 480;

    // ----- 短横线格式 -----
    /// <summary>短横线格式中每个时间槽的 tick 数（十六分音符）。</summary>
    private const long DashSlotTicks = 120;
    /// <summary><c>[...]</c> 装饰音组内每个音符之间的 tick 间隔。</summary>
    private const long BracketStepTicks = 20;

    /// <summary>字母 → MIDI 音高。</summary>
    private static readonly Dictionary<char, int> KeyToPitch = new()
    {
        { 'Q', 72 }, { 'W', 74 }, { 'E', 76 }, { 'R', 77 }, { 'T', 79 }, { 'Y', 81 }, { 'U', 83 },
        { 'A', 60 }, { 'S', 62 }, { 'D', 64 }, { 'F', 65 }, { 'G', 67 }, { 'H', 69 }, { 'J', 71 },
        { 'Z', 48 }, { 'X', 50 }, { 'C', 52 }, { 'V', 53 }, { 'B', 55 }, { 'N', 57 }, { 'M', 59 },
    };

    // ===== 格式枚举 =====

    private enum Format { Simple, Advanced, Slash, Dash }

    // ===== 公共 API =====

    public static MidiParser.ParseResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("字母谱文本不能为空");

        var fmt = DetectFormat(text);
        var notes = new List<Note>();
        long currentTick = 0;

        switch (fmt)
        {
            case Format.Dash:
                ParseDashFormat(text, notes, ref currentTick);
                break;
            case Format.Slash:
                ParseSlashFormat(text, notes, ref currentTick);
                break;
            case Format.Advanced:
                ParseAdvancedOrSimple(text, notes, ref currentTick, advanced: true);
                break;
            default:
                ParseAdvancedOrSimple(text, notes, ref currentTick, advanced: false);
                break;
        }

        if (notes.Count == 0)
            throw new ArgumentException(
                "字母谱中没有找到有效的音符（有效字母: Q W E R T Y U A S D F G H J Z X C V B N M）");

        return BuildResult(notes, currentTick);
    }

    // ===== 格式检测 =====

    private static Format DetectFormat(string text)
    {
        if (text.Contains('~'))
            return Format.Advanced;
        if (text.Contains('/'))
            return Format.Slash;
        if (text.Contains('-'))
            return Format.Dash;
        return Format.Simple;
    }

    // ================================================================
    //  短横线格式
    // ================================================================

    /// <summary>短横线格式 token 类型。</summary>
    private const int TK_DASH = 0;    // '-' 延长/休止
    private const int TK_NOTE = 1;    // 单音符或和弦
    private const int TK_BRACKET = 2; // [...] 装饰音组

    /// <summary>一个 token 的数据。</summary>
    private readonly struct DToken
    {
        public readonly int Type;
        /// <summary>TK_NOTE: 音高列表（1 个=单音符，多个=和弦）。</summary>
        public readonly List<int>? Pitches;
        /// <summary>TK_BRACKET: 装饰音组内的子 token 列表（每个子 token 是一个音高列表）。</summary>
        public readonly List<List<int>>? SubTokens;

        public DToken(int type, List<int>? pitches = null, List<List<int>>? subTokens = null)
        {
            Type = type;
            Pitches = pitches;
            SubTokens = subTokens;
        }
    }

    /// <summary>
    /// 解析短横线格式。
    /// 两遍处理：先 tokenize，再根据 <c>-</c> 延长关系生成音符。
    /// </summary>
    private static void ParseDashFormat(string text, List<Note> notes, ref long currentTick)
    {
        var tokens = TokenizeDash(text);
        int i = 0;

        while (i < tokens.Count)
        {
            var tok = tokens[i];

            if (tok.Type == TK_DASH)
            {
                // 但在 generate 阶段不应该走到这里，因为 note/chord/bracket 已经消费了后续 dash。
                // 作为保底：独立 dash = 休止
                currentTick += DashSlotTicks;
                i++;
            }
            else if (tok.Type == TK_NOTE)
            {
                int dashes = CountFollowingDashes(tokens, i);
                long dur = DashSlotTicks * (1 + dashes);

                foreach (var p in tok.Pitches!)
                    notes.Add(CreateNote(p, currentTick, dur));
                currentTick += dur;
                i += 1 + dashes;
            }
            else if (tok.Type == TK_BRACKET)
            {
                int dashes = CountFollowingDashes(tokens, i);
                long totalDur = DashSlotTicks * (1 + dashes);
                EmitBracketGroup(notes, tok.SubTokens!, ref currentTick, totalDur);
                i += 1 + dashes;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>tokenize 短横线格式文本。</summary>
    private static List<DToken> TokenizeDash(string text)
    {
        var tokens = new List<DToken>();
        int pos = 0;

        while (pos < text.Length)
        {
            char ch = text[pos];

            // 跳过空白
            if (char.IsWhiteSpace(ch))
            {
                pos++;
                continue;
            }

            if (ch == '-')
            {
                tokens.Add(new DToken(TK_DASH));
                pos++;
            }
            else if (ch == '(')
            {
                pos++; // skip '('
                var pitches = CollectPitchesUntilClose(text, ref pos);
                if (pitches.Count > 0)
                    tokens.Add(new DToken(TK_NOTE, pitches));
            }
            else if (ch == '[')
            {
                pos++; // skip '['
                var subTokens = CollectBracketSubTokens(text, ref pos);
                if (subTokens.Count > 0)
                    tokens.Add(new DToken(TK_BRACKET, subTokens: subTokens));
            }
            else
            {
                char upper = char.ToUpperInvariant(ch);
                if (KeyToPitch.TryGetValue(upper, out int pitch))
                    tokens.Add(new DToken(TK_NOTE, new List<int> { pitch }));
                // 忽略无效字符
                pos++;
            }
        }

        return tokens;
    }

    /// <summary>收集 <c>[</c> 到 <c>]</c> 之间的子 token（含和弦和单音符）。</summary>
    private static List<List<int>> CollectBracketSubTokens(string text, ref int pos)
    {
        var subTokens = new List<List<int>>();

        while (pos < text.Length && text[pos] != ']')
        {
            char ch = text[pos];

            if (char.IsWhiteSpace(ch))
            {
                pos++;
                continue;
            }

            if (ch == '(')
            {
                pos++; // skip '('
                var pitches = CollectPitchesUntilClose(text, ref pos);
                if (pitches.Count > 0)
                    subTokens.Add(pitches);
            }
            else
            {
                char upper = char.ToUpperInvariant(ch);
                if (KeyToPitch.TryGetValue(upper, out int pitch))
                    subTokens.Add(new List<int> { pitch });
                pos++;
            }
        }

        if (pos < text.Length && text[pos] == ']') pos++; // skip ']'
        return subTokens;
    }

    /// <summary>计算 token[index] 之后连续 <c>TK_DASH</c> 的个数。</summary>
    private static int CountFollowingDashes(List<DToken> tokens, int index)
    {
        int count = 0;
        for (int j = index + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Type != TK_DASH) break;
            count++;
        }
        return count;
    }

    /// <summary>输出装饰音组（<c>[...]</c>）内的音符：在 <paramref name="totalDuration"/> 内快速连奏。</summary>
    private static void EmitBracketGroup(
        List<Note> notes, List<List<int>> subTokens, ref long currentTick, long totalDuration)
    {
        if (subTokens.Count == 0) return;

        long step = subTokens.Count > 1
            ? Math.Min(BracketStepTicks, totalDuration / subTokens.Count)
            : totalDuration;

        long offset = 0;
        for (int n = 0; n < subTokens.Count; n++)
        {
            bool isLast = n == subTokens.Count - 1;
            long dur = isLast ? Math.Max(1, totalDuration - offset) : step;

            foreach (var p in subTokens[n])
                notes.Add(CreateNote(p, currentTick + offset, dur));

            if (!isLast) offset += step;
        }

        currentTick += totalDuration;
    }

    // ================================================================
    //  斜杠格式
    // ================================================================

    private static void ParseSlashFormat(string text, List<Note> notes, ref long currentTick)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        var beats = text.Split('/');

        foreach (var beat in beats)
        {
            var tokens = TokenizeBeat(beat);

            if (tokens.Count == 0)
            {
                currentTick += SlashBeatTicks;
                continue;
            }

            long tickPerToken = SlashBeatTicks / tokens.Count;
            long remainder = SlashBeatTicks - tickPerToken * tokens.Count;

            for (int i = 0; i < tokens.Count; i++)
            {
                long dur = tickPerToken + (i == tokens.Count - 1 ? remainder : 0);
                var token = tokens[i];

                foreach (var pitch in token)
                    notes.Add(CreateNote(pitch, currentTick, dur));
                currentTick += dur;
            }
        }
    }

    private static List<List<int>> TokenizeBeat(string segment)
    {
        var tokens = new List<List<int>>();
        int pos = 0;

        while (pos < segment.Length)
        {
            char ch = segment[pos];

            if (char.IsWhiteSpace(ch)) { pos++; continue; }

            if (ch == '(')
            {
                pos++;
                var pitches = CollectPitchesUntilClose(segment, ref pos);
                if (pitches.Count > 0)
                    tokens.Add(pitches);
            }
            else
            {
                char upper = char.ToUpperInvariant(ch);
                if (KeyToPitch.TryGetValue(upper, out int pitch))
                    tokens.Add(new List<int> { pitch });
                pos++;
            }
        }
        return tokens;
    }

    // ================================================================
    //  简易 / 高级格式
    // ================================================================

    private static void ParseAdvancedOrSimple(string text, List<Note> notes, ref long currentTick, bool advanced)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;

            char ch = text[pos];

            if (advanced && IsDurationChar(ch))
            {
                currentTick += ConsumeDuration(text, ref pos, 0);
                continue;
            }

            if (ch == '(')
            {
                pos++;
                if (pos < text.Length && text[pos] == '琶')
                {
                    pos++;
                    var pitches = CollectPitchesUntilClose(text, ref pos);
                    long dur = advanced
                        ? ConsumeDuration(text, ref pos, AdvancedDefaultTicks)
                        : SimpleDefaultTicks;
                    EmitArpeggio(notes, pitches, ref currentTick, dur);
                }
                else
                {
                    var pitches = CollectPitchesUntilClose(text, ref pos);
                    long dur = advanced
                        ? ConsumeDuration(text, ref pos, AdvancedDefaultTicks)
                        : SimpleDefaultTicks;
                    EmitChord(notes, pitches, ref currentTick, dur);
                }
            }
            else
            {
                char upper = char.ToUpperInvariant(ch);
                if (KeyToPitch.TryGetValue(upper, out int pitch))
                {
                    pos++;
                    long dur = advanced
                        ? ConsumeDuration(text, ref pos, AdvancedDefaultTicks)
                        : SimpleDefaultTicks;
                    notes.Add(CreateNote(pitch, currentTick, dur));
                    currentTick += dur;
                }
                else
                {
                    pos++;
                }
            }
        }
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

    // ================================================================
    //  高级格式时值辅助
    // ================================================================

    private static bool IsDurationChar(char ch)
        => ch == '~' || ch == '.' || ch == ';' || ch == ':' || ch == ',';

    private static long DurationCharToTicks(char ch) => ch switch
    {
        '~' => TickTilde, '.' => TickDot, ';' => TickSemi,
        ':' => TickColon, ',' => TickComma, _ => 0,
    };

    private static long ConsumeDuration(string text, ref int pos, long defaultTicks)
    {
        long total = 0;
        bool found = false;
        while (pos < text.Length && IsDurationChar(text[pos]))
        {
            total += DurationCharToTicks(text[pos]);
            found = true;
            pos++;
        }
        return found ? total : defaultTicks;
    }

    // ================================================================
    //  通用解析辅助
    // ================================================================

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }

    private static List<int> CollectPitchesUntilClose(string text, ref int pos)
    {
        var pitches = new List<int>();
        while (pos < text.Length && text[pos] != ')')
        {
            char upper = char.ToUpperInvariant(text[pos]);
            if (KeyToPitch.TryGetValue(upper, out int pitch))
                pitches.Add(pitch);
            pos++;
        }
        if (pos < text.Length && text[pos] == ')') pos++;
        return pitches;
    }

    // ================================================================
    //  音符生成
    // ================================================================

    private static void EmitChord(List<Note> notes, List<int> pitches, ref long currentTick, long duration)
    {
        if (pitches.Count == 0) return;
        foreach (var p in pitches)
            notes.Add(CreateNote(p, currentTick, duration));
        currentTick += duration;
    }

    private static void EmitArpeggio(List<Note> notes, List<int> pitches, ref long currentTick, long totalDuration)
    {
        if (pitches.Count == 0) return;
        long step = pitches.Count > 1
            ? Math.Min(ArpeggioStep, totalDuration / pitches.Count)
            : totalDuration;

        for (int n = 0; n < pitches.Count; n++)
        {
            bool isLast = n == pitches.Count - 1;
            long noteStart = currentTick + n * step;
            long noteDur = isLast
                ? Math.Max(1, totalDuration - (pitches.Count - 1) * step)
                : step;
            notes.Add(CreateNote(pitches[n], noteStart, noteDur));
        }
        currentTick += totalDuration;
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
}
