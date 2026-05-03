using System;
using System.Collections.Generic;
using System.Linq;
using GenshinLyrePlayer.Models;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 拍号（小节/拍）管理器：维护一组按 <see cref="TimeSignatureMarker.Tick"/> 升序的拍号标签，
/// 并提供"任一标签内每小节/每拍占多少 tick"的查询方法。<br/>
/// 与 <see cref="TempoManager"/> 配合使用：
/// <list type="bullet">
///   <item>本类负责"音乐意义上的格线位置"（哪个 tick 是 bar/beat 边界）；</item>
///   <item>TempoManager 负责把 tick 折算到秒，再由 PianoRoll 把秒映射到屏幕 X。</item>
/// </list>
/// 这样格线无论曲速如何变化都贴齐音乐节拍，不再像旧版"每秒一条"那样随意。
/// </summary>
public sealed class TimeSignatureManager
{
    /// <summary>每四分音符的 tick 数（PPQ）。来自 MIDI Header，全程不变。</summary>
    public int Ppq { get; }

    private readonly List<TimeSignatureMarker> _markers;

    /// <summary>按 Tick 升序排列的拍号标签。第一个标签必然位于 Tick = 0（默认 4/4）。</summary>
    public IReadOnlyList<TimeSignatureMarker> Markers => _markers;

    /// <summary>
    /// 构造。<paramref name="raw"/> 是 MIDI 文件里收集到的原始拍号事件（已展开 Denominator 为实际值）。
    /// </summary>
    public TimeSignatureManager(int ppq, IEnumerable<(long Tick, int Numerator, int Denominator)>? raw = null)
    {
        Ppq = ppq <= 0 ? 480 : ppq;

        _markers = (raw ?? Array.Empty<(long, int, int)>())
            .Where(t => t.Numerator > 0 && t.Denominator > 0)
            .OrderBy(t => t.Tick)
            .Select(t => new TimeSignatureMarker
            {
                Tick = Math.Max(0, t.Tick),
                Numerator = t.Numerator,
                Denominator = t.Denominator,
            })
            .ToList();

        // 必须保证起点 (Tick=0) 一定有一个 marker，否则 PianoRoll 在曲首没有拍号可用。
        if (_markers.Count == 0 || _markers[0].Tick != 0)
            _markers.Insert(0, new TimeSignatureMarker { Tick = 0, Numerator = 4, Denominator = 4 });

        // 同一拍号连续出现：保留更早的，去掉后面的，避免重复格线计算。
        for (int i = _markers.Count - 1; i > 0; i--)
        {
            var a = _markers[i - 1];
            var b = _markers[i];
            if (a.Numerator == b.Numerator && a.Denominator == b.Denominator)
                _markers.RemoveAt(i);
        }

        // 累计 BarIndex：标准 MIDI 规范要求拍号变化点恰好落在 bar 边界，但有些文件并不严格，
        // 这里取下取整：从前一个标签到本标签覆盖了多少个完整 bar。
        _markers[0].BarIndex = 0;
        for (int i = 1; i < _markers.Count; i++)
        {
            var prev = _markers[i - 1];
            long ticksPerBar = TicksPerBar(prev);
            long span = _markers[i].Tick - prev.Tick;
            int bars = ticksPerBar > 0 ? (int)(span / ticksPerBar) : 0;
            _markers[i].BarIndex = prev.BarIndex + bars;
        }
    }

    /// <summary>该拍号下每小节占多少 tick。</summary>
    public long TicksPerBar(TimeSignatureMarker m)
        => (long)m.Numerator * 4L * Ppq / Math.Max(1, m.Denominator);

    /// <summary>该拍号下每拍占多少 tick（按分母决定的拍单位）。</summary>
    public long TicksPerBeat(TimeSignatureMarker m)
        => 4L * Ppq / Math.Max(1, m.Denominator);

    /// <summary>找到 <paramref name="tick"/> 所在的拍号段在列表中的索引。</summary>
    public int FindMarkerIndex(long tick)
    {
        int idx = 0;
        for (int i = 1; i < _markers.Count; i++)
        {
            if (_markers[i].Tick > tick) break;
            idx = i;
        }
        return idx;
    }
}
