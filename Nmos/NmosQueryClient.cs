using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NmosUmd.Nmos;

/// <summary>
/// Reads an IS-04 Query API. The registry holds every node's resources, which is exactly what
/// this application needs: a receiver on one node is almost always routed to a sender on
/// another, so asking a single node would only ever resolve half of the picture.
/// </summary>
public sealed class NmosQueryClient : IDisposable
{
    private static readonly string[] KnownVersions = { "v1.0", "v1.1", "v1.2", "v1.3" };

    /// <summary>
    /// Page size asked of the registry. A registry caps this at its own maximum and says what it
    /// actually used in X-Paging-Limit, so asking for more than it allows costs nothing.
    /// </summary>
    private const int PageLimit = 100;

    /// <summary>Runaway guard: 200 pages of 100 is far more than any real registry holds.</summary>
    private const int MaxPages = 200;

    private static readonly Regex NextLinkPattern =
        new(@"<(?<url>[^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    /// <summary>Registry root, e.g. "http://10.119.30.10:8235/".</summary>
    public Uri Root { get; }

    /// <summary>API version in use, e.g. "v1.3". Set by <see cref="ConnectAsync"/>.</summary>
    public string ApiVersion { get; private set; } = string.Empty;

    /// <summary>Versioned Query API base, e.g. "http://host:8235/x-nmos/query/v1.3/".</summary>
    public Uri? QueryBase { get; private set; }

    public NmosQueryClient(string hostPort, bool https = false, bool allowInvalidCertificates = false,
                           TimeSpan? timeout = null, Action<string>? log = null)
    {
        Root = BuildRoot(hostPort, https);
        _log = log;

        var handler = new HttpClientHandler();
        if (allowInvalidCertificates)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("User-Agent", "NmosUmd/1.0");
    }

    /// <summary>
    /// Turns whatever the user typed into a registry root. Accepts "10.0.0.1", "10.0.0.1:8235",
    /// "http://host:8235" and a full "http://host:8235/x-nmos/query/v1.3/" pasted from a browser,
    /// because all four are things people reach for.
    /// </summary>
    public static Uri BuildRoot(string hostPort, bool https)
    {
        var text = (hostPort ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException("No registry address given.", nameof(hostPort));

        if (!text.Contains("://", StringComparison.Ordinal))
            text = (https ? "https://" : "http://") + text;

        var uri = new Uri(text, UriKind.Absolute);

        // Drop any /x-nmos/... path so only scheme, host and port survive.
        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>Picks the highest API version the registry advertises and proves it responds.</summary>
    public async Task ConnectAsync(string? forcedVersion, CancellationToken token)
    {
        var root = new Uri(Root, "x-nmos/query/");

        if (!string.IsNullOrWhiteSpace(forcedVersion))
        {
            ApiVersion = forcedVersion!.Trim().TrimEnd('/');
        }
        else
        {
            var versions = await GetJsonAsync(root, token).ConfigureAwait(false);
            var available = versions.RootElement.EnumerateArray()
                .Select(v => v.GetString()?.Trim('/') ?? string.Empty)
                .Where(v => v.Length > 0)
                .ToList();

            if (available.Count == 0)
                throw new InvalidOperationException($"{root} returned no API versions.");

            ApiVersion = available.OrderBy(VersionKey).Last();
            _log?.Invoke($"Registry offers {string.Join(", ", available)}; using {ApiVersion}.");
        }

        QueryBase = new Uri(root, ApiVersion + "/");

        // A HEAD-style probe: if the version is wrong this throws now rather than on every poll.
        using var _ = await GetJsonAsync(new Uri(QueryBase, "receivers"), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the resources needed to describe what each receiver is fed by.
    /// </summary>
    /// <param name="includeSlowResources">
    /// Fetch devices, nodes, flows and sources as well. Those change rarely, so the poll loop
    /// refreshes them occasionally rather than every time round, which keeps a big registry
    /// from being hammered for data that has not moved.
    /// </param>
    public async Task<RegistrySnapshot> FetchAsync(bool includeSlowResources, RegistrySnapshot previous,
                                                   CancellationToken token)
    {
        if (QueryBase is null) throw new InvalidOperationException("Not connected.");

        var receiversTask = FetchAllAsync("receivers", ParseReceiver, token);
        var sendersTask = FetchAllAsync("senders", ParseSender, token);

        var devicesTask = includeSlowResources ? FetchAllAsync("devices", ParseDevice, token) : null;
        var nodesTask = includeSlowResources ? FetchAllAsync("nodes", ParseNode, token) : null;
        var flowsTask = includeSlowResources ? FetchAllAsync("flows", ParseFlow, token) : null;
        var sourcesTask = includeSlowResources ? FetchAllAsync("sources", ParseSource, token) : null;

        var receivers = await receiversTask.ConfigureAwait(false);
        var senders = Index(await sendersTask.ConfigureAwait(false), s => s.Id);

        IReadOnlyDictionary<string, NmosDevice> devices = previous.Devices;
        IReadOnlyDictionary<string, NmosNode> nodes = previous.Nodes;
        IReadOnlyDictionary<string, NmosFlow> flows = previous.Flows;
        IReadOnlyDictionary<string, NmosSource> sources = previous.Sources;

        if (includeSlowResources)
        {
            devices = Index(await devicesTask!.ConfigureAwait(false), d => d.Id);
            nodes = Index(await nodesTask!.ConfigureAwait(false), n => n.Id);
            flows = Index(await flowsTask!.ConfigureAwait(false), f => f.Id);
            sources = Index(await sourcesTask!.ConfigureAwait(false), s => s.Id);
        }

        return new RegistrySnapshot
        {
            Receivers = receivers,
            Senders = senders,
            Devices = devices,
            Nodes = nodes,
            Flows = flows,
            Sources = sources,
            FetchedUtc = DateTime.UtcNow
        };
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ http

    /// <summary>
    /// Reads every page of a resource collection.
    ///
    /// The Query API pages its responses, and a registry's default page is small - nmos-cpp
    /// serves ten. A single unpaged GET therefore returns only the ten most recently updated
    /// receivers and quietly drops the rest, which looks exactly like a registry that has ten
    /// receivers in it. So ask for a large page, and follow the "next" links until the registry
    /// returns one it has not filled.
    /// </summary>
    private async Task<List<T>> FetchAllAsync<T>(string resource, Func<JsonElement, T> parse,
                                                 CancellationToken token)
    {
        var items = new List<T>();
        var uri = new Uri(QueryBase!, resource + "?paging.limit=" + PageLimit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = 0;

        while (true)
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                                            .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            using var document = await ReadJsonAsync(response, token).ConfigureAwait(false);

            var count = 0;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    count++;
                    if (element.ValueKind == JsonValueKind.Object) items.Add(parse(element));
                }
            }

            // A page the registry did not fill is the last one there is to read.
            if (count < EffectiveLimit(response)) break;

            var next = NextPage(response, resource);
            if (next is null || !seen.Add(next.PathAndQuery)) break;

            if (++pages >= MaxPages)
            {
                _log?.Invoke($"Stopped reading {resource} after {MaxPages} pages; some may be missing.");
                break;
            }

            uri = next;
        }

        return items;
    }

    /// <summary>The page size the registry actually applied, having capped ours to its maximum.</summary>
    private static int EffectiveLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Paging-Limit", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var limit) && limit > 0)
        {
            return limit;
        }

        return PageLimit;
    }

    /// <summary>
    /// The rel="next" Link, rebuilt onto the address we are actually talking to. A registry
    /// behind NAT, or one that only knows its own container hostname, advertises links we could
    /// not reach - but the cursor in its query string is exactly what we need.
    /// </summary>
    private Uri? NextPage(HttpResponseMessage response, string resource)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;

        var match = NextLinkPattern.Match(string.Join(", ", values));
        if (!match.Success) return null;

        var url = match.Groups["url"].Value;
        var question = url.IndexOf('?');
        if (question < 0) return null;

        return new Uri(QueryBase!, resource + url[question..]);
    }

    /// <summary>
    /// Indexes by id, last one winning. Paging is a cursor over live data, so a resource that is
    /// updated mid-read can legitimately appear on two pages; that is not a reason to throw.
    /// </summary>
    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> id)
    {
        var map = new Dictionary<string, T>();
        foreach (var item in items) map[id(item)] = item;
        return map;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, default, token).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                                        .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{uri} returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, default, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ parsing

    private static NmosReceiver ParseReceiver(JsonElement e)
    {
        var active = false;
        string? senderId = null;

        if (e.TryGetProperty("subscription", out var subscription) &&
            subscription.ValueKind == JsonValueKind.Object)
        {
            if (subscription.TryGetProperty("active", out var activeElement) &&
                activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                active = activeElement.GetBoolean();
            }

            if (subscription.TryGetProperty("sender_id", out var senderElement) &&
                senderElement.ValueKind == JsonValueKind.String)
            {
                senderId = senderElement.GetString();
            }
        }

        return new NmosReceiver
        {
            Id = String(e, "id"),
            Label = String(e, "label"),
            Description = String(e, "description"),
            GroupHint = GroupHint(e),
            DeviceId = String(e, "device_id"),
            Format = String(e, "format"),
            Transport = String(e, "transport"),
            SubscriptionActive = active,
            SenderId = senderId
        };
    }

    private static NmosSender ParseSender(JsonElement e) => new()
    {
        Id = String(e, "id"),
        Label = String(e, "label"),
        Description = String(e, "description"),
        GroupHint = GroupHint(e),
        DeviceId = String(e, "device_id"),
        FlowId = String(e, "flow_id"),
        Transport = String(e, "transport"),
        ManifestHref = String(e, "manifest_href")
    };

    private static NmosFlow ParseFlow(JsonElement e) => new()
    {
        Id = String(e, "id"),
        Label = String(e, "label"),
        Description = String(e, "description"),
        GroupHint = GroupHint(e),
        SourceId = String(e, "source_id"),
        DeviceId = String(e, "device_id"),
        Format = String(e, "format")
    };

    private static NmosSource ParseSource(JsonElement e) => new()
    {
        Id = String(e, "id"),
        Label = String(e, "label"),
        Description = String(e, "description"),
        GroupHint = GroupHint(e),
        DeviceId = String(e, "device_id"),
        Format = String(e, "format")
    };

    private static NmosDevice ParseDevice(JsonElement e) => new()
    {
        Id = String(e, "id"),
        Label = String(e, "label"),
        Description = String(e, "description"),
        GroupHint = GroupHint(e),
        NodeId = String(e, "node_id")
    };

    private static NmosNode ParseNode(JsonElement e) => new()
    {
        Id = String(e, "id"),
        Label = String(e, "label"),
        Description = String(e, "description"),
        GroupHint = GroupHint(e),
        Hostname = String(e, "hostname"),
        Href = String(e, "href")
    };

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Group hints ("MC-MV-07 Input 13:2022-6 0") are how many devices express the operational
    /// name of a port, so they are worth exposing to the label template alongside the label.
    /// </summary>
    private static string GroupHint(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var tag in tags.EnumerateObject())
        {
            if (!tag.Name.StartsWith("urn:x-nmos:tag:grouphint", StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.Value.ValueKind != JsonValueKind.Array) continue;

            foreach (var value in tag.Value.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static (int Major, int Minor) VersionKey(string version)
    {
        var text = version.TrimStart('v', 'V');
        var parts = text.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    /// <summary>Versions this tool has been written against, for the drop-down.</summary>
    public static IReadOnlyList<string> SupportedVersions => KnownVersions;
}
