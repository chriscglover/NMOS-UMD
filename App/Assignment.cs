using System;
using NmosUmd.Nmos;
using NmosUmd.Tsl;

namespace NmosUmd.App;

/// <summary>
/// One line of the mapping table: an NMOS receiver tied to a TSL UMD display address.
/// The label sent to that display is whatever the receiver is currently routed from.
/// </summary>
public sealed class Assignment
{
    public bool Enabled { get; set; } = true;

    /// <summary>TSL display address. 0-126 on V3.1/V4.0, 0-65534 on V5.0.</summary>
    public int Address { get; set; }

    /// <summary>Receiver resource id. Empty until one is picked.</summary>
    public string ReceiverId { get; set; } = string.Empty;

    /// <summary>
    /// Last known receiver label, saved with the mapping. A saved configuration is opened
    /// long before the registry answers - and sometimes when the receiver's node is offline
    /// entirely - so the table can still show which receiver a row means.
    /// </summary>
    public string ReceiverLabel { get; set; } = string.Empty;

    /// <summary>Optional per-row template; blank uses the global one.</summary>
    public string TemplateOverride { get; set; } = string.Empty;

    // ---- runtime state, not persisted: what this display was last actually told ----

    [System.Text.Json.Serialization.JsonIgnore]
    public string LastSentText { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public TallyColour LastSentLamp { get; set; } = TallyColour.Off;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSent { get; set; }

    /// <summary>
    /// The label or lamp has changed since this display was last sent to. Changed displays are
    /// sent ahead of the routine refresh, so a route change reaches the wall immediately rather
    /// than waiting for its turn to come round.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Dirty { get; set; }

    public Assignment Clone() => new()
    {
        Enabled = Enabled,
        Address = Address,
        ReceiverId = ReceiverId,
        ReceiverLabel = ReceiverLabel,
        TemplateOverride = TemplateOverride
    };
}

/// <summary>How an over-long label is squeezed into the 16 characters V3.1 allows.</summary>
public enum FitMode
{
    /// <summary>Keep the first 16 characters.</summary>
    Truncate,

    /// <summary>Keep the last 16 characters - useful when the port number is on the end.</summary>
    KeepEnd,

    /// <summary>Drop vowels from the longest words first, then spaces, then truncate.</summary>
    Squeeze
}

