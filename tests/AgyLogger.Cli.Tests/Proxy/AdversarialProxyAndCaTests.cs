namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;
using AgyLogger.Cli.Tests.E2E.Harness;

using Xunit;

/// <summary>
/// Empirical challenge and stress tests for CertificateAuthority, ProxyServer, and HttpWireProtocol.
/// </summary>
public sealed class AdversarialProxyAndCaTests
{
    #region 1. CertificateAuthority Challenges

    [Fact]
    public void DisposedCa_AllOperationsThrowObjectDisposedException()
    {
        var ca = CertificateAuthority.CreateInMemory(storeName: "AgyLoggerTest");
        ca.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = ca.RootCertificate);
        Assert.Throws<ObjectDisposedException>(() => ca.GetOrCreateLeafCertificate("example.com"));
        Assert.Throws<ObjectDisposedException>(() => ca.ExportRootCertificatePem());
        Assert.Throws<ObjectDisposedException>(() => ca.ExportRootCertificatePem(Path.GetTempFileName()));
        Assert.Throws<ObjectDisposedException>(() => ca.ExportRootCertPem(Path.GetTempFileName()));
        Assert.Throws<ObjectDisposedException>(() => ca.TrustRootCertificate());
        Assert.Throws<ObjectDisposedException>(() => ca.TrustInCurrentUserStore());
        Assert.Throws<ObjectDisposedException>(() => ca.UntrustRootCertificate());
        Assert.Throws<ObjectDisposedException>(() => ca.IsRootCertificateTrusted());

        // Repeated dispose must be safe and idempotent
        ca.Dispose();
        ca.Dispose();
    }

    [Fact]
    public async Task ConcurrentLeafCertificateRequests_MultipleHosts_AllValidAndCached()
    {
        using var ca = CertificateAuthority.CreateInMemory(storeName: "AgyLoggerTest");
        var hosts = new[] { "host1.example.com", "host2.example.com", "host3.example.com", "127.0.0.1", "::1" };

        var tasks = Enumerable.Range(0, 50).Select(i =>
        {
            var host = hosts[i % hosts.Length];
            return Task.Run(() => (Host: host, Cert: ca.GetOrCreateLeafCertificate(host)));
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var host in hosts)
        {
            var certsForHost = results.Where(r => r.Host == host).Select(r => r.Cert).ToList();
            Assert.NotEmpty(certsForHost);

            var first = certsForHost[0];
            Assert.NotNull(first);
            Assert.True(first.HasPrivateKey);

            // All concurrent requests for the same host must return the identical cached instance
            Assert.All(certsForHost, cert => Assert.Same(first, cert));

            // Verify certificate is signed by this CA
            Assert.Equal(ca.RootCertificate.Subject, first.Issuer);
        }
    }

    [Fact]
    public void LeafCertificateRequests_InvalidHostNames_ThrowsArgumentExceptionWithoutCorruptingCache()
    {
        using var ca = CertificateAuthority.CreateInMemory(storeName: "AgyLoggerTest");

        Assert.Throws<ArgumentException>(() => ca.GetOrCreateLeafCertificate(""));
        Assert.Throws<ArgumentException>(() => ca.GetOrCreateLeafCertificate("   "));

        // Subsequent valid request succeeds
        var validCert = ca.GetOrCreateLeafCertificate("valid.example.com");
        Assert.NotNull(validCert);
    }

    [Fact]
    public void HeadlessTrustStoreExecution_RemainsStableWithoutModalPrompts()
    {
        const string isolatedStore = "AgyLoggerTest";
        using var ca = CertificateAuthority.CreateInMemory(storeName: isolatedStore);

        try
        {
            // Initial trust state
            Assert.False(ca.IsRootCertificateTrusted());

            // Trust in isolated test store executes silently without Windows system warning modal
            var trustResult = ca.TrustRootCertificate();
            Assert.True(trustResult);
            Assert.True(ca.IsRootCertificateTrusted());

            // Untrust removes cert silently
            var untrustResult = ca.UntrustRootCertificate();
            Assert.True(untrustResult);
            Assert.False(ca.IsRootCertificateTrusted());
        }
        finally
        {
            ca.UntrustRootCertificate();
        }

        // Read-only inspection of the real default OS Root store is non-invasive and non-prompting
        using var defaultCa = CertificateAuthority.CreateInMemory();
        Assert.False(defaultCa.IsRootCertificateTrusted());
    }

    #endregion

    #region 2. HttpWireProtocol Bounds & Malformed Request Challenges

    [Fact]
    public async Task ReadHttpRequestAsync_HeaderExceeding64KB_ThrowsInvalidDataException()
    {
        // 65 KB header
        var largeHeaderName = "X-Large-Header";
        var largeHeaderVal = new string('A', 65 * 1024);
        var rawData = $"GET /test HTTP/1.1\r\nHost: example.com\r\n{largeHeaderName}: {largeHeaderVal}\r\n\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rawData));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadHttpRequestAsync_HeaderJustUnder64KB_SuccessfullyParses()
    {
        // 60 KB header
        var headerVal = new string('B', 60 * 1024);
        var rawData = $"POST /api/v1 HTTP/1.1\r\nHost: example.com\r\nX-Padding: {headerVal}\r\nContent-Length: 4\r\n\r\nTest";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rawData));

        var parsed = await HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None);

        Assert.NotNull(parsed);
        Assert.Equal("POST", parsed.Method);
        Assert.Equal("/api/v1", parsed.Path);
        Assert.Equal("example.com", parsed.Headers["Host"]);
        Assert.Equal("Test", Encoding.UTF8.GetString(parsed.Body));
    }

    [Fact]
    public async Task ReadHttpResponseHeadersAsync_HeaderExceeding64KB_ThrowsInvalidDataException()
    {
        var largeHeaderVal = new string('C', 65 * 1024);
        var rawData = $"HTTP/1.1 200 OK\r\nX-Large-Header: {largeHeaderVal}\r\n\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rawData));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpWireProtocol.ReadHttpResponseHeadersAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GARBAGE_WITHOUT_SPACES\r\n\r\n")]
    [InlineData("GET\r\n\r\n")]
    [InlineData("\r\n\r\n")]
    public async Task ReadHttpRequestAsync_MalformedRequestLines_ReturnsNull(string rawInput)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rawInput));
        var result = await HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadHttpRequestAsync_TwoTokenRequestLine_DefaultsHttpVersion()
    {
        var raw = "GET /resource\r\nHost: example.com\r\n\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var result = await HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GET", result.Method);
        Assert.Equal("/resource", result.Path);
        Assert.Equal("HTTP/1.1", result.HttpVersion);
    }

    [Theory]
    [InlineData("HTTP/1.1 NOT_AN_INT OK\r\n\r\n")]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("HTTP/1.1\r\n\r\n")]
    public async Task ReadHttpResponseHeadersAsync_MalformedStatusLines_ReturnsNull(string rawInput)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rawInput));
        var (headers, remaining) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(stream, CancellationToken.None);

        Assert.Null(headers);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task ReadHttpRequestAsync_DuplicateHostHeaders_OverwritesWithoutException()
    {
        var raw = "GET /index HTTP/1.1\r\nHost: legitimate.com\r\nHost: malicious.com\r\n\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var result = await HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Headers.ContainsKey("Host"));
        Assert.Equal("malicious.com", result.Headers["Host"]);
    }

    [Fact]
    public async Task SendForwardedRequestAsync_DuplicateHostHeadersInRequest_EmitsTargetHostExclusively()
    {
        var request = new ParsedHttpRequest
        {
            Method = "POST",
            Path = "/v1/generate",
            HttpVersion = "HTTP/1.1",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Host"] = "spoofed.host.com",
                ["X-Custom"] = "val1"
            },
            Body = "payload"u8.ToArray()
        };

        using var mem = new MemoryStream();
        await HttpWireProtocol.SendForwardedRequestAsync(mem, request, "target.upstream.com", "/v1/generate", CancellationToken.None);

        var forwardedString = Encoding.UTF8.GetString(mem.ToArray());

        // Target host must be present
        Assert.Contains("Host: target.upstream.com\r\n", forwardedString);
        // Spoofed host from request headers must NOT be present
        Assert.DoesNotContain("Host: spoofed.host.com", forwardedString);
        // Must contain Host header exactly once
        var hostHeaderCount = forwardedString.Split("\r\n").Count(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, hostHeaderCount);
    }

    #endregion

    #region 3. Defensive Traffic Guards: Rate Limits & WebSockets

    [Fact]
    public void TrackBurst_ExactThresholdTransitionsAndWindowReset()
    {
        var options = new ProxyOptions
        {
            BurstThreshold = 5,
            BurstWindow = TimeSpan.FromMilliseconds(500)
        };

        var proxy = new ProxyServer(options);

        // Requests 1..5: not suppressed
        for (var i = 1; i <= 5; i++)
        {
            var (suppressed, justDetected) = proxy.TrackBurst("GET", "/test", 200);
            Assert.False(suppressed, $"Request {i} should not be suppressed");
            Assert.False(justDetected, $"Request {i} should not be justDetected");
        }

        // Request 6: burst detected
        var (suppressed6, justDetected6) = proxy.TrackBurst("GET", "/test", 200);
        Assert.True(suppressed6);
        Assert.True(justDetected6);

        // Request 7: suppressed, but justDetected is false
        var (suppressed7, justDetected7) = proxy.TrackBurst("GET", "/test", 200);
        Assert.True(suppressed7);
        Assert.False(justDetected7);

        // Different endpoint: tracked independently
        var (suppressedOther, _) = proxy.TrackBurst("POST", "/other", 200);
        Assert.False(suppressedOther);

        // Wait for burst window to expire
        Thread.Sleep(600);

        // Request after window expiry: window resets, count = 1
        var (suppressedFresh, justDetectedFresh) = proxy.TrackBurst("GET", "/test", 200);
        Assert.False(suppressedFresh);
        Assert.False(justDetectedFresh);
    }

    [Theory]
    [InlineData("Upgrade", "websocket", true)]
    [InlineData("keep-alive, Upgrade", "WebSocket", true)]
    [InlineData("upgrade", "WEBSOCKET", true)]
    [InlineData("keep-alive", "websocket", false)]
    [InlineData("Upgrade", "h2c", false)]
    [InlineData("keep-alive", null, false)]
    public void IsWebSocketUpgrade_DetectsCaseInsensitiveAndMultiValuedHeaders(string? conn, string? up, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (conn is not null) headers["Connection"] = conn;
        if (up is not null) headers["Upgrade"] = up;

        var actual = ProxyServer.IsWebSocketUpgrade(headers);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task LiveProxyServer_OversizedHeader_TerminatesConnectionGracefullyWithoutCrashing()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = client.GetStream();

        // Send 70KB request
        var headerVal = new string('X', 70 * 1024);
        var rawReq = $"GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Junk: {headerVal}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(rawReq));
        await stream.FlushAsync();

        // Server detects >64KB and terminates connection
        var buf = new byte[1024];
        int bytesRead = 0;
        try
        {
            bytesRead = await stream.ReadAsync(buf);
        }
        catch (IOException)
        {
            // Expected on Windows: connection reset by peer
        }
        Assert.Equal(0, bytesRead);

        // Server remains alive and functional
        Assert.True(proxy.IsRunning);
    }

    [Fact]
    public async Task LiveProxyServer_WebSocketUpgradeRejection_ClosesWith426()
    {
        var options = new ProxyOptions
        {
            Port = 0,
            RejectWebSocketUpgrades = true
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = client.GetStream();

        var wsReq = "GET /chat/ws HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(wsReq));
        await stream.FlushAsync();

        var buf = new byte[1024];
        var read = await stream.ReadAsync(buf);
        var response = Encoding.ASCII.GetString(buf, 0, read);

        Assert.StartsWith("HTTP/1.1 426 Upgrade Required", response);
        Assert.Contains("Connection: close", response);
    }

    #endregion
}