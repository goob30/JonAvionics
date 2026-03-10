using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp.Server;
using JonAvionics;
using JonAvionics.providers;

string aircraftType = "fbw"; // "fbw" or "pmdg"

// WebSocket server for local HTML preview
GlobalServer.Wssv = new WebSocketServer("ws://0.0.0.0:8381");
GlobalServer.Wssv.AddWebSocketService<McduDataBroadcaster>("/mcdu");
GlobalServer.Wssv.Start();
Console.WriteLine("Main Server: WebSocket server started on ws://0.0.0.0:8381");

// Raw TCP JSON server for the Pi
var tcpBroadcaster = new TcpJsonBroadcaster(8382);
_ = Task.Run(() => tcpBroadcaster.StartAsync());
Console.WriteLine("Main Server: TCP JSON server started on 0.0.0.0:8382");

// HTTP server for browser preview
var exePath = AppDomain.CurrentDomain.BaseDirectory;
var frontendPath = Path.GetFullPath(Path.Combine(exePath, @"..\..\..\frontend"));
string defaultHtmlFile = (aircraftType == "fbw") ? "mcdu_fbw.html" : "mcdu_pmdg.html";
var httpFileServer = new HttpFileServer("http://localhost:8000/", frontendPath, defaultHtmlFile);
_ = Task.Run(() => httpFileServer.Start());

// Provider setup
IFmsDataProvider provider;
if (aircraftType == "fbw")
{
    Console.WriteLine("Main Server: Starting provider for 'fbw'");
    provider = new FbwDataProvider();
}
else if (aircraftType == "pmdg")
{
    Console.WriteLine("Main Server: Starting provider for 'pmdg'");
    var config = new PmdgConfigurator();
    config.EnsureSdkEnabled();
    provider = new PmdgDataProvider();
}
else
{
    Console.WriteLine($"Error: Unknown aircraft type '{aircraftType}'.");
    Console.ReadKey();
    return;
}

// Queue-backed sender instead of polling latestJson
var sendQueue = new McduSendQueue();

// Provider callback: enqueue immediately
provider.OnDataReceived += (jsonData) =>
{
    Console.WriteLine($"PROGRAM_IN {Environment.TickCount64}");

    GlobalServer.Wssv?.WebSocketServices["/mcdu"]?.Sessions.Broadcast(jsonData);
    sendQueue.Enqueue(jsonData);
};

// Dedicated sender task
_ = Task.Run(async () =>
{
    while (true)
    {
        string jsonData = await sendQueue.DequeueAsync();

        Console.WriteLine($"TCP_SEND {Environment.TickCount64}");
        tcpBroadcaster.BroadcastLine(jsonData);
    }
});

// Start provider
_ = Task.Run(() => provider.Start());

// Keep app alive
Console.WriteLine("All services started. The application is now running.");
Console.WriteLine("Browser preview: http://localhost:8000");
Console.WriteLine("Pi TCP feed: tcp://<PC-IP>:8382");
Console.ReadKey();

public static class GlobalServer
{
    public static WebSocketServer? Wssv;
}

public class McduDataBroadcaster : WebSocketBehavior
{
}

public sealed class McduSendQueue
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void Enqueue(string json)
    {
        _queue.Enqueue(json);
        _signal.Release();
    }

    public async Task<string> DequeueAsync()
    {
        await _signal.WaitAsync();

        while (true)
        {
            if (_queue.TryDequeue(out var item))
                return item;
        }
    }
}

public class TcpJsonBroadcaster
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();

    public TcpJsonBroadcaster(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public async Task StartAsync()
    {
        _listener.Start();

        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            _clients.TryAdd(client, 0);

            _ = Task.Run(() => WatchClientAsync(client));
            Console.WriteLine($"TCP client connected: {client.Client.RemoteEndPoint}");
        }
    }

    private async Task WatchClientAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[1];

            while (client.Connected)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (n <= 0) break;
            }
        }
        catch
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
            try { client.Close(); } catch { }
            Console.WriteLine("TCP client disconnected.");
        }
    }

    public void BroadcastLine(string line)
    {
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");

        foreach (var kv in _clients)
        {
            TcpClient client = kv.Key;

            try
            {
                if (!client.Connected)
                {
                    _clients.TryRemove(client, out _);
                    continue;
                }

                var stream = client.GetStream();
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch
            {
                _clients.TryRemove(client, out _);
                try { client.Close(); } catch { }
            }
        }
    }
}