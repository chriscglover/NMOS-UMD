using System;
using NmosUmd.Nmos;
using NmosUmd.Tsl;

namespace NmosUmd.App;

/// <summary>What one assignment currently resolves to.</summary>
public sealed class UmdRow
{
    public required Assignment Assignment { get; init; }
    public required UmdMessage Message { get; init; }

    /// <summary>The source name as it stands before the template and 16-character fit, for the table.</summary>
    public required string SourceText { get; init; }

    /// <summary>The receiver is subscribed to a sender the registry can name.</summary>
    public bool Routed { get; init; }

    /// <summary>The receiver itself is not in the registry - its node is offline, or the id is stale.</summary>
    public bool ReceiverOffline { get; init; }
}

/// <summary>
/// Works out what each display should be showing. Kept apart from the window so the rules -
/// which template applies, what happens when a route or a receiver disappears, how the text is
/// cut to length - are in one place rather than spread through event handlers.
/// </summary>
public static class UmdEngine
{
    /// <summary>V3.1 and V4.0 carry exactly 16 ASCII characters; V5.0 has no fixed limit.</summary>
    public static int MaxTextLength(TslVersion version) =>
        version == TslVersion.V50 ? 0 : TslPacketBuilder.V31TextLength;

    public static UmdRow Build(Assignment assignment, RegistrySnapshot snapshot, AppConfig config)
    {
        var receiver = snapshot.Receiver(assignment.ReceiverId);
        var offline = receiver is null;

        // Keep the row meaningful when the receiver is not in the registry: the saved label
        // still names it, so the display says which input has gone away.
        receiver ??= new NmosReceiver
        {
            Id = assignment.ReceiverId,
            Label = assignment.ReceiverLabel
        };

        var route = offline
            ? new RouteInfo { Receiver = receiver }
            : snapshot.Route(receiver);

        var routed = route.IsRouted;
        var template = string.IsNullOrWhiteSpace(assignment.TemplateOverride)
            ? (routed ? config.RoutedTemplate : config.UnroutedTemplate)
            : assignment.TemplateOverride;

        var text = LabelFormatter.Format(template, route, assignment.Address);

        // A routed template that resolves to nothing (a sender with no label, say) would leave
        // the display blank, which reads as "no signal". Fall back rather than show nothing.
        if (string.IsNullOrWhiteSpace(text) && routed)
            text = LabelFormatter.Format(config.UnroutedTemplate, route, assignment.Address);
        if (string.IsNullOrWhiteSpace(text))
            text = receiver.DisplayName;

        if (config.Uppercase) text = text.ToUpperInvariant();
        text = LabelFormatter.Fit(text, MaxTextLength(config.Version), config.FitMode);

        var lamp = routed ? config.RoutedLamp : config.UnroutedLamp;

        var message = new UmdMessage
        {
            Address = assignment.Address,
            Text = text,
            LeftTally = lamp,
            RightTally = lamp,
            TextTally = config.DriveTextTally ? lamp : TallyColour.Off,
            Brightness = config.Brightness
        };

        return new UmdRow
        {
            Assignment = assignment,
            Message = message,
            SourceText = DescribeSource(route, offline),
            Routed = routed,
            ReceiverOffline = offline
        };
    }

    /// <summary>Plain-English source description for the mapping table.</summary>
    private static string DescribeSource(RouteInfo route, bool offline)
    {
        if (offline) return "(receiver not in registry)";
        if (route.IsRouted) return route.Sender!.DisplayName;
        if (route.IsRoutedToUnknown)
        {
            return route.HasSenderId
                ? $"(sender {Short(route.Receiver.SenderId!)} not in registry)"
                : "(active, no sender id)";
        }
        return "(not routed)";
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    /// <summary>Builds the bytes for one row, wrapping them for a byte stream if asked.</summary>
    public static byte[] BuildPacket(UmdRow row, AppConfig config)
    {
        var screen = config.ScreenBroadcast ? TslPacketBuilder.V50BroadcastScreen : config.Screen;
        var packet = TslPacketBuilder.Build(config.Version, row.Message, screen, config.Unicode);
        return config.UseTcp && config.StreamFraming ? StreamFraming.Wrap(packet) : packet;
    }

    /// <summary>
    /// True when this display is showing something other than what it was last sent - a new
    /// route, a new label, a lamp change - or has never been sent to at all.
    /// </summary>
    public static bool HasChanged(UmdRow row)
    {
        var assignment = row.Assignment;
        return !assignment.HasSent
               || assignment.LastSentText != row.Message.Text
               || assignment.LastSentLamp != row.Message.LeftTally;
    }

    /// <summary>Records what was sent, so the change detection above has something to compare with.</summary>
    public static void MarkSent(UmdRow row)
    {
        var assignment = row.Assignment;
        assignment.LastSentText = row.Message.Text;
        assignment.LastSentLamp = row.Message.LeftTally;
        assignment.HasSent = true;
        assignment.Dirty = false;
    }

    /// <summary>Highest display address the chosen protocol version can address.</summary>
    public static int MaxAddress(TslVersion version) =>
        version == TslVersion.V50 ? TslPacketBuilder.MaxV50Address : TslPacketBuilder.MaxLegacyAddress;
}
