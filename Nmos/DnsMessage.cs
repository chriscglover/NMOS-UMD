using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace NmosUmd.Nmos;

/// <summary>Resource record types this browser cares about.</summary>
internal enum DnsRecordType
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255
}

/// <summary>One decoded resource record. Only the fields DNS-SD needs are kept.</summary>
internal sealed class DnsRecord
{
    public string Name = string.Empty;
    public DnsRecordType Type;
    public uint Ttl;

    /// <summary>PTR target - the service instance name.</summary>
    public string? PtrTarget;

    /// <summary>SRV target host and port.</summary>
    public string? SrvTarget;
    public int SrvPort;

    /// <summary>TXT strings, each still in "key=value" form.</summary>
    public List<string>? TxtStrings;

    /// <summary>A / AAAA address.</summary>
    public IPAddress? Address;
}

/// <summary>
/// Just enough DNS wire format to browse DNS-SD over mDNS: build PTR queries and
/// decode the PTR / SRV / TXT / A records that come back.
///
/// Written by hand rather than pulled from a library so the application stays a
/// single copy-and-run executable with no NuGet or Bonjour dependency - the same
/// reasoning the PCAP Replay tool used for the native Windows DNS-SD API.
/// </summary>
internal static class DnsMessage
{
    /// <summary>
    /// Builds a standard multicast query asking for the PTR records of each service type.
    /// </summary>
    /// <param name="unicastResponse">
    /// Sets the QU bit, asking responders to answer directly to our source port. Needed when
    /// the socket could not be bound to port 5353 and so never sees the multicast replies.
    /// </param>
    public static byte[] BuildQuery(IReadOnlyList<string> names, bool unicastResponse)
    {
        var buffer = new List<byte>(64 + names.Count * 32);

        WriteUInt16(buffer, 0);                  // ID - 0 for mDNS
        WriteUInt16(buffer, 0);                  // flags - standard query
        WriteUInt16(buffer, names.Count);        // QDCOUNT
        WriteUInt16(buffer, 0);                  // ANCOUNT
        WriteUInt16(buffer, 0);                  // NSCOUNT
        WriteUInt16(buffer, 0);                  // ARCOUNT

        foreach (var name in names)
        {
            WriteName(buffer, name);
            WriteUInt16(buffer, (int)DnsRecordType.Ptr);
            WriteUInt16(buffer, unicastResponse ? 0x8001 : 0x0001); // QU/QM + class IN
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes every answer, authority and additional record in a message. Questions are
    /// skipped: a response to another host's query is just as useful to us as one to ours,
    /// which is the whole point of mDNS being multicast.
    /// </summary>
    public static List<DnsRecord> ParseRecords(byte[] data, int length)
    {
        var records = new List<DnsRecord>();
        if (length < 12) return records;

        var flags = ReadUInt16(data, 2);
        var questions = ReadUInt16(data, 4);
        var answers = ReadUInt16(data, 6);
        var authorities = ReadUInt16(data, 8);
        var additionals = ReadUInt16(data, 10);
        var offset = 12;

        // Bit 15 set means this is a response; a query carries nothing worth reading.
        if ((flags & 0x8000) == 0 && answers == 0 && additionals == 0) return records;

        for (var i = 0; i < questions; i++)
        {
            if (!SkipName(data, length, ref offset)) return records;
            offset += 4; // QTYPE + QCLASS
            if (offset > length) return records;
        }

        var total = answers + authorities + additionals;
        for (var i = 0; i < total; i++)
        {
            if (!TryReadRecord(data, length, ref offset, out var record)) break;
            if (record is not null) records.Add(record);
        }

        return records;
    }

    private static bool TryReadRecord(byte[] data, int length, ref int offset, out DnsRecord? record)
    {
        record = null;

        if (!TryReadName(data, length, ref offset, out var name)) return false;
        if (offset + 10 > length) return false;

        var type = (DnsRecordType)ReadUInt16(data, offset);
        // class sits at offset + 2, with the cache-flush bit in bit 15 - not needed here
        var ttl = (uint)((data[offset + 4] << 24) | (data[offset + 5] << 16) |
                         (data[offset + 6] << 8) | data[offset + 7]);
        var rdLength = ReadUInt16(data, offset + 8);
        offset += 10;

        var rdStart = offset;
        var rdEnd = rdStart + rdLength;
        if (rdEnd > length) return false;
        offset = rdEnd; // step past the record whatever happens below

        var parsed = new DnsRecord { Name = name, Type = type, Ttl = ttl };

        switch (type)
        {
            case DnsRecordType.Ptr:
            {
                var p = rdStart;
                if (!TryReadName(data, length, ref p, out var target)) return true;
                parsed.PtrTarget = target;
                break;
            }
            case DnsRecordType.Srv:
            {
                if (rdLength < 7) return true;
                parsed.SrvPort = ReadUInt16(data, rdStart + 4);
                var p = rdStart + 6;
                if (!TryReadName(data, length, ref p, out var target)) return true;
                parsed.SrvTarget = target;
                break;
            }
            case DnsRecordType.Txt:
            {
                var strings = new List<string>();
                var p = rdStart;
                while (p < rdEnd)
                {
                    int len = data[p++];
                    if (len == 0 || p + len > rdEnd) break;
                    strings.Add(Encoding.UTF8.GetString(data, p, len));
                    p += len;
                }
                parsed.TxtStrings = strings;
                break;
            }
            case DnsRecordType.A:
            {
                if (rdLength != 4) return true;
                var bytes = new byte[4];
                Array.Copy(data, rdStart, bytes, 0, 4);
                parsed.Address = new IPAddress(bytes);
                break;
            }
            case DnsRecordType.Aaaa:
            {
                if (rdLength != 16) return true;
                var bytes = new byte[16];
                Array.Copy(data, rdStart, bytes, 0, 16);
                parsed.Address = new IPAddress(bytes);
                break;
            }
            default:
                return true; // known length, record discarded
        }

        record = parsed;
        return true;
    }

    /// <summary>
    /// Reads a possibly compressed name. Compression pointers are followed once the reader
    /// has left the sequential stream, so <paramref name="offset"/> ends up just past the
    /// first pointer rather than wherever the chase finished.
    /// </summary>
    private static bool TryReadName(byte[] data, int length, ref int offset, out string name)
    {
        var labels = new List<string>();
        var jumped = false;
        var position = offset;
        var guard = 0;

        while (true)
        {
            if (position >= length) { name = string.Empty; return false; }
            if (++guard > 128) { name = string.Empty; return false; } // pointer loop

            int len = data[position];

            if ((len & 0xC0) == 0xC0)
            {
                if (position + 1 >= length) { name = string.Empty; return false; }
                var pointer = ((len & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }
                position = pointer;
                continue;
            }

            position++;
            if (len == 0) break;
            if (position + len > length) { name = string.Empty; return false; }
            labels.Add(Encoding.UTF8.GetString(data, position, len));
            position += len;
        }

        if (!jumped) offset = position;
        name = string.Join(".", labels);
        return true;
    }

    private static bool SkipName(byte[] data, int length, ref int offset)
        => TryReadName(data, length, ref offset, out _);

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var count = Math.Min(bytes.Length, 63);
            buffer.Add((byte)count);
            for (var i = 0; i < count; i++) buffer.Add(bytes[i]);
        }
        buffer.Add(0);
    }

    private static void WriteUInt16(List<byte> buffer, int value)
    {
        buffer.Add((byte)((value >> 8) & 0xFF));
        buffer.Add((byte)(value & 0xFF));
    }

    private static int ReadUInt16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];
}
