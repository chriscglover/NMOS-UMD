using System.Collections.Generic;

namespace NmosUmd.Tsl;

/// <summary>
/// Optional byte-stream wrapper described by the TSL V5.0 spec for TCP / serial transports:
/// each packet is preceded by DLE STX, and any DLE inside the packet is doubled so the
/// receiver can find packet boundaries in a continuous stream.
/// </summary>
public static class StreamFraming
{
    public const byte Dle = 0xFE;
    public const byte Stx = 0x02;

    public static byte[] Wrap(byte[] packet)
    {
        var framed = new List<byte>(packet.Length + 4) { Dle, Stx };
        foreach (var b in packet)
        {
            framed.Add(b);
            if (b == Dle) framed.Add(Dle); // escape by doubling
        }
        return framed.ToArray();
    }
}
