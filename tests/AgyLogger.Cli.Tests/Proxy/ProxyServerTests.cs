namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;
using AgyLogger.Cli.Tests.E2E.Harness;

using Xunit;

public class ProxyServerTests
{
    [Fact]
    public async Task ProxyServer_StartAndStop_BindsPortAndDisposesCleanly()
    {
        var options = new ProxyOptions
        {
            Port = 0,
            Host = "127.0.0.1"
        };

        await using var proxy = new ProxyServer(options);
        Assert.False(proxy.IsRunning);
        Assert.Equal(0, proxy.BoundPort);

        await proxy.StartAsync();
        Assert.True(proxy.IsRunning);
        Assert.True(proxy.BoundPort > 0);

        await proxy.StopAsync();
        Assert.False(proxy.IsRunning);
    }

    [Fact]
    public async Task ProxyServer_StartWhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);

        await proxy.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StartAsync());
    }

    [Fact]
    public async Task ProxyServer_ReverseProxy_ForwardsPostAndStreamsResponse()
    {
        await using var upstream = await TestHttpServer.StartAsync((method, path, headers, body) =>
        {
            var echo = Encoding.UTF8.GetString(body);
            return (200, new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                Encoding.UTF8.GetBytes($"{{\"echo\": \"{echo}\"}}"));
        });

        var options = new ProxyOptions
        {
            Port = 0,
            ReverseTargetUrl = upstream.BaseUrl
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var payload = "test payload";
        var response = await client.PostAsync(
            $"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini-2.5-pro:generateContent",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bodyText = await response.Content.ReadAsStringAsync();
        Assert.Contains("test payload", bodyText);

        Assert.Single(upstream.ReceivedRequests);
        var received = upstream.ReceivedRequests[0];
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1beta/models/gemini-2.5-pro:generateContent", received.Path);
        Assert.Equal(payload, Encoding.UTF8.GetString(received.Body));
    }

    [Fact]
    public async Task ProxyServer_ReverseProxy_ImmediateChunkFlushing()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
        {
            var content = "data: chunk 1\n\ndata: chunk 2\n\n";
            return (200, new Dictionary<string, string> { ["Content-Type"] = "text/event-stream" },
                Encoding.UTF8.GetBytes(content));
        });

        var options = new ProxyOptions
        {
            Port = 0,
            ReverseTargetUrl = upstream.BaseUrl
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini:streamGenerateContent?alt=sse");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: chunk 1", content);
        Assert.Contains("data: chunk 2", content);
    }

    [Fact]
    public async Task ProxyServer_ReverseProxy_PreservesUpstreamErrorStatusCodes()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
            (400, new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                "{\"error\": \"Bad Request from upstream\"}"u8.ToArray()));

        var options = new ProxyOptions
        {
            Port = 0,
            ReverseTargetUrl = upstream.BaseUrl
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Bad Request from upstream", body);
    }

    [Fact]
    public async Task ProxyServer_ReverseProxy_GeneratesMarkdownLogOnCompletion()
    {
        var tempLogs = Path.Combine(Path.GetTempPath(), "AgyLogger_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        try
        {
            await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
                (200, new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    "{\"candidates\": [{\"content\": {\"parts\": [{\"text\": \"hello response\"}]}}]}"u8.ToArray()));

            var options = new ProxyOptions
            {
                Port = 0,
                ReverseTargetUrl = upstream.BaseUrl,
                LogsDirectory = tempLogs,
                FilterHousekeepingRequests = false
            };

            await using var proxy = new ProxyServer(options);
            await proxy.StartAsync();

            using var client = new HttpClient();
            var res = await client.PostAsync(
                $"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini-2.5-pro:generateContent",
                new StringContent("{\"prompt\": \"hi\"}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            // Give async background file write task time to finish
            var logContent = await WaitForFileContentAsync(tempLogs);
            Assert.Contains("<meta>", logContent);
            Assert.Contains("<headers>", logContent);
            Assert.Contains("<request>", logContent);
            Assert.Contains("<response>", logContent);
            Assert.Contains("gemini-2.5-pro", logContent);
        }
        finally
        {
            if (Directory.Exists(tempLogs))
            {
                try { Directory.Delete(tempLogs, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProxyServer_ReverseProxy_RedactsSensitiveHeadersInLog()
    {
        var tempLogs = Path.Combine(Path.GetTempPath(), "AgyLogger_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        try
        {
            await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
                (200, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "{}"u8.ToArray()));

            var options = new ProxyOptions
            {
                Port = 0,
                ReverseTargetUrl = upstream.BaseUrl,
                LogsDirectory = tempLogs,
                FilterHousekeepingRequests = false
            };

            await using var proxy = new ProxyServer(options);
            await proxy.StartAsync();

            using var client = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini:generateContent")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", "Bearer super-secret-token-12345");
            req.Headers.Add("x-goog-api-key", "secret-goog-api-key-xyz");

            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var logContent = await WaitForFileContentAsync(tempLogs);
            Assert.Contains("[REDACTED]", logContent);
            Assert.DoesNotContain("super-secret-token-12345", logContent);
            Assert.DoesNotContain("secret-goog-api-key-xyz", logContent);
        }
        finally
        {
            if (Directory.Exists(tempLogs))
            {
                try { Directory.Delete(tempLogs, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProxyServer_ForwardMitm_ConnectHandshakeReturns200()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.BoundPort);

        var stream = client.GetStream();
        var connectRequest = "CONNECT target.example.com:443 HTTP/1.1\r\nHost: target.example.com:443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectRequest);
        await stream.FlushAsync();

        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.StartsWith("HTTP/1.1 200 Connection Established", response);
    }

    [Fact]
    public async Task ProxyServer_ForwardMitm_PerformsTlsHandshakeWithLeafCert()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.BoundPort);

        var stream = client.GetStream();
        var connectRequest = "CONNECT secure.target.com:443 HTTP/1.1\r\nHost: secure.target.com:443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectRequest);
        await stream.FlushAsync();

        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer);
        var connectResp = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("HTTP/1.1 200 Connection Established", connectResp);

        // Perform TLS Handshake against the established tunnel
        X509Certificate? receivedCert = null;
        using var clientSsl = new SslStream(stream, false, (sender, cert, chain, errors) =>
        {
            receivedCert = cert;
            return true; // Accept for test assertion
        });

        await clientSsl.AuthenticateAsClientAsync("secure.target.com");
        Assert.NotNull(receivedCert);
        Assert.Contains("secure.target.com", receivedCert.Subject);

        // Verify leaf certificate is signed by proxy's Root CA
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(proxy.CertificateAuthority.RootCertificate);

        using var x509 = new X509Certificate2(receivedCert);
        var isValid = chain.Build(x509);
        Assert.True(isValid);
    }

    [Fact]
    public async Task ProxyServer_ForwardMitm_DecryptedTurnForwardedAndLogged()
    {
        // 1. Upstream HTTP server
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, body) =>
            (200, new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                "{\"result\": \"mitm-success\"}"u8.ToArray()));

        var tempLogs = Path.Combine(Path.GetTempPath(), "AgyLogger_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        try
        {
            var options = new ProxyOptions
            {
                Port = 0,
                LogsDirectory = tempLogs,
                FilterHousekeepingRequests = false
            };

            await using var proxy = new ProxyServer(options);
            await proxy.StartAsync();

            // 2. Client initiates CONNECT to upstream.Port
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", proxy.BoundPort);
            var stream = client.GetStream();

            var connectReq = $"CONNECT 127.0.0.1:{upstream.Port} HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(connectReq));
            await stream.FlushAsync();

            var buf = new byte[1024];
            var r = await stream.ReadAsync(buf);
            var connectResp = Encoding.ASCII.GetString(buf, 0, r);
            Assert.StartsWith("HTTP/1.1 200 Connection Established", connectResp);

            // 3. Client wraps in TLS
            using var clientSsl = new SslStream(stream, false, (_, _, _, _) => true);
            await clientSsl.AuthenticateAsClientAsync("127.0.0.1");

            // 4. Send HTTP request inside TLS tunnel
            var reqPayload = "{\"prompt\": \"mitm test\"}";
            var httpReq = $"POST /v1beta/models/gemini:generateContent HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\nContent-Type: application/json\r\nContent-Length: {reqPayload.Length}\r\nConnection: close\r\n\r\n{reqPayload}";
            await clientSsl.WriteAsync(Encoding.UTF8.GetBytes(httpReq));
            await clientSsl.FlushAsync();

            var respBuilder = new StringBuilder();
            var respBuf = new byte[4096];
            while (true)
            {
                var readCount = await clientSsl.ReadAsync(respBuf);
                if (readCount == 0) break;
                respBuilder.Append(Encoding.UTF8.GetString(respBuf, 0, readCount));
                if (respBuilder.ToString().Contains("mitm-success")) break;
            }
            var respString = respBuilder.ToString();
            Assert.Contains("200 OK", respString);
            Assert.Contains("mitm-success", respString);

            // 5. Verify log was generated
            var logContent = await WaitForFileContentAsync(tempLogs);
            Assert.Contains("mitm-success", logContent);
        }
        finally
        {
            if (Directory.Exists(tempLogs))
            {
                try { Directory.Delete(tempLogs, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProxyServer_DefensiveGuard_RejectsWebSocketUpgradeWith426()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
            (200, new(), "should not be reached"u8.ToArray()));

        var options = new ProxyOptions
        {
            Port = 0,
            ReverseTargetUrl = upstream.BaseUrl,
            RejectWebSocketUpgrades = true
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{proxy.BoundPort}/ws");
        req.Headers.Add("Connection", "Upgrade");
        req.Headers.Add("Upgrade", "websocket");

        var response = await client.SendAsync(req);

        Assert.Equal((HttpStatusCode)426, response.StatusCode);
        Assert.Empty(upstream.ReceivedRequests); // Upstream was NOT contacted

        // Also test static guards
        var (code, msg) = ProxyServer.RejectUpgrade("Upgrade", "websocket");
        Assert.Equal(426, code);
        Assert.Contains("426 Upgrade Required", msg);

        var (code2, _) = ProxyServer.RejectUpgrade("keep-alive", null);
        Assert.Equal(200, code2);
    }

    [Fact]
    public void ProxyServer_DefensiveGuard_TrackBurstSuppressesDiskWrites()
    {
        var options = new ProxyOptions
        {
            BurstThreshold = 20,
            BurstWindow = TimeSpan.FromMilliseconds(2000)
        };

        var proxy = new ProxyServer(options);

        // First 20 requests within window: not suppressed
        for (var i = 1; i <= 20; i++)
        {
            var (suppressed, justDetected) = proxy.TrackBurst("POST", "/v1beta/models/gemini:generateContent", 200);
            Assert.False(suppressed);
            Assert.False(justDetected);
        }

        // 21st request: suppressed and justDetected is true
        var (suppressed21, justDetected21) = proxy.TrackBurst("POST", "/v1beta/models/gemini:generateContent", 200);
        Assert.True(suppressed21);
        Assert.True(justDetected21);

        // 22nd request: suppressed, but justDetected is false (warning already emitted)
        var (suppressed22, justDetected22) = proxy.TrackBurst("POST", "/v1beta/models/gemini:generateContent", 200);
        Assert.True(suppressed22);
        Assert.False(justDetected22);

        // Static helper test
        Assert.Equal((false, false), ProxyServer.TrackBurst(20, 20));
        Assert.Equal((true, true), ProxyServer.TrackBurst(21, 20));
        Assert.Equal((true, false), ProxyServer.TrackBurst(22, 20));
    }

    [Fact]
    public void ProxyServer_DefensiveGuard_FiltersHousekeepingRequests()
    {
        // When filterHousekeeping is false, all requests pass
        Assert.True(ProxyServer.ShouldLogRequest("GET", "/health", WireFormat.Gemini, filterHousekeeping: false));

        // When filterHousekeeping is true:
        // Only POST requests are logged
        Assert.False(ProxyServer.ShouldLogRequest("GET", "/v1beta/models/gemini:generateContent", WireFormat.Gemini, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("OPTIONS", "/v1beta/models/gemini:generateContent", WireFormat.Gemini, filterHousekeeping: true));

        // Gemini: only generateContent is logged; housekeeping like :countTokens is filtered
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini-2.5-pro:generateContent", WireFormat.Gemini, filterHousekeeping: true));
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse", WireFormat.Gemini, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini-2.5-pro:countTokens", WireFormat.Gemini, filterHousekeeping: true));

        // Anthropic: /v1/messages is logged; count_tokens is filtered
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1/messages", WireFormat.Anthropic, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1/messages/count_tokens", WireFormat.Anthropic, filterHousekeeping: true));

        // String renderer overload test
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:generateContent", "gemini"));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:countTokens", "gemini"));
    }

    [Fact]
    public async Task ProxyServer_Resilience_UpstreamUnreachableReturns502()
    {
        // Bind an ephemeral port and immediately close it to get an unreachable port
        var tempListener = new TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        var closedPort = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();

        var options = new ProxyOptions
        {
            Port = 0,
            ReverseTargetUrl = $"http://127.0.0.1:{closedPort}"
        };

        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/test");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Bad Gateway", body);
    }

    [Fact]
    public async Task ProxyServer_Resilience_ClientDisconnectMidStreamPreservesCapturedBytes()
    {
        var tempLogs = Path.Combine(Path.GetTempPath(), "AgyLogger_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        try
        {
            var largePayload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("stream-chunk-data ", 5000)));
            await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
                (200, new Dictionary<string, string> { ["Content-Type"] = "text/plain" }, largePayload));

            var options = new ProxyOptions
            {
                Port = 0,
                ReverseTargetUrl = upstream.BaseUrl,
                LogsDirectory = tempLogs,
                FilterHousekeepingRequests = false
            };

            await using var proxy = new ProxyServer(options);
            await proxy.StartAsync();

            // Connect raw client, send request, read only initial 50 bytes, then abruptly close socket
            using (var client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", proxy.BoundPort);
                var stream = client.GetStream();
                var req = "POST /v1beta/models/gemini:generateContent HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(req));
                await stream.FlushAsync();

                var smallBuf = new byte[50];
                _ = await stream.ReadAsync(smallBuf);
                // Abrupt close (simulating Ctrl+C)
                client.Close();
            }

            // Server must remain running without crashing
            Assert.True(proxy.IsRunning);

            // Give background task time to log partial exchange
            string[] files = [];
            var logContent = await WaitForFileContentAsync(tempLogs);
            Assert.Contains("stream-chunk-data", logContent);
        }
        finally
        {
            if (Directory.Exists(tempLogs))
            {
                try { Directory.Delete(tempLogs, recursive: true); } catch { }
            }
        }
    }

    private static async Task<string> WaitForFileContentAsync(string directory, int maxAttempts = 30, int delayMs = 100)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.GetFiles(directory, "*.md");
                if (files.Length > 0)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(files[0]);
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            return content;
                        }
                    }
                    catch (IOException)
                    {
                        // Retry on transient file lock on Windows
                    }
                }
            }
            await Task.Delay(delayMs);
        }

        var remaining = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.md") : [];
        if (remaining.Length > 0)
        {
            return await File.ReadAllTextAsync(remaining[0]);
        }
        throw new FileNotFoundException($"No .md files found in {directory} after waiting.");
    }
}