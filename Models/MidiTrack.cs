using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GenshinLyrePlayer.Models;

/// <summary>
/// MIDI 中的一个逻辑音轨：包含一个名称、一组音符以及运行时的 Mute 状态。
/// 多轨视图中每条轨道会对应一条缩略图，钢琴卷帘同一时刻只展示其中一条。
/// </summary>
public partial class MidiTrack : ObservableObject
{
    /// <summary>在解析结果中的顺序下标（0 开始）。</summary>
    public int Index { get; init; }

    /// <summary>显示名（取自 MIDI 的 Sequence/Track Name meta，否则 "Track N"）。</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>用户是否将该轨道静音（仅影响演奏，不影响显示）。</summary>
    [ObservableProperty] private bool _muted;

    /// <summary>
    /// 用户是否对该轨道开启 Solo（独奏）。多个轨道可同时 Solo。
    /// 与 Mute 的优先级关系参考 TuneLab：
    ///   - 任意一条 <see cref="Solo"/> = true 的轨道存在时，所有 <see cref="Solo"/> = false 的轨道都不演奏；
    ///   - 同一条轨道上 Solo 优先于 Mute，即 Solo=true 的轨道一定会演奏，无论 Muted 是否为 true。
    /// 综合判定结果由 ViewModel 写入 <see cref="IsAudible"/>。
    /// </summary>
    [ObservableProperty] private bool _solo;

    /// <summary>
    /// 经过全局 Mute / Solo 状态综合判定后，本轨道是否会被实际演奏。
    /// 该值由 <c>MainWindowViewModel</c> 在 Mute/Solo 变化时统一刷新，
    /// 视觉层（缩略图、统计）只需读取此布尔即可。
    /// </summary>
    [ObservableProperty] private bool _isAudible = true;

    /// <summary>
    /// 该轨道的音符集合。使用可变引用以便在移调 / 切换乐器组后
    /// 通过 INotifyPropertyChanged 触发缩略图重绘。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<Note> _notes = Array.Empty<Note>();

    /// <summary>
    /// 此轨道在全局移调（<c>Transpose</c>）之外，单独再叠加的八度偏移量（一个单位 = 12 半音）。
    /// 例如：全局 Transpose=0、本轨 OctaveOffset=+1，则本轨所有音符比原始 MIDI 高一个八度演奏。
    /// 实际应用方式：在 <c>MainWindowViewModel.ReapplyMapping</c> 里将
    /// <c>Transpose + OctaveOffset * 12</c> 作为本轨的最终半音位移传给
    /// <c>MidiParser.ApplyTranspose</c>，仅影响此轨道。
    /// </summary>
    [ObservableProperty] private int _octaveOffset;

    /// <summary>用于缩略图着色的轨道颜色（ARGB 32 位整数）。</summary>
    public uint ColorArgb { get; init; }

    /// <summary>供 XAML 绑定的 Avalonia Color 实例。</summary>
    public Color Color => Color.FromUInt32(ColorArgb);
}
