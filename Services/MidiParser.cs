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

        /// <summary>整首曲子里全部 SetTempo（曲速变化）事件按时间排序后的列表，用于在速度轨上绘制曲速标签。</summary>
        public IReadOnlyList<TempoMarker> Tempos { get; init; } = Array.Empty<TempoMarker>();

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
    /// 从 MIDI 文件中按“轨”拆分音符。
    /// - 对于多轨 MIDI (type 1)，每个 MIDI track 对应一条轨道；
    /// - 对于单轨 MIDI (type 0)，若出现多个通道则按通道拆分。
    /// 没有任何音符的 track（纯 meta）会被忽略。
    /// </summary>
    public static ParseResult Parse(string path)
    {
        var midiFile = new MidiFile(path, false);
        int ppq = midiFile.DeltaTicksPerQuarterNote;
        if (ppq <= 0) ppq = 480;

        // 1. 收集全局 tempo 变化点（跨全部 track）。
        var tempos = new List<(long Tick, int Mpqn)>();
        foreach (var track in midiFile.Events)
        {
            foreach (var e in track)
            {
                if (e is TempoEvent te)
                    tempos.Add((te.AbsoluteTime, te.MicrosecondsPerQuarterNote));
            }
        }
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        if (tempos.Count == 0 || tempos[0].Tick > 0)
            tempos.Insert(0, (0, 500000)); // 默认 120 BPM

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

        // 2. 每个 track 独立收集 Name + Notes。
        var tracks = new List<MidiTrack>();
        double total = 0;

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

                    double start = TickToSeconds(startTick);
                    double end = TickToSeconds(endTick);
                    double dur = end - start;
                    if (dur < 0.02) dur = 0.02;

                    notes.Add(new Note
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

            if (notes.Count == 0) continue;
            notes.Sort((a, b) => a.Start.CompareTo(b.Start));

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

        // 应用默认映射（无移调）
        foreach (var tr in tracks)
            ApplyTranspose(tr.Notes, 0, Instruments.Default);

        // 把曲速点折算成 (秒, BPM) 列表，去掉相邻重复的 BPM。
        var tempoMarkers = new List<TempoMarker>();
        double lastBpm = -1;
        foreach (var (tick, mpqn) in tempos)
        {
            if (mpqn <= 0) continue;
            double bpm = 60_000_000.0 / mpqn;
            // 同一 BPM 连续出现时只保留首个，避免在速度轨上叠成糊
            if (Math.Abs(bpm - lastBpm) < 0.001) continue;
            tempoMarkers.Add(new TempoMarker { Time = TickToSeconds(tick), Bpm = bpm });
            lastBpm = bpm;
        }

        return new ParseResult
        {
            Tracks = tracks,
            Tempos = tempoMarkers,
            TotalDuration = total,
            FileName = System.IO.Path.GetFileName(path),
        };
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
