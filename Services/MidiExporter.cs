using System;
using System.Collections.Generic;
using GenshinLyrePlayer.Models;
using NAudio.Midi;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 将当前已加载的轨道 / 曲速 / 拍号数据导出为标准 MIDI Format-1 文件。
///
/// 导出使用 <see cref="Note.OriginalPitch"/>（原始音高，不含移调），
/// 保证 MIDI → 导入 → 导出 的无损往返；要保存移调后结果可在导出前
/// 将 <c>Transpose</c> 重置为 0 即可。
/// </summary>
public static class MidiExporter
{
    /// <summary>
    /// 把当前场景的轨道、曲速、拍号写入一个 MIDI Format-1 文件。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="tracks">所有轨道。</param>
    /// <param name="tempoManager">曲速管理器（可为 null，此时使用 120 BPM）。</param>
    /// <param name="tsManager">拍号管理器（可为 null，此时使用 4/4）。</param>
    public static void Export(
        string path,
        IReadOnlyList<MidiTrack> tracks,
        TempoManager? tempoManager,
        TimeSignatureManager? tsManager)
    {
        int ppq = tempoManager?.Ppq ?? 480;
        var collection = new MidiEventCollection(1, ppq);

        // ===== Track 0: conductor (tempo + time signature meta) =====
        collection.AddTrack();
        AddTempoEvents(collection, tempoManager);
        AddTimeSignatureEvents(collection, tsManager);

        // 计算所有轨道最大的 EndTick 以放置 EndTrack 事件
        long maxEndTick = 0;
        foreach (var tr in tracks)
            foreach (var n in tr.Notes)
            {
                long end = n.StartTick + n.DurationTick;
                if (end > maxEndTick) maxEndTick = end;
            }

        // Conductor track 的 EndTrack
        collection[0].Add(new MetaEvent(MetaEventType.EndTrack, 0, maxEndTick));

        // ===== Track 1..N: note tracks =====
        for (int i = 0; i < tracks.Count; i++)
        {
            var tr = tracks[i];
            collection.AddTrack();
            int trackIdx = i + 1; // +1 because track 0 is conductor

            // Track Name meta event
            collection[trackIdx].Add(
                new TextEvent(tr.Name, MetaEventType.SequenceTrackName, 0));

            // Notes
            foreach (var note in tr.Notes)
            {
                int channel = Math.Clamp(note.Channel, 0, 15) + 1; // NAudio uses 1-based channels
                int pitch = note.OriginalPitch;
                int velocity = Math.Clamp(note.Velocity, 1, 127);
                int dur = (int)Math.Max(1, note.DurationTick);

                var noteOn = new NoteOnEvent(note.StartTick, channel, pitch, velocity, dur);
                collection[trackIdx].Add(noteOn);
                collection[trackIdx].Add(noteOn.OffEvent);
            }

            // EndTrack
            collection[trackIdx].Add(new MetaEvent(MetaEventType.EndTrack, 0, maxEndTick));
        }

        MidiFile.Export(path, collection);
    }

    /// <summary>把 <see cref="TempoManager"/> 的所有标签写为 SetTempo 元事件。</summary>
    private static void AddTempoEvents(MidiEventCollection collection, TempoManager? mgr)
    {
        if (mgr == null)
        {
            // 默认 120 BPM = 500000 µs/quarter
            collection[0].Add(new TempoEvent(500_000, 0));
            return;
        }

        foreach (var m in mgr.Markers)
        {
            collection[0].Add(new TempoEvent(m.Mpqn, m.Tick));
        }
    }

    /// <summary>把 <see cref="TimeSignatureManager"/> 的所有标签写为 TimeSignature 元事件。</summary>
    private static void AddTimeSignatureEvents(MidiEventCollection collection, TimeSignatureManager? mgr)
    {
        if (mgr == null)
        {
            // 默认 4/4
            collection[0].Add(new TimeSignatureEvent(0, 4, 2, 24, 8));
            return;
        }

        foreach (var m in mgr.Markers)
        {
            // MIDI 标准：denominator 存的是 2 的幂次 (e.g. 2 → 4, 3 → 8)
            int denomLog2 = 0;
            int d = m.Denominator;
            while (d > 1) { d >>= 1; denomLog2++; }

            // ticksInMetronomeClick: MIDI clocks per metronome click
            // 通常每拍一次 = 24 MIDI clocks (标准值)
            // no32ndNotesInQuarterNote: 通常为 8
            collection[0].Add(new TimeSignatureEvent(m.Tick, m.Numerator, denomLog2, 24, 8));
        }
    }
}
