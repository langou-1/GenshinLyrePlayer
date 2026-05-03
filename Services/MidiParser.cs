using System;
using System.Collections.Generic;
using System.Linq;
using GenshinLyrePlayer.Models;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Note = GenshinLyrePlayer.Models.Note;
using MidiNote = Melanchall.DryWetMidi.Interaction.Note;

namespace GenshinLyrePlayer.Services;

public static class MidiParser
{
    public sealed class ParseResult
    {
        public List<Note> Notes { get; init; } = new();
        public double TotalDuration { get; init; }
        public string FileName { get; init; } = string.Empty;
    }

    /// <summary>自动移调的结果：最佳乐器 + 最佳移调 + 可演奏音符数。</summary>
    public readonly record struct TransposeResult(Instrument Instrument, int Shift, int Score);

    /// <summary>从 MIDI 文件中提取所有音符，按时间升序返回。</summary>
    public static ParseResult Parse(string path)
    {
        var midiFile = MidiFile.Read(path);
        var tempoMap = midiFile.GetTempoMap();
        var midiNotes = midiFile.GetNotes();

        var result = new List<Note>();
        double total = 0;

        foreach (var n in midiNotes)
        {
            var startMetric = n.TimeAs<MetricTimeSpan>(tempoMap);
            var lengthMetric = n.LengthAs<MetricTimeSpan>(tempoMap);
            double start = startMetric.TotalMicroseconds / 1_000_000.0;
            double dur = lengthMetric.TotalMicroseconds / 1_000_000.0;
            if (dur < 0.02) dur = 0.02;

            result.Add(new Note
            {
                OriginalPitch = n.NoteNumber,
                Start = start,
                Duration = dur,
                Channel = n.Channel,
                Velocity = n.Velocity,
            });

            if (start + dur > total) total = start + dur;
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
    /// 对所有音符应用半音数移调 + 指定乐器；更新 EffectivePitch / Supported / Key。
    /// </summary>
    public static void ApplyTranspose(IEnumerable<Note> notes, int semitones, Instrument instrument)
    {
        foreach (var n in notes)
        {
            int p = n.OriginalPitch + semitones;
            n.EffectivePitch = p;
            n.Key = instrument.GetKey(p);
            n.Supported = n.Key.HasValue;
        }
    }

    /// <summary>
    /// 在 [-36,+36] 范围内寻找让指定乐器可演奏音符数最多的移调值。
    /// 若得分相同，优先移调幅度更小的结果。
    /// </summary>
    public static int FindBestTranspose(IList<Note> notes, Instrument instrument)
    {
        return FindBestTransposeWithScore(notes, instrument).Shift;
    }

    /// <summary>
    /// 与 <see cref="FindBestTranspose(IList{Note}, Instrument)"/> 相同，但同时返回得分。
    /// </summary>
    public static TransposeResult FindBestTransposeWithScore(IList<Note> notes, Instrument instrument)
    {
        if (notes.Count == 0) return new TransposeResult(instrument, 0, 0);

        int bestShift = 0;
        int bestScore = -1;
        int bestAbs = int.MaxValue;

        for (int shift = -36; shift <= 36; shift++)
        {
            int score = 0;
            foreach (var n in notes)
            {
                if (instrument.IsSupported(n.OriginalPitch + shift)) score++;
            }

            if (score > bestScore || (score == bestScore && Math.Abs(shift) < bestAbs))
            {
                bestScore = score;
                bestShift = shift;
                bestAbs = Math.Abs(shift);
            }
        }
        return new TransposeResult(instrument, bestShift, bestScore);
    }

    /// <summary>
    /// 跨多个乐器寻找最佳 (乐器, 移调) 组合。
    /// 优先级：
    ///   1. 可演奏音符数更多者胜出；
    ///   2. 若可演奏数相同，优先保留 <paramref name="preferred"/>；
    ///   3. 再相同，优先移调幅度更小者。
    /// 这样「如果当前乐器没有合适移调，则自动切换到其他乐器」的语义得到保证。
    /// </summary>
    public static TransposeResult FindBestTransposeAcrossInstruments(
        IList<Note> notes,
        IEnumerable<Instrument> instruments,
        Instrument? preferred = null)
    {
        TransposeResult? best = null;
        foreach (var inst in instruments)
        {
            var r = FindBestTransposeWithScore(notes, inst);
            if (best is null)
            {
                best = r;
                continue;
            }
            var cur = best.Value;
            bool take =
                r.Score > cur.Score
                || (r.Score == cur.Score && preferred != null && r.Instrument == preferred && cur.Instrument != preferred)
                || (r.Score == cur.Score && (preferred == null || (cur.Instrument != preferred && r.Instrument != preferred))
                    && Math.Abs(r.Shift) < Math.Abs(cur.Shift));
            if (take) best = r;
        }
        return best ?? new TransposeResult(preferred ?? Instruments.Default, 0, 0);
    }
}
