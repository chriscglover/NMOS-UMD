using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NmosUmd.Nmos;

/// <summary>How the connection to the registry is doing, for the status bar.</summary>
public enum RegistryState
{
    Stopped,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// Keeps a current picture of the registry by polling the Query API.
///
/// Polling rather than an IS-04 websocket subscription: a poll is stateless, so a registry
/// restart or a dropped link heals itself on the next tick with no resubscription dance, and
/// at one second the latency is well inside what a UMD needs. The cost is one HTTP round trip
/// per tick, which the slow-resource split below keeps small.
/// </summary>
public sealed class RegistryMonitor : IDisposable
{
    /// <summary>Devices, nodes, flows and sources are re-read on this interval, not every poll.</summary>
    private static readonly TimeSpan SlowResourceInterval = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private NmosQueryClient? _client;

    /// <summary>Raised off the UI thread each time a poll succeeds.</summary>
    public event Action<RegistrySnapshot>? SnapshotUpdated;

    /// <summary>Raised off the UI thread on any state change, with a human-readable reason.</summary>
    public event Action<RegistryState, string>? StateChanged;

    public event Action<string>? Log;

    public RegistryState State { get; private set; } = RegistryState.Stopped;

    public RegistrySnapshot Snapshot { get; private set; } = RegistrySnapshot.Empty;

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start(string hostPort, bool https, bool allowInvalidCertificates, string? forcedVersion,
                      int pollIntervalMs)
    {
        Stop();

        var cts = new CancellationTokenSource();
        _cts = cts;
        Snapshot = RegistrySnapshot.Empty;
        _worker = Task.Run(() => RunAsync(hostPort, https, allowInvalidCertificates, forcedVersion,
                                          Math.Max(200, pollIntervalMs), cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        var worker = _worker;

        _cts = null;
        _worker = null;

        if (cts is null) return;

        try { cts.Cancel(); } catch { /* already gone */ }
        try { worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        cts.Dispose();

        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }

        SetState(RegistryState.Stopped, "Disconnected.");
    }

    public void Dispose() => Stop();

    private async Task RunAsync(string hostPort, bool https, bool allowInvalidCertificates,
                                string? forcedVersion, int pollIntervalMs, CancellationToken token)
    {
        var backoffMs = 1000;

        while (!token.IsCancellationRequested)
        {
            NmosQueryClient? client = null;

            try
            {
                SetState(RegistryState.Connecting, $"Connecting to {hostPort}...");

                client = new NmosQueryClient(hostPort, https, allowInvalidCertificates,
                                             TimeSpan.FromSeconds(5), Log);
                await client.ConnectAsync(forcedVersion, token).ConfigureAwait(false);

                lock (_gate)
                {
                    _client?.Dispose();
                    _client = client;
                }

                SetState(RegistryState.Connected, $"Connected to {client.Root.Authority} ({client.ApiVersion}).");
                backoffMs = 1000;

                await PollAsync(client, pollIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(RegistryState.Error, Describe(ex));
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                }
                client?.Dispose();
            }

            if (token.IsCancellationRequested) break;

            // Back off so a registry that is down does not produce a log line every second,
            // but never so far that a registry coming back takes minutes to be noticed.
            try { await Task.Delay(backoffMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, 10000);
        }
    }

    private async Task PollAsync(NmosQueryClient client, int pollIntervalMs, CancellationToken token)
    {
        var slowClock = Stopwatch.StartNew();
        var needSlow = true;

        while (!token.IsCancellationRequested)
        {
            var includeSlow = needSlow || slowClock.Elapsed >= SlowResourceInterval;

            var snapshot = await client.FetchAsync(includeSlow, Snapshot, token).ConfigureAwait(false);

            if (includeSlow)
            {
                slowClock.Restart();
                needSlow = false;
            }
            else if (HasDanglingReference(snapshot))
            {
                // A sender or flow we have never seen has turned up mid-route: pull the slow
                // resources on the next tick so the label resolves rather than showing an id.
                needSlow = true;
            }

            Snapshot = snapshot;
            SnapshotUpdated?.Invoke(snapshot);

            try { await Task.Delay(pollIntervalMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>True when a routed receiver points at something the cached resources cannot resolve.</summary>
    private static bool HasDanglingReference(RegistrySnapshot snapshot)
    {
        foreach (var receiver in snapshot.Receivers)
        {
            if (string.IsNullOrEmpty(receiver.SenderId)) continue;
            if (!snapshot.Senders.TryGetValue(receiver.SenderId!, out var sender)) continue;

            if (!string.IsNullOrEmpty(sender.DeviceId) && !snapshot.Devices.ContainsKey(sender.DeviceId)) return true;
            if (!string.IsNullOrEmpty(sender.FlowId) && !snapshot.Flows.ContainsKey(sender.FlowId)) return true;
        }
        return false;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "Registry timed out.",
        AggregateException aggregate => Describe(aggregate.InnerExceptions.FirstOrDefault() ?? aggregate),
        _ => ex.Message
    };

    private void SetState(RegistryState state, string message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }
}
