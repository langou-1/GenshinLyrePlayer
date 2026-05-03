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
        public List<MidiTrack> Tracks { get; init; } = new();
        public double TotalDuration { get; init; }
        public string FileName { get; init; } = string.Empty;

        /// <summary>整首曲子的曲速管理器。在用户编辑 BPM 后，所有 Note 的秒时间会基于它重算。</summary>
        public TempoManager TempoManager { get; init; } = new(480);

        /// <summary>所有轨道里最大的 (StartTick + DurationTick)，用于在 BPM 变更后重算 <see cref="TotalDuration"/>。</summary>
        public long MaxEndTick { get; init; }

        /// <summary>所有轨道合并后的音符（按开始时间排序）。</summary>
        public IEnumerable<Note> AllNotes => Tracks.SelectMany(t => t.Notes);
    }

    /// <summary>自动移调的结果：最佳乐器组 + 最佳移调 + 可演奏音符数。</summary>
    public readonly record struct TransposeResult(InstrumentGroup Group, int Shift, int Score);

    /// <summary>缩略图配色盘（ARGB，带 0xFF 不透明 alpha）。</summary>
    private static readonly uint[] TrackColors =
    {
        0xFF5AC8F0, // 青蓝
        0xFFF0C05A, // 橙黄
        0xFF8EE05A, // 草绿
        0xFFF08AB0, // 粉红
        0xFFB388FF, // 紫
        0xFFFF8A65, // 橘红
        0xFF4DD0E1, // 青绿
        0xFFF06292, // 洋红
        0xFFAED581, // 浅绿
        0xFFFFD54F, // 金
        0xFF9575CD, // 薰衣草
        0xFF64B5F6, // 天蓝
    };

    /// <summary>
    /// 从 MIDI 文件中按"轨"拆分音符。
    /// - 对于多轨 MIDI (type 1)，每个 MIDI track 对应一条轨道；
    /// - 对于单轨 MIDI (type 0)，若出现多个通道则按通道拆分。
    /// 没有任何音符的 track（纯 meta）会被忽略。
    ///
    /// 重构后：每个 <see cref="Note"/> 同时保留 <see cref="Note.StartTick"/> /
    /// <see cref="Note.DurationTick"/> 与 <see cref="Note.Start"/> / <see cref="Note.Duration"/>(秒)。
    /// 后者通过 <see cref="TempoManager"/> 折算得到，曲速被编辑后可被重算。
    /// </summary>
    public static ParseResult Parse(string path)
    {
        var midiFile = new MidiFile(path, false);
        int ppq = midiFile.DeltaTicksPerQuarterNote;
        if (ppq <= 0) ppq = 480;

        // 1. 收集全局 tempo 变化点 → 构造 TempoManager
        var rawTempos = new List<(long Tick, int Mpqn)>();
        foreach (var track in midiFile.Events)
        {
            foreach (var e in track)
            {
                if (e is TempoEvent te)
                    rawTempos.Add((te.AbsoluteTime, te.MicrosecondsPerQuarterNote));
            }
        }
        var tempoManager = new TempoManager(ppq, rawTempos);

        // 2. 每个 track 独立收集 Name + Notes（先只填 tick 字段，秒时间稍后由 TempoManager 计算）
        var tracks = new List<MidiTrack>();
        long maxEndTick = 0;

        for (int t = 0; t < midiFile.Events.Tracks; t++)
        {
            var trackEvents = midiFile.Events[t];
            string? trackName = null;
            var notes = new List<Note>();

            foreach (var e in trackEvents)
            {
                if (e is TextEvent te &&
                    te.MetaEventType == MetaEventType.SequenceTrackName &&
                    string.IsNullOrEmpty(trackName))
                {
                    trackName = te.Text;
                }
                else if (e is NoteOnEvent noteOn && noteOn.OffEvent != null && noteOn.Velocity > 0)
                {
                    long startTick = noteOn.AbsoluteTime;
                    long endTick = noteOn.OffEvent.AbsoluteTime;
                    if (endTick < startTick) endTick = startTick;
                    long durTick = endTick - startTick;

                    notes.Add(new Note
                    {
                        OriginalPitch = noteOn.NoteNumber,
                        StartTick = startTick,
                        DurationTick = durTick,
                        Channel = noteOn.Channel,
                        Velocity = noteOn.Velocity,
                    });

                    if (endTick > maxEndTick) maxEndTick = endTick;
                }
            }

            if (notes.Count == 0) continue;
            notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));

            int idx = tracks.Count;
            tracks.Add(new MidiTrack
            {
                Index = idx,
                Name = string.IsNullOrWhiteSpace(trackName) ? $"Track {idx + 1}" : trackName!.Trim(),
                Notes = notes,
                ColorArgb = TrackColors[idx % TrackColors.Length],
            });
        }

        // 单轨多通道：按 channel 拆分
        if (tracks.Count == 1)
        {
            var only = tracks[0];
            var channels = only.Notes.Select(n => n.Channel).Distinct().OrderBy(c => c).ToList();
            if (channels.Count > 1)
            {
                tracks.Clear();
                foreach (var ch in channels)
                {
                    var chNotes = only.Notes.Where(n => n.Channel == ch).ToList();
                    int idx = tracks.Count;
                    tracks.Add(new MidiTrack
                    {
                        Index = idx,
                        Name = ch == 10 ? $"Drums (Ch {ch})" : $"Channel {ch}",
                        Notes = chNotes,
                        ColorArgb = TrackColors[idx % TrackColors.Length],
                    });
                }
            }
        }

        // 把 tick → 秒 折算写到每个 Note 上
        RecomputeNoteTimes(tracks, tempoManager);

        // 应用默认映射（无移调）
        foreach (var tr in tracks)
            ApplyTranspose(tr.Notes, 0, Instruments.Default);

        double total = tempoManager.TickToSeconds(maxEndTick);

        return new ParseResult
        {
            Tracks = tracks,
            TempoManager = tempoManager,
            MaxEndTick = maxEndTick,
            TotalDuration = total,
            FileName = System.IO.Path.GetFileName(path),
        };
    }

    /// <summary>
    /// 用给定的 <see cref="TempoManager"/> 把每个 <see cref="Note"/> 的 tick 字段折算到秒。
    /// 当用户编辑了某个曲速 → 重算所有 Note 的 Start / Duration 时使用。
    /// </summary>
    public static void RecomputeNoteTimes(IEnumerable<MidiTrack> tracks, TempoManager mgr)
    {
        foreach (var tr in tracks)
        {
            foreach (var n in tr.Notes)
            {
                double start = mgr.TickToSeconds(n.StartTick);
                double end = mgr.TickToSeconds(n.StartTick + n.DurationTick);
                n.Start = start;
                // 与原解析逻辑一致：极短音符给个 20ms 下限，避免按下/抬起时间几乎同时。
                n.Duration = Math.Max(0.02, end - start);
            }
        }
    }

    /// <summary>对所有音符应用半音数移调 + 指定乐器组。</summary>
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

    public static int FindBestTranspose(IList<Note> notes, InstrumentGroup group)
        => FindBestTransposeWithScore(notes, group).Shift;

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
