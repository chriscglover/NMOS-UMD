# NMOS UMD

A Windows tool that reads an NMOS registry and drives TSL UMD displays with the **name of
whatever is currently routed to a receiver**.

Associate an NMOS receiver with a UMD address, and that display shows the source feeding it.
Route "PCAP Replay THEBEAST:3210" to receiver "MV-01 In 01", and UMD address 1 reads
`PCAP Replay THEBEAST:3210`. Un-route it and the display reads `Parked`.

## What it does

- Finds registries by **mDNS**, or takes a **typed address** for networks where mDNS does not
  cross subnets.
- Polls the IS-04 **Query API** for receivers and senders, and resolves each receiver's active
  subscription to the sender's name.
- Sends **TSL UMD 3.1, 4.0 or 5.0** over UDP or TCP, one packet per mapped display: changes
  immediately, and a paced keepalive so displays recover on their own.
- **Starts working on its own** — connects and begins sending as soon as it opens.
- Fits long NMOS labels into the **16 characters** 3.1 and 4.0 allow, three different ways.
- Saves the mapping, so the tool comes back up driving the same wall.

## Build

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.

```powershell
dotnet build
dotnet run
```

To produce a single file that needs nothing installed on the target machine:

```powershell
# standalone - no .NET needed on the target machine
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtraction=true `
  -p:EnableCompressionInSingleFile=true

# framework-dependent - needs the .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true
```

## How to use it

### NMOS registry

**Discover (mDNS)** browses for `_nmos-query._tcp` and lists every registry that answers, with
its address and the API versions its TXT record advertises. *Rescan* clears the list and asks
again. The browse also covers `_nmos-register._tcp` and `_nmos-registration._tcp`, so the status
bar can tell you a registry is present even when it advertises no Query API — the Query API is
a separate service on a different port, and a registry that only advertises registration cannot
be read by this tool.

**Manual** takes `host`, `host:port`, or a whole URL pasted out of a browser
(`http://10.0.0.5:8235/x-nmos/query/v1.3/` is accepted and trimmed back to the root). Tick
**HTTPS** for a secure registry, and **Ignore certificate errors** for a self-signed one.

**API version** defaults to the highest the registry advertises. Pin it if you need a specific
one. **Poll (ms)** is how often the registry is re-read; 1000 ms is a good default and is well
inside what a UMD needs.

Polling is deliberate rather than an IS-04 websocket subscription: it is stateless, so a
registry restart or a dropped link simply heals on the next tick with no resubscription. Only
receivers and senders are re-read every poll — devices, nodes, flows and sources change rarely
and are refreshed every 15 seconds, or immediately when a route appears that references
something not yet known.

Every collection is read a page at a time and the `Link: rel="next"` chain is followed to the
end. This is not optional: the Query API pages its responses and a registry's default page is
small — nmos-cpp serves ten — so a single plain GET returns the ten most recently updated
receivers and silently drops the rest, which looks exactly like a registry that only has ten
receivers in it.

### Mapping receivers to displays

**Add receivers...** lists every receiver in the registry with its device and what it is
currently routed from, and lays the ones you tick onto consecutive UMD addresses. Filter first
to pick out a single multiviewer. Receivers are listed in natural order, so "In 2" comes before
"In 10" rather than after it.

**Add blank row** adds one row to fill in by hand; the receiver column is a drop-down of
everything in the registry.

The table then shows, live:

| Column | Meaning |
|---|---|
| On | Whether this display is driven |
| Addr | TSL display address — 0–126 on 3.1 and 4.0, 0–65534 on 5.0 |
| NMOS receiver | The receiver this display follows |
| Routed source | What the receiver is fed by, black when routed, amber when idle, red when the receiver itself has gone from the registry |
| UMD text | Exactly what is being sent, after the template and the length fit |
| Template override | A template for that row alone |

**Save** writes to `%APPDATA%\NmosUmd\config.json`, which is also loaded at start-up and saved
on exit. **Export...** and **Import...** move a mapping between machines.

### Label

**Routed** is the template used when the receiver has an active sender; **Unrouted** when it
does not. The defaults are `{sender.label|sender.id}` and `Parked`.

A template is plain text with `{tokens}` in it. A token can list alternatives separated by `|`,
and the first one that resolves to something wins — so `{sender.label|sender.id}` shows the
sender's name, falling back to its id for a device registered without a label. A quoted
alternative is literal text: `{source.label|"NO SOURCE"}`.

| Token | Value |
|---|---|
| `{sender.label}` `{sender.description}` `{sender.id}` | The sender feeding this receiver |
| `{sender.device}` `{sender.node}` | The device and node the sender belongs to |
| `{sender.grouphint}` `{sender.transport}` | Group hint tag, and transport (`rtp.mcast`) |
| `{source.label}` `{flow.label}` `{flow.format}` | Walked back from the sender via its flow |
| `{receiver.label}` `{receiver.device}` `{receiver.node}` … | The receiver itself |
| `{addr}` `{addr:000}` | The UMD address, optionally zero padded |

**Too long** decides what happens when the text will not fit the 16 characters of 3.1 and 4.0.
Taking `PCAP Replay THEBEAST:3210`:

| Mode | Result | Behaviour |
|---|---|---|
| Truncate | `PCAP Replay THEB` | Keep the first 16 characters |
| Keep the end | `THEBEAST:3210` | Keep the tail, dropping a leading part-word |
| Squeeze (drop vowels) | `PCPRplyTHBS:3210` | Drop vowels from the longest word first, then close up the spaces, then trim letters off the end |

Squeeze never drops a digit, and never drops a letter that sits against one — `1080i25` stays
`1080i25` rather than becoming `108025`, which would read as a different, entirely plausible
number.

**Upper case** applies to everything sent.

### TSL output

**Destination** is the display or multiviewer, over UDP or TCP. 8900 is a common default port;
5.0 does not mandate one. UDP broadcast addresses work.

**Protocol** picks the packet format. On 5.0 you can also set the screen index or address all
screens (`0xFFFF`), and send text as UTF-16LE. On TCP, **stream framing** wraps each packet in
the DLE/STX framing from the 5.0 spec.

**Lamps** drive the tally lamps from the routing state rather than from any tally source: pick a
colour for routed and for unrouted, so an idle input can show red or an active one green. 3.1
lamps are only on or off. **Drive text tally** extends the same colour to the text tally on 4.0
and 5.0.

**Repeat every (ms)** is the keepalive: each display is re-sent its current label on this
interval, which is what recovers a display that was powered down or a receiver that reconnected.
A label that *changes* does not wait for it — a changed display jumps the queue and goes out
within one tick, typically under half a second including the registry poll.

Packets are spaced evenly rather than sent as a burst: 32 displays on a five second repeat is
one packet every 156 ms. Changed displays are spaced the same way, so re-routing the whole wall
at once does not turn into a burst either.

That spacing matters over TCP. A burst of packets coalesces into a single read at the far end,
and a receiver that treats each read as one message parses the first packet and discards the
rest: Companion's TSL UMD listener does exactly this, so a burst of 32 displays reached it as 2.
Spacing them out costs nothing and keeps every display fed.

**Start output** begins sending; the status bar counts the packets. Everything sent is exactly
what the *UMD text* column shows.

### Starting on its own

The window connects and starts sending by itself: it browses for the registry, connects to the
first one it finds (or to the typed address), and starts the output as soon as the registry
answers and there is something mapped. Opening the application is all that is needed to put the
wall back up.

If the destination is not listening yet — a multiviewer still booting — the start is retried
every five seconds rather than failing with a dialog, and the log says so.

Pressing **Disconnect** or **Stop output** turns the automatic behaviour off for that session,
so nothing restarts behind you. A send that fails does *not* count as you stopping it: the
output stops, and comes back on its own when the destination will take packets again. Set
`"AutoStart": false` in `%APPDATA%\NmosUmd\config.json` to open idle instead.

## What gets read from the registry

A receiver's `subscription` object carries `active` and `sender_id`. When both are set and that
sender is in the registry, the receiver is routed and the sender's resources are available for
the template. Three other cases are handled explicitly:

- **Not routed** — `active` is false, or there is no `sender_id`. The unrouted template applies.
- **Routed to a sender the registry does not hold** — the far node has unregistered, or the
  receiver was patched with a raw SDP rather than by IS-05. The table says so; the display shows
  the unrouted text rather than a stale name.
- **The receiver itself is missing** — its node is offline or the mapping is stale. The row goes
  red and keeps the receiver's saved label so you can still see which input it is.

Because the tool reads the **registry** rather than a node, a receiver on one node routed to a
sender on another resolves correctly — which asking a single node could not do.

## Source layout

| File | Contents |
|---|---|
| [Program.cs](Program.cs) | Entry point |
| [MainForm.cs](MainForm.cs) | The window, the mapping table and the send loop |
| [ReceiverPickerForm.cs](ReceiverPickerForm.cs) | "Add receivers" dialog |
| [Nmos/MdnsBrowser.cs](Nmos/MdnsBrowser.cs) | DNS-SD browse over mDNS |
| [Nmos/DnsMessage.cs](Nmos/DnsMessage.cs) | The DNS wire format the browse needs |
| [Nmos/NmosQueryClient.cs](Nmos/NmosQueryClient.cs) | IS-04 Query API client |
| [Nmos/NmosResources.cs](Nmos/NmosResources.cs) | Resource model and route resolution |
| [Nmos/RegistryMonitor.cs](Nmos/RegistryMonitor.cs) | Poll loop, reconnection and backoff |
| [App/LabelFormatter.cs](App/LabelFormatter.cs) | Templates and the length fit |
| [App/UmdEngine.cs](App/UmdEngine.cs) | What each display should be showing |
| [App/AppConfig.cs](App/AppConfig.cs) | Saved settings and mapping |
| [Tsl/TslPacketBuilder.cs](Tsl/TslPacketBuilder.cs) | Packet encoding for 3.1, 4.0 and 5.0 |
| [Net/Senders.cs](Net/Senders.cs) | UDP and TCP transports |

The TSL packet encoding, framing and transports are taken from the
[TSL UMD Tester](https://github.com/chriscglover/TSL-UMD-Tester); its README documents the
packet layouts byte by byte.

No NuGet packages and no Bonjour SDK: the mDNS browse is implemented directly on UDP multicast,
so deployment is copying the executable.

## Dependency-free mDNS

One socket is opened per IPv4 interface, bound to port 5353 where Windows allows it so the
multicast responses to anyone's query are seen. Where that bind is refused — Bonjour or another
resolver holding the port exclusively — the socket falls back to an ephemeral port and sets the
QU bit to ask for unicast responses instead. Queries are re-sent at 0, 1, 3 and 10 seconds and
then every 30, so a registry that appears later still turns up.

## Licence

[MIT](LICENSE)
