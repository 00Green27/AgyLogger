using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgyLogger.Cli.Tests.E2E.Harness;

/// <summary>
/// Ephemeral in-memory mock server for simulating upstream APIs and verifying proxy pass-through.
/// Built strictly with standard .NET BCL TcpListener.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _listenTask;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public List<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)> ReceivedRequests { get; } = new();
    public Func<string, string, Dictionary<string, string>, byte[], (int StatusCode, Dictionary<string, string> Headers, byte[] Body)> Handler { get; set; }

    public TestHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        Handler = (_, _, _, _) => (200, new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, "OK"u8.ToArray());
        _listenTask = Task.Run(ListenLoopAsync);
    }

    public static async Task<TestHttpServer> StartAsync(
        Func<string, string, Dictionary<string, string>, byte[], (int StatusCode, Dictionary<string, string> Headers, byte[] Body)>? handler = null)
    {
        var server = new TestHttpServer();
        if (handler is not null)
        {
            server.Handler = handler;
        }
        await Task.Yield();
        return server;
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var buffer = new byte[8192];
                var bytesRead = await stream.ReadAsync(buffer, ct);
                if (bytesRead == 0) return;

                var raw = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd == -1) headerEnd = raw.Length;

                var lines = raw[..headerEnd].Split("\r\n");
                var requestLine = lines[0].Split(' ');
                var method = requestLine.Length > 0 ? requestLine[0] : "GET";
                var path = requestLine.Length > 1 ? requestLine[1] : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        var key = lines[i][..colon].Trim();
                        var val = lines[i][(colon + 1)..].Trim();
                        headers[key] = val;
                    }
                }

                byte[] body = [];
                var bodyStart = headerEnd + 4;
                if (bodyStart < bytesRead)
                {
                    var bodyLen = bytesRead - bodyStart;
                    body = new byte[bodyLen];
                    Array.Copy(buffer, bodyStart, body, 0, bodyLen);
                }

                lock (ReceivedRequests)
                {
                    ReceivedRequests.Add((method, path, headers, body));
                }

                var (status, respHeaders, respBody) = Handler(method, path, headers, body);
                var statusText = status switch
                {
                    200 => "OK",
                    400 => "Bad Request",
                    426 => "Upgrade Required",
                    429 => "Too Many Requests",
                    500 => "Internal Server Error",
                    502 => "Bad Gateway",
                    503 => "Service Unavailable",
                    _ => "Response"
                };

                var sb = new StringBuilder();
                sb.Append($"HTTP/1.1 {status} {statusText}\r\n");
                foreach (var (k, v) in respHeaders)
                {
                    sb.Append($"{k}: {v}\r\n");
                }
                if (!respHeaders.ContainsKey("Content-Length") && !respHeaders.ContainsKey("Transfer-Encoding"))
                {
                    sb.Append($"Content-Length: {respBody.Length}\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");

                var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
                await stream.WriteAsync(headerBytes, ct);
                if (respBody.Length > 0)
                {
                    await stream.WriteAsync(respBody, ct);
                }
                await stream.FlushAsync(ct);
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }
        _cts.Dispose();
    }
}