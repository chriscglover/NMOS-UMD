using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NmosUmd.Nmos;

namespace NmosUmd.App;

/// <summary>
/// Turns "this receiver is fed by that sender" into the text a UMD shows.
///
/// The template is a plain string with {tokens} in it. A token may list alternatives separated
/// by "|", and the first one that resolves to something non-empty wins, so
/// {sender.label|sender.id} shows the sender's name and falls back to its id when a device has
/// been registered without a label.
/// </summary>
public static class LabelFormatter
{
    public const string DefaultRoutedTemplate = "{sender.label|sender.id}";
    /// <summary>What a display shows when its receiver has nothing routed to it.</summary>
    public const string DefaultUnroutedTemplate = "Parked";

    private static readonly Regex TokenPattern =
        new(@"\{(?<body>[^{}]*)\}", RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>Every token the templates understand, for the help text in the UI.</summary>
    public static IReadOnlyList<string> Tokens { get; } = new[]
    {
        "{sender.label}", "{sender.description}", "{sender.id}", "{sender.device}", "{sender.node}",
        "{sender.grouphint}", "{sender.transport}",
        "{source.label}", "{source.description}", "{flow.label}", "{flow.format}",
        "{receiver.label}", "{receiver.description}", "{receiver.id}", "{receiver.device}",
        "{receiver.node}", "{receiver.grouphint}", "{receiver.format}",
        "{addr}", "{addr:000}"
    };

    /// <summary>Fills in a template for one receiver's current route.</summary>
    public static string Format(string template, RouteInfo route, int address)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return TokenPattern.Replace(template, match =>
        {
            var body = match.Groups["body"].Value;

            foreach (var alternative in body.Split('|'))
            {
                var value = Resolve(alternative.Trim(), route, address);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return string.Empty;
        });
    }

    private static string Resolve(string token, RouteInfo route, int address)
    {
        if (token.Length == 0) return string.Empty;

        // Literal fallback: {sender.label|"NO SOURCE"} puts plain text at the end of a chain.
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"') return token[1..^1];

        if (token.StartsWith("addr", StringComparison.OrdinalIgnoreCase))
        {
            var colon = token.IndexOf(':');
            if (colon < 0) return address.ToString(CultureInfo.InvariantCulture);
            var format = token[(colon + 1)..];
            return address.ToString(format, CultureInfo.InvariantCulture);
        }

        var dot = token.IndexOf('.');
        var scope = dot < 0 ? token : token[..dot];
        var field = dot < 0 ? "label" : token[(dot + 1)..];

        NmosResource? resource = scope.ToLowerInvariant() switch
        {
            "sender" => route.Sender,
            "receiver" => route.Receiver,
            "flow" => route.Flow,
            "source" => route.Source,
            _ => null
        };

        if (resource is null) return string.Empty;

        switch (field.ToLowerInvariant())
        {
            case "label": return resource.Label;
            case "name": return resource.DisplayName;
            case "description": return resource.Description;
            case "id": return resource.Id;
            case "grouphint": return resource.GroupHint;

            case "device":
                return scope.Equals("sender", StringComparison.OrdinalIgnoreCase)
                    ? route.SenderDevice?.DisplayName ?? string.Empty
                    : route.ReceiverDevice?.DisplayName ?? string.Empty;

            case "node":
                return scope.Equals("sender", StringComparison.OrdinalIgnoreCase)
                    ? route.SenderNode?.DisplayName ?? string.Empty
                    : route.ReceiverNode?.DisplayName ?? string.Empty;

            case "format":
                return ShortFormat(resource switch
                {
                    NmosReceiver receiver => receiver.Format,
                    NmosFlow flow => flow.Format,
                    NmosSource source => source.Format,
                    _ => string.Empty
                });

            case "transport":
                return resource is NmosSender sender ? ShortFormat(sender.Transport) : string.Empty;

            default:
                return string.Empty;
        }
    }

    /// <summary>"urn:x-nmos:format:video" reads as "video" on a 16-character display.</summary>
    private static string ShortFormat(string urn)
    {
        if (string.IsNullOrEmpty(urn)) return string.Empty;
        var last = urn.LastIndexOf(':');
        return last >= 0 && last < urn.Length - 1 ? urn[(last + 1)..] : urn;
    }

    /// <summary>
    /// Squeezes text into the character budget a protocol version allows. V3.1 and V4.0 carry
    /// exactly 16 ASCII characters, which most NMOS labels comfortably overrun.
    /// </summary>
    public static string Fit(string text, int maxLength, FitMode mode)
    {
        var clean = WhitespacePattern.Replace(text ?? string.Empty, " ").Trim();
        if (maxLength <= 0 || clean.Length <= maxLength) return clean;

        return mode switch
        {
            FitMode.KeepEnd => KeepEnd(clean, maxLength),
            FitMode.Squeeze => Squeeze(clean, maxLength),
            _ => clean[..maxLength]
        };
    }

    /// <summary>
    /// Keeps the tail, then drops a leading part-word: "ay THEBEAST:3210" reads as a mistake,
    /// where "THEBEAST:3210" reads as a name.
    /// </summary>
    private static string KeepEnd(string text, int maxLength)
    {
        var tail = text[^maxLength..];
        if (text[^(maxLength + 1)] == ' ') return tail; // the cut already fell on a word boundary

        var space = tail.IndexOf(' ');
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }

    /// <summary>
    /// Squeezes in four steps, each more destructive than the last, stopping as soon as the text
    /// fits: drop vowels from the longest word first (where the redundancy is), close up the
    /// spaces, trim letters off the end, and only then cut.
    ///
    /// Digits are never dropped. In a name like "PCAP Replay THEBEAST:3210" the trailing number
    /// is what tells one source from another, so it is the last thing an operator can afford to
    /// lose - and it is exactly what a plain truncation takes first.
    /// </summary>
    private static string Squeeze(string text, int maxLength)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return text[..maxLength];

        var current = new List<StringBuilder>(words.Select(w => new StringBuilder(w)));

        while (Length(current) > maxLength)
        {
            var target = -1;
            for (var i = 0; i < current.Count; i++)
            {
                if (!HasRemovableVowel(current[i])) continue;
                if (target < 0 || current[i].Length > current[target].Length) target = i;
            }

            if (target < 0) break; // nothing left to drop
            RemoveLastVowel(current[target]);
        }

        var squeezed = string.Join(" ", current.Select(w => w.ToString()));
        if (squeezed.Length <= maxLength) return squeezed;

        var packed = new StringBuilder(squeezed.Replace(" ", string.Empty));

        // Trim letters from the end, so the digits survive.
        while (packed.Length > maxLength)
        {
            var last = -1;
            for (var i = packed.Length - 1; i >= 0; i--)
            {
                if (!char.IsLetter(packed[i]) || TouchesDigit(packed, i)) continue;
                last = i;
                break;
            }

            if (last < 0) break;
            packed.Remove(last, 1);
        }

        var result = packed.ToString();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static int Length(List<StringBuilder> words)
        => words.Sum(w => w.Length) + Math.Max(0, words.Count - 1);

    private static bool HasRemovableVowel(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
            if (IsRemovable(word, i)) return true;
        return false;
    }

    private static void RemoveLastVowel(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
        {
            if (!IsRemovable(word, i)) continue;
            word.Remove(i, 1);
            return;
        }
    }

    private static bool IsRemovable(StringBuilder word, int index) =>
        IsVowel(word[index]) && !TouchesDigit(word, index);

    /// <summary>
    /// True when a character sits against a digit. A letter wedged between digits is part of a
    /// number rather than a word - dropping the "i" from "1080i25" would leave "108025", which
    /// reads as a different, entirely plausible number.
    /// </summary>
    private static bool TouchesDigit(StringBuilder word, int index) =>
        (index > 0 && char.IsDigit(word[index - 1])) ||
        (index + 1 < word.Length && char.IsDigit(word[index + 1]));

    private static bool IsVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;
}
