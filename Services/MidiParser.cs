using System;
using System.Collections.Generic;
using System.Linq;
using GenshinLyrePlayer.Models;
using NAudio.Midi;

namespace GenshinLyrePlayer.Services;

public static class MidiParser
{
    public sealed class ParseResult
    {
        public List<Note> Notes { get; init; } = new();
        public double TotalDuration { get; init; }
        public string FileName { get; init; } = string.Empty;
    }

    /// <summary>自动移调的结果：最佳乐器组 + 最佳移调 + 可演奏音符数。</summary>
    public readonly record struct TransposeResult(InstrumentGroup Group, int Shift, int Score);

    /// <summary>从 MIDI 文件中提取所有音符，按时间升序返回。</summary>
    public static ParseResult Parse(string path)
    {
        // strictChecking = false，尽量宽容地读文件。
        var midiFile = new MidiFile(path, false);
        int ppq = midiFile.DeltaTicksPerQuarterNote;
        if (ppq <= 0) ppq = 480;

        // 1. 收集所有 tempo 变化点（跨全部 track，按绝对 tick 合并）。
        var tempos = new List<(long Tick, int Mpqn)>();
        foreach (var track in midiFile.Events)
        {
            foreach (var e in track)
            {
                if (e is TempoEvent te)
                {
                    tempos.Add((te.AbsoluteTime, te.MicrosecondsPerQuarterNote));
                }
            }
        }
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        // 默认 120 BPM（500000 µs/四分音符）。
        if (tempos.Count == 0 || tempos[0].Tick > 0)
        {
            tempos.Insert(0, (0, 500000));
        }

        // 2. 将绝对 tick 转换为秒（考虑中途 tempo 变化）。
        double TickToSeconds(long tick)
        {
            if (tick <= 0) return 0;
            double seconds = 0;
            long prevTick = 0;
            int mpqn = tempos[0].Mpqn;
            for (int i = 0; i < tempos.Count; i++)
            {
                var (t, m) = tempos[i];
                if (t >= tick) break;
                seconds += (t - prevTick) * (mpqn / 1_000_000.0) / ppq;
                prevTick = t;
                mpqn = m;
            }
            seconds += (tick - prevTick) * (mpqn / 1_000_000.0) / ppq;
            return seconds;
        }

        // 3. 遍历所有 NoteOnEvent，拿到 start/length 并换算成秒。
        var result = new List<Note>();
        double total = 0;

        foreach (var track in midiFile.Events)
        {
            foreach (var e in track)
            {
                if (e is NoteOnEvent noteOn && noteOn.OffEvent != null && noteOn.Velocity > 0)
                {
                    long startTick = noteOn.AbsoluteTime;
                    long endTick = noteOn.OffEvent.AbsoluteTime;
                    if (endTick < startTick) endTick = startTick;

                    double start = TickToSeconds(startTick);
                    double end = TickToSeconds(endTick);
                    double dur = end - start;
                    if (dur < 0.02) dur = 0.02;

                    result.Add(new Note
                    {
                        OriginalPitch = noteOn.NoteNumber,
                        Start = start,
                        Duration = dur,
                        Channel = noteOn.Channel,
                        Velocity = noteOn.Velocity,
                    });

                    if (start + dur > total) total = start + dur;
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        ApplyTranspose(result, 0, Instruments.Default);

        return new ParseResult
        {
            Notes = result,
            TotalDuration = total,
            FileName = System.IO.Path.GetFileName(path),
        };
    }

    /// <summary>
    /// 对所有音符应用半音数移调 + 指定乐器组；更新 EffectivePitch / Supported / Key。
    /// </summary>
    public static void ApplyTranspose(IEnumerable<Note> notes, int semitones, InstrumentGroup group)
    {
        foreach (var n in notes)
        {
            int p = n.OriginalPitch + semitones;
            n.EffectivePitch = p;
            n.Key = group.GetKey(p);
            n.Supported = n.Key.HasValue;
        }
    }

    /// <summary>
    /// 在 [-36,+36] 范围内寻找让指定乐器组可演奏音符数最多的移调值。
    /// 若得分相同，优先移调幅度更小的结果。
    /// </summary>
    public static int FindBestTranspose(IList<Note> notes, InstrumentGroup group)
    {
        return FindBestTransposeWithScore(notes, group).Shift;
    }

    /// <summary>
    /// 与 <see cref="FindBestTranspose(IList{Note}, InstrumentGroup)"/> 相同，但同时返回得分。
    /// </summary>
    public static TransposeResult FindBestTransposeWithScore(IList<Note> notes, InstrumentGroup group)
    {
        if (notes.Count == 0) return new TransposeResult(group, 0, 0);

        int bestShift = 0;
        int bestScore = -1;
        int bestAbs = int.MaxValue;

        for (int shift = -36; shift <= 36; shift++)
        {
            int score = 0;
            foreach (var n in notes)
            {
                if (group.IsSupported(n.OriginalPitch + shift)) score++;
            }

            if (score > bestScore || (score == bestScore && Math.Abs(shift) < bestAbs))
            {
                bestScore = score;
                bestShift = shift;
                bestAbs = Math.Abs(shift);
            }
        }
        return new TransposeResult(group, bestShift, bestScore);
    }

    /// <summary>
    /// 跨多个乐器组寻找最佳 (组, 移调) 组合。
    /// 优先级：
    ///   1. 可演奏音符数更多者胜出；
    ///   2. 若可演奏数相同，优先保留 <paramref name="preferred"/>；
    ///   3. 再相同，优先移调幅度更小者。
    /// 这样「如果当前组没有合适移调，则自动切换到其他组」的语义得到保证。
    /// </summary>
    public static TransposeResult FindBestTransposeAcrossGroups(
        IList<Note> notes,
        IEnumerable<InstrumentGroup> groups,
        InstrumentGroup? preferred = null)
    {
        TransposeResult? best = null;
        foreach (var g in groups)
        {
            var r = FindBestTransposeWithScore(notes, g);
            if (best is null)
            {
                best = r;
                continue;
            }
            var cur = best.Value;
            bool take =
                r.Score > cur.Score
                || (r.Score == cur.Score && preferred != null && r.Group == preferred && cur.Group != preferred)
                || (r.Score == cur.Score && (preferred == null || (cur.Group != preferred && r.Group != preferred))
                    && Math.Abs(r.Shift) < Math.Abs(cur.Shift));
            if (take) best = r;
        }
        return best ?? new TransposeResult(preferred ?? Instruments.Default, 0, 0);
    }
}
