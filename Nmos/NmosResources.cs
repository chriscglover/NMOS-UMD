using System;
using System.Collections.Generic;
using System.Linq;

namespace NmosUmd.Nmos;

/// <summary>Fields common to every IS-04 resource.</summary>
public abstract class NmosResource
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Value of the urn:x-nmos:tag:grouphint/v1.0 tag, if the resource carries one.</summary>
    public string GroupHint { get; init; } = string.Empty;

    /// <summary>Label if the device set one, otherwise the id - never blank.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Id : Label;
}

public sealed class NmosNode : NmosResource
{
    public string Hostname { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
}

public sealed class NmosDevice : NmosResource
{
    public string NodeId { get; init; } = string.Empty;
}

public sealed class NmosSource : NmosResource
{
    public string DeviceId { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
}

public sealed class NmosFlow : NmosResource
{
    public string SourceId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
}

public sealed class NmosSender : NmosResource
{
    public string DeviceId { get; init; } = string.Empty;
    public string FlowId { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string ManifestHref { get; init; } = string.Empty;
}

public sealed class NmosReceiver : NmosResource
{
    public string DeviceId { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;

    /// <summary>subscription.active - whether the receiver is enabled and consuming a stream.</summary>
    public bool SubscriptionActive { get; init; }

    /// <summary>subscription.sender_id - null when the receiver is idle or fed from a bare SDP.</summary>
    public string? SenderId { get; init; }
}

/// <summary>
/// What a receiver is currently fed by, with every resource on the path already looked up:
/// receiver -> sender -> flow -> source, plus the devices and nodes either end belongs to.
/// </summary>
public sealed class RouteInfo
{
    public required NmosReceiver Receiver { get; init; }
    public NmosDevice? ReceiverDevice { get; init; }
    public NmosNode? ReceiverNode { get; init; }

    public NmosSender? Sender { get; init; }
    public NmosFlow? Flow { get; init; }
    public NmosSource? Source { get; init; }
    public NmosDevice? SenderDevice { get; init; }
    public NmosNode? SenderNode { get; init; }

    /// <summary>The receiver names a sender in its subscription.</summary>
    public bool HasSenderId => !string.IsNullOrEmpty(Receiver.SenderId);

    /// <summary>Routed and the sender resource was found in the registry.</summary>
    public bool IsRouted => Receiver.SubscriptionActive && Sender is not null;

    /// <summary>
    /// Routed to a sender the registry does not hold. Happens when the far node has
    /// unregistered, or when the receiver was patched with a raw SDP file rather than by IS-05.
    /// </summary>
    public bool IsRoutedToUnknown => Receiver.SubscriptionActive && Sender is null;
}

/// <summary>
/// An immutable view of everything read from the registry in one poll, indexed for lookup.
/// </summary>
public sealed class RegistrySnapshot
{
    public static RegistrySnapshot Empty { get; } = new();

    public IReadOnlyList<NmosReceiver> Receivers { get; init; } = Array.Empty<NmosReceiver>();
    public IReadOnlyDictionary<string, NmosSender> Senders { get; init; } = new Dictionary<string, NmosSender>();
    public IReadOnlyDictionary<string, NmosFlow> Flows { get; init; } = new Dictionary<string, NmosFlow>();
    public IReadOnlyDictionary<string, NmosSource> Sources { get; init; } = new Dictionary<string, NmosSource>();
    public IReadOnlyDictionary<string, NmosDevice> Devices { get; init; } = new Dictionary<string, NmosDevice>();
    public IReadOnlyDictionary<string, NmosNode> Nodes { get; init; } = new Dictionary<string, NmosNode>();

    public DateTime FetchedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Receivers sorted the way a human would expect to pick them from a list.</summary>
    public IEnumerable<NmosReceiver> ReceiversByName =>
        Receivers.OrderBy(r => DeviceNameOf(r), StringComparer.OrdinalIgnoreCase)
                 .ThenBy(r => r.DisplayName, NaturalComparer.Instance);

    public NmosReceiver? Receiver(string? id) =>
        string.IsNullOrEmpty(id) ? null : Receivers.FirstOrDefault(r => r.Id == id);

    public string DeviceNameOf(NmosReceiver receiver) =>
        Devices.TryGetValue(receiver.DeviceId, out var device) ? device.DisplayName : string.Empty;

    /// <summary>Walks receiver -> sender -> flow -> source and the device/node either side.</summary>
    public RouteInfo Route(NmosReceiver receiver)
    {
        NmosSender? sender = null;
        if (!string.IsNullOrEmpty(receiver.SenderId)) Senders.TryGetValue(receiver.SenderId!, out sender);

        NmosFlow? flow = null;
        if (sender is not null && !string.IsNullOrEmpty(sender.FlowId)) Flows.TryGetValue(sender.FlowId, out flow);

        NmosSource? source = null;
        if (flow is not null && !string.IsNullOrEmpty(flow.SourceId)) Sources.TryGetValue(flow.SourceId, out source);

        return new RouteInfo
        {
            Receiver = receiver,
            ReceiverDevice = Device(receiver.DeviceId),
            ReceiverNode = NodeOfDevice(receiver.DeviceId),
            Sender = sender,
            Flow = flow,
            Source = source,
            SenderDevice = sender is null ? null : Device(sender.DeviceId),
            SenderNode = sender is null ? null : NodeOfDevice(sender.DeviceId)
        };
    }

    private NmosDevice? Device(string id) =>
        !string.IsNullOrEmpty(id) && Devices.TryGetValue(id, out var device) ? device : null;

    private NmosNode? NodeOfDevice(string deviceId)
    {
        var device = Device(deviceId);
        if (device is null || string.IsNullOrEmpty(device.NodeId)) return null;
        return Nodes.TryGetValue(device.NodeId, out var node) ? node : null;
    }
}

/// <summary>
/// Sorts "Input 2" before "Input 10". Receiver labels are almost always numbered, and plain
/// string ordering scatters them in a way that makes a 32-input multiviewer painful to map.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startI = i;
                var startJ = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var left = x[startI..i].TrimStart('0');
                var right = y[startJ..j].TrimStart('0');

                if (left.Length != right.Length) return left.Length - right.Length;
                var digits = string.CompareOrdinal(left, right);
                if (digits != 0) return digits;
                continue;
            }

            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0) return c;
            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
