using System;

namespace NmosUmd.Tsl;

/// <summary>Which TSL UMD protocol dialect to emit.</summary>
public enum TslVersion
{
    V31,
    V40,
    V50
}

/// <summary>
/// Tally lamp state. The numeric values are the 2-bit codes used by TSL V4.0 (XDATA)
/// and V5.0 (CONTROL word). V3.1 only has on/off, so anything other than Off is "on".
/// </summary>
public enum TallyColour
{
    Off = 0,
    Red = 1,
    Green = 2,
    Amber = 3
}

/// <summary>One under-monitor display's worth of data.</summary>
public sealed class UmdMessage
{
    /// <summary>Display address. 0-126 for V3.1/V4.0, 0-65534 for V5.0.</summary>
    public int Address { get; set; }

    public string Text { get; set; } = string.Empty;

    public TallyColour LeftTally { get; set; }
    public TallyColour RightTally { get; set; }
    public TallyColour TextTally { get; set; }

    /// <summary>0 = off, 1 = 1/7, 2 = 1/2, 3 = full.</summary>
    public int Brightness { get; set; } = 3;
}
