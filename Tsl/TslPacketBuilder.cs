using System;
using System.Text;

namespace NmosUmd.Tsl;

/// <summary>
/// Builds TSL UMD packets for protocol versions 3.1, 4.0 and 5.0.
///
/// V3.1 - 18 bytes:
///     [0]      header  = 0x80 + display address (0-126)
///     [1]      control  bit0..3 = tally 1..4 (on/off)
///                       bit4..5 = brightness (0-3)
///                       bit6    = 0 for display data, 1 for command data
///                       bit7    = 0
///     [2..17]  16 ASCII characters (0x20-0x7E), space padded
///
/// V4.0 - 22 bytes: the V3.1 packet followed by
///     [18]     CHKSUM = 2's complement of (sum of the 18 V3.1 bytes) modulo 128
///     [19]     VBC     bit7    = 0
///                      bit4..6 = minor version (0 for V4.0)
///                      bit0..3 = XDATA byte count (2)
///     [20]     XDATA 1 - display L tally colours
///     [21]     XDATA 2 - display R tally colours
///              each: bit4..5 = LH tally, bit2..3 = text, bit0..1 = RH tally
///                    (0 = off, 1 = red, 2 = green, 3 = amber), bit6..7 = 0
///
/// V5.0 - variable length, all 16-bit fields little endian:
///     [0..1]   PBC    = byte count of everything that follows the PBC field
///     [2]      VER    = 0 (minor version)
///     [3]      FLAGS  bit0 = 0 ASCII / 1 UTF-16LE, bit1 = 0 display data / 1 screen control
///     [4..5]   SCREEN = screen index (0xFFFF = broadcast)
///     then one or more DMSGs:
///     [+0..1]  INDEX   = display index (0xFFFF = broadcast)
///     [+2..3]  CONTROL bit0..1 = RH tally, bit2..3 = text tally, bit4..5 = LH tally,
///                      bit6..7 = brightness, bit8..14 = 0, bit15 = control-data flag
///     [+4..5]  LENGTH  = byte count of TEXT
///     [+6..]   TEXT
/// </summary>
public static class TslPacketBuilder
{
    public const int V31Length = 18;
    public const int V40Length = 22;
    public const int V31TextLength = 16;

    /// <summary>Highest display address that fits in a V3.1/V4.0 header byte.</summary>
    public const int MaxLegacyAddress = 126;

    public const int MaxV50Address = 65534;
    public const int V50BroadcastAddress = 0xFFFF;
    public const int V50BroadcastScreen = 0xFFFF;

    public static byte[] Build(TslVersion version, UmdMessage message, int screen, bool unicode)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        return version switch
        {
            TslVersion.V31 => BuildV31(message),
            TslVersion.V40 => BuildV40(message),
            TslVersion.V50 => BuildV50(message, screen, unicode),
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
    }

    public static byte[] BuildV31(UmdMessage message)
    {
        var packet = new byte[V31Length];
        packet[0] = (byte)(0x80 | (message.Address & 0x7F));
        packet[1] = BuildV31Control(message);
        WriteAsciiPadded(message.Text, packet, 2, V31TextLength);
        return packet;
    }

    public static byte[] BuildV40(UmdMessage message)
    {
        var packet = new byte[V40Length];
        BuildV31(message).CopyTo(packet, 0);

        var sum = 0;
        for (var i = 0; i < V31Length; i++) sum += packet[i];

        // CHKSUM: 2's complement of the V3.1 byte sum, modulo 128 (so bit 7 stays clear).
        packet[18] = (byte)((128 - (sum % 128)) % 128);

        // VBC: minor version 0 (V4.0) in bits 4-6, XDATA length 2 in bits 0-3.
        packet[19] = 0x02;

        // The XDATA bytes address the left and right halves of a dual display. A test tool
        // wants "left lamp" and "right lamp" to behave the same whichever half a device
        // reads, so both bytes carry the same LH/text/RH colours.
        var xdata = BuildXdata(message);
        packet[20] = xdata;
        packet[21] = xdata;
        return packet;
    }

    public static byte[] BuildV50(UmdMessage message, int screen, bool unicode)
    {
        var text = message.Text ?? string.Empty;
        var textBytes = unicode
            ? Encoding.Unicode.GetBytes(text)
            : Encoding.ASCII.GetBytes(SanitiseAscii(text));

        var total = 6 + 6 + textBytes.Length; // header + DMSG header + text
        var packet = new byte[total];

        WriteUInt16(packet, 0, total - 2); // PBC excludes its own two bytes
        packet[2] = 0x00;                  // VER
        packet[3] = (byte)(unicode ? 0x01 : 0x00); // FLAGS
        WriteUInt16(packet, 4, screen);
        WriteUInt16(packet, 6, message.Address);
        WriteUInt16(packet, 8, BuildV50Control(message));
        WriteUInt16(packet, 10, textBytes.Length);
        textBytes.CopyTo(packet, 12);

        return packet;
    }

    private static byte BuildV31Control(UmdMessage message)
    {
        var control = 0;
        if (message.LeftTally != TallyColour.Off) control |= 0x01;  // tally 1
        if (message.RightTally != TallyColour.Off) control |= 0x02; // tally 2
        control |= (Clamp(message.Brightness, 0, 3) & 0x03) << 4;
        return (byte)control; // bit 6 = 0 -> display data, bit 7 = 0
    }

    private static byte BuildXdata(UmdMessage message)
    {
        var value = ((int)message.LeftTally & 0x03) << 4;
        value |= ((int)message.TextTally & 0x03) << 2;
        value |= (int)message.RightTally & 0x03;
        return (byte)value;
    }

    private static int BuildV50Control(UmdMessage message)
    {
        var control = (int)message.RightTally & 0x03;
        control |= ((int)message.TextTally & 0x03) << 2;
        control |= ((int)message.LeftTally & 0x03) << 4;
        control |= (Clamp(message.Brightness, 0, 3) & 0x03) << 6;
        return control; // bit 15 = 0 -> display data rather than control data
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteAsciiPadded(string text, byte[] buffer, int offset, int length)
    {
        var clean = SanitiseAscii(text ?? string.Empty);
        for (var i = 0; i < length; i++)
            buffer[offset + i] = (byte)(i < clean.Length ? clean[i] : ' ');
    }

    /// <summary>V3.1/V4.0 display data is restricted to printable ASCII 0x20-0x7E.</summary>
    private static string SanitiseAscii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        return builder.ToString();
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static string ToHex(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
