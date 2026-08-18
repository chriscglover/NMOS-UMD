using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NmosUmd.Nmos;

/// <summary>One discovered DNS-SD service instance, with the IS-04 TXT keys parsed out.</summary>
public sealed class MdnsService
{
    /// <summary>Full instance name, e.g. "registry._nmos-query._tcp.local".</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>The instance label on its own, e.g. "registry".</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Bare service type, e.g. "_nmos-query._tcp".</summary>
    public string ServiceType { get; init; } = string.Empty;

    public string HostName { get; init; } = string.Empty;
    public IPAddress? Address { get; init; }
    public int Port { get; init; }
    public IReadOnlyDictionary<string, string> Txt { get; init; } = new Dictionary<string, string>();

    // Parsed from the TXT record, per the IS-04 DNS-SD binding.
    public int Priority { get; init; } = 100;             // "pri", lower wins
    public string ApiProto { get; init; } = "http";       // "api_proto"
    public string ApiAuth { get; init; } = "false";       // "api_auth"
    public IReadOnlyList<string> ApiVersions { get; init; } = Array.Empty<string>();

    public bool IsResolved => Address is not null && Port > 0;

    /// <summary>Host:port as it should be typed into the manual box.</summary>
    public string HostPort => Address is null ? string.Empty : $"{Address}:{Port}";

    public string VersionSummary => ApiVersions.Count == 0 ? "unknown" : string.Join(", ", ApiVersions);

    public override string ToString()
    {
        var where = IsResolved ? HostPort : "unresolved";
        var proto = ApiProto == "http" ? string.Empty : $" {ApiProto}";
        return $"{DisplayName}  [{where}]  {VersionSummary}{proto}";
    }
}

/// <summary>
/// Browses DNS-SD over mDNS for NMOS services and keeps a live list of what is out there.
///
/// Registries come and go, so this browses continuously rather than doing a one-shot query -
/// a registry that appears a second after the window opened still turns up in the list.
///
/// One socket is opened per IPv4 interface, bound to port 5353 where the OS allows it so the
/// normal multicast responses are seen. Where the bind is refused the socket falls back to an
/// ephemeral port and asks for unicast responses (the QU bit) instead, which still works but
/// only sees answers to our own queries.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    public const string QueryServiceType = "_nmos-query._tcp";
    public const string RegisterServiceType = "_nmos-register._tcp";
    public const string RegistrationServiceType = "_nmos-registration._tcp";

    /// <summary>
    /// The Query API type is what this application needs. IS-04 also has two names for the
    /// Registration API - the long one up to v1.2 and the short one from v1.3 - which are
    /// browsed as well so the log can say "there is a registry here, but it advertises no
    /// Query API" rather than showing nothing at all.
    /// </summary>
    public static IReadOnlyList<string> DefaultServiceTypes { get; } = new[]
    {
        QueryServiceType,
        RegisterServiceType,
        RegistrationServiceType
    };

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastGroup, 5353);

    private readonly object _gate = new();
    private readonly List<string> _serviceTypes;
    private readonly List<Socket> _sockets = new();
    private readonly Dictionary<string, ServiceEntry> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private bool _anyMulticastSocket;

    public MdnsBrowser(IEnumerable<string>? serviceTypes = null, Action<string>? log = null)
    {
        _serviceTypes = (serviceTypes ?? DefaultServiceTypes).ToList();
        _log = log;
    }

    /// <summary>Raised on the browser's own threads whenever the service list changes.</summary>
    public event Action? Updated;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) { Refresh(); return; }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        foreach (var local in LocalAddresses())
        {
            var socket = TryOpen(local, out var multicast);
            if (socket is null) continue;

            _anyMulticastSocket |= multicast;
            _sockets.Add(socket);
            _ = Task.Run(() => ReceiveLoopAsync(socket, token), token);
        }

        if (_sockets.Count == 0)
        {
            _log?.Invoke("mDNS: no usable network interface found; use the manual registry address.");
            return;
        }

        IsRunning = true;
        _ = Task.Run(() => QueryLoopAsync(token), token);
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* shutting down */ }

        foreach (var socket in _sockets)
        {
            try { socket.Close(); } catch { /* shutting down */ }
            socket.Dispose();
        }

        _sockets.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Re-sends the queries now, for a "rescan" button.</summary>
    public void Refresh()
    {
        if (!IsRunning) { Start(); return; }
        SendQueries();
    }

    /// <summary>Forgets everything discovered so far, so a rescan starts from a clean list.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _services.Clear();
            _hosts.Clear();
        }
        Updated?.Invoke();
    }

    /// <summary>Snapshot of what has been discovered, best first.</summary>
    public IReadOnlyList<MdnsService> Services(string? serviceType = null)
    {
        lock (_gate)
        {
            return _services.Values
                .Where(e => serviceType is null || string.Equals(e.ServiceType, serviceType, StringComparison.OrdinalIgnoreCase))
                .Select(Snapshot)
                .OrderBy(s => s.IsResolved ? 0 : 1)
                .ThenBy(s => s.Priority)
                .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ sockets

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;
                yield return info.Address;
            }
        }
    }

    private Socket? TryOpen(IPAddress local, out bool multicast)
    {
        multicast = false;
        Socket? socket = null;

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.ExclusiveAddressUse = false;

            // Port 5353 lets us see the multicast responses everybody else's queries provoke.
            // Bonjour or the Windows DNS client may already hold it; SO_REUSEADDR normally
            // makes that fine, and where it does not we drop to an ephemeral port below.
            try
            {
                socket.Bind(new IPEndPoint(local, 5353));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastGroup, local));
                multicast = true;
            }
            catch (SocketException)
            {
                socket.Dispose();
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(local, 0));
                _log?.Invoke($"mDNS: port 5353 unavailable on {local}, using unicast responses.");
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            return socket;
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            _log?.Invoke($"mDNS: {local} unusable ({ex.Message}).");
            return null;
        }
    }

    private async Task QueryLoopAsync(CancellationToken token)
    {
        // Front-loaded: answer quickly when the window opens, then settle down to a slow
        // refresh that picks up a registry which appeared later.
        var delays = new[] { 0, 1000, 3000, 10000 };
        var index = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var delay = index < delays.Length ? delays[index++] : 30000;
                if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
                SendQueries();
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private void SendQueries()
    {
        var names = _serviceTypes.Select(t => t.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? t : t + ".local").ToList();
        var packet = DnsMessage.BuildQuery(names, unicastResponse: !_anyMulticastSocket);

        foreach (var socket in _sockets.ToArray())
        {
            try { socket.SendTo(packet, MulticastEndpoint); }
            catch (Exception ex) { _log?.Invoke($"mDNS query failed: {ex.Message}"); }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[9000];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            int received;
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote).ConfigureAwait(false);
                received = result.ReceivedBytes;
                remote = result.RemoteEndPoint;
            }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }

            if (received <= 0) continue;

            try
            {
                if (Ingest(DnsMessage.ParseRecords(buffer, received))) Updated?.Invoke();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"mDNS parse error: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ cache

    private sealed class ServiceEntry
    {
        public string Instance = string.Empty;
        public string DisplayName = string.Empty;
        public string ServiceType = string.Empty;
        public string HostName = string.Empty;
        public int Port;
        public Dictionary<string, string> Txt = new(StringComparer.OrdinalIgnoreCase);
    }

    private bool Ingest(List<DnsRecord> records)
    {
        var changed = false;

        lock (_gate)
        {
            foreach (var record in records)
            {
                switch (record.Type)
                {
                    case DnsRecordType.Ptr:
                    {
                        var type = TrimLocal(record.Name);
                        if (!_serviceTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) break;
                        if (string.IsNullOrEmpty(record.PtrTarget)) break;

                        if (record.Ttl == 0)
                        {
                            changed |= _services.Remove(record.PtrTarget);
                            break;
                        }

                        if (!_services.ContainsKey(record.PtrTarget))
                        {
                            _services[record.PtrTarget] = new ServiceEntry
                            {
                                Instance = record.PtrTarget,
                                DisplayName = InstanceLabel(record.PtrTarget, type),
                                ServiceType = type
                            };
                            changed = true;
                        }
                        break;
                    }

                    case DnsRecordType.Srv:
                    {
                        if (!TryGetOrCreate(record.Name, out var entry)) break;
                        var host = TrimTrailingDot(record.SrvTarget ?? string.Empty);
                        if (entry.HostName != host || entry.Port != record.SrvPort)
                        {
                            entry.HostName = host;
                            entry.Port = record.SrvPort;
                            changed = true;
                        }
                        break;
                    }

                    case DnsRecordType.Txt:
                    {
                        if (!TryGetOrCreate(record.Name, out var entry)) break;
                        var txt = ParseTxt(record.TxtStrings);
                        if (!SameTxt(entry.Txt, txt))
                        {
                            entry.Txt = txt;
                            changed = true;
                        }
                        break;
                    }

                    case DnsRecordType.A:
                    {
                        if (record.Address is null) break;
                        var host = TrimTrailingDot(record.Name);
                        if (!_hosts.TryGetValue(host, out var existing) || !existing.Equals(record.Address))
                        {
                            _hosts[host] = record.Address;
                            changed = true;
                        }
                        break;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// SRV and TXT records arrive for instances we may not have seen a PTR for - a responder
    /// answering somebody else's query, for instance. Accept them when the name ends in one of
    /// the types we browse, so nothing is missed just because the PTR was dropped.
    /// </summary>
    private bool TryGetOrCreate(string instance, out ServiceEntry entry)
    {
        if (_services.TryGetValue(instance, out entry!)) return true;

        var type = _serviceTypes.FirstOrDefault(t =>
            TrimLocal(instance).EndsWith("." + t, StringComparison.OrdinalIgnoreCase));

        if (type is null) { entry = null!; return false; }

        entry = new ServiceEntry
        {
            Instance = instance,
            DisplayName = InstanceLabel(instance, type),
            ServiceType = type
        };
        _services[instance] = entry;
        return true;
    }

    private MdnsService Snapshot(ServiceEntry entry)
    {
        _hosts.TryGetValue(entry.HostName, out var address);

        entry.Txt.TryGetValue("pri", out var priText);
        entry.Txt.TryGetValue("api_proto", out var proto);
        entry.Txt.TryGetValue("api_auth", out var auth);
        entry.Txt.TryGetValue("api_ver", out var versions);

        return new MdnsService
        {
            Instance = entry.Instance,
            DisplayName = entry.DisplayName,
            ServiceType = entry.ServiceType,
            HostName = entry.HostName,
            Address = address,
            Port = entry.Port,
            Txt = new Dictionary<string, string>(entry.Txt, StringComparer.OrdinalIgnoreCase),
            Priority = int.TryParse(priText, out var pri) ? pri : 100,
            ApiProto = string.IsNullOrWhiteSpace(proto) ? "http" : proto,
            ApiAuth = string.IsNullOrWhiteSpace(auth) ? "false" : auth,
            ApiVersions = string.IsNullOrWhiteSpace(versions)
                ? Array.Empty<string>()
                : versions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }

    private static Dictionary<string, string> ParseTxt(List<string>? strings)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (strings is null) return txt;

        foreach (var item in strings)
        {
            var split = item.IndexOf('=');
            if (split <= 0) txt[item] = string.Empty;
            else txt[item[..split]] = item[(split + 1)..];
        }
        return txt;
    }

    private static bool SameTxt(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string TrimLocal(string name)
    {
        name = TrimTrailingDot(name);
        return name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
    }

    private static string TrimTrailingDot(string name) => name.TrimEnd('.');

    private static string InstanceLabel(string instance, string serviceType)
    {
        var trimmed = TrimLocal(instance);
        var suffix = "." + serviceType;
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length]
            : trimmed;
    }
}
