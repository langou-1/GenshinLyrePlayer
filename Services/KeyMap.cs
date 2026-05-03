using System.Collections.Generic;

namespace GenshinLyrePlayer.Services;

/// <summary>
/// 兼容层：保留老版 <c>KeyMap</c> 静态 API，内部委派到默认乐器组
/// (<see cref="Instruments.Default"/>)。新代码请直接使用 <see cref="Models.InstrumentGroup"/>
/// 以支持多乐器组切换。
/// </summary>
public static class KeyMap
{
    public static IReadOnlyDictionary<int, char> PitchToKey => Instruments.Default.PitchToKey;

    public static int MinPitch => Instruments.Default.MinPitch;
    public static int MaxPitch => Instruments.Default.MaxPitch;

    public static bool IsSupported(int pitch) => Instruments.Default.IsSupported(pitch);
    public static char? GetKey(int pitch) => Instruments.Default.GetKey(pitch);

    public static string GetLabel(int pitch) => Instruments.GetStandardLabel(pitch);
}
