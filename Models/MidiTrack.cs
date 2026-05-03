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
    /// 该轨道的音符集合。使用可变引用以便在移调 / 切换乐器组后
    /// 通过 INotifyPropertyChanged 触发缩略图重绘。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<Note> _notes = Array.Empty<Note>();

    /// <summary>用于缩略图着色的轨道颜色（ARGB 32 位整数）。</summary>
    public uint ColorArgb { get; init; }

    /// <summary>供 XAML 绑定的 Avalonia Color 实例。</summary>
    public Color Color => Color.FromUInt32(ColorArgb);
}
