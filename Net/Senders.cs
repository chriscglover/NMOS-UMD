using System;
using System.Net;
using System.Net.Sockets;

namespace NmosUmd.Net;

public interface IPacketSender : IDisposable
{
    string Description { get; }
    bool IsConnected { get; }
    void Send(byte[] data);
}

public sealed class UdpPacketSender : IPacketSender
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;

    public UdpPacketSender(IPEndPoint endpoint)
    {
        _endpoint = endpoint;
        _client = new UdpClient(endpoint.AddressFamily) { EnableBroadcast = true };
    }

    public string Description => $"UDP {_endpoint}";

    public bool IsConnected => true; // connectionless

    public void Send(byte[] data) => _client.Send(data, data.Length, _endpoint);

    public void Dispose() => _client.Dispose();
}

public sealed class TcpPacketSender : IPacketSender
{
    private readonly IPEndPoint _endpoint;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpPacketSender(IPEndPoint endpoint) => _endpoint = endpoint;

    public string Description => $"TCP {_endpoint}";

    public bool IsConnected => _client is { Connected: true };

    public void Connect(int timeoutMs = 3000)
    {
        Dispose();
        var client = new TcpClient(_endpoint.AddressFamily) { NoDelay = true };
        var async = client.BeginConnect(_endpoint.Address, _endpoint.Port, null, null);
        if (!async.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            client.Close();
            throw new TimeoutException($"Timed out connecting to {_endpoint}.");
        }

        client.EndConnect(async);
        _client = client;
        _stream = client.GetStream();
    }

    public void Send(byte[] data)
    {
        if (_stream is null || !IsConnected) Connect();
        _stream!.Write(data, 0, data.Length);
        _stream.Flush();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Close();
        _client = null;
    }
}
