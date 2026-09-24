namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// High-performance HTTP/HTTPS proxy server supporting both plain HTTP reverse proxying
/// and forward MitM HTTPS CONNECT tunneling with zero third-party dependencies.
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private readonly ProxyOptions _options;
    private readonly CertificateAuthority _ca;
    private readonly bool _ownsCa;
    private readonly ConcurrentDictionary<string, BurstState> _burstStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, bool> _activeConnections = new();
    private readonly ConcurrentDictionary<Socket, byte> _activeSockets = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private bool _isRunning;
    private int _boundPort;

    public ProxyOptions Options => _options;
    public CertificateAuthority CertificateAuthority => _ca;
    public bool IsRunning => _isRunning;
    public int BoundPort => _boundPort;

    public ProxyServer(ProxyOptions? options = null, CertificateAuthority? certificateAuthority = null)
    {
        _options = options ?? new ProxyOptions();
        _options.Validate();

        if (certificateAuthority is not null)
        {
            _ca = certificateAuthority;
            _ownsCa = false;
        }
        else
        {
            _ca = CertificateAuthority.GetOrCreateDefault(_options.CaCertPath);
            _ownsCa = true;
        }
    }

    /// <summary>
    /// Starts the proxy server and begins accepting client connections.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("ProxyServer is already running.");
        }

        if (_options.AutoTrustRootCertificate)
        {
            _ca.TrustInCurrentUserStore();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var ip = IPAddress.Parse(_options.Host);
        _listener = new TcpListener(ip, _options.Port);
        _listener.Start();

        _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _isRunning = true;

        _acceptLoopTask = Task.Run(AcceptConnectionsLoopAsync, _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gracefully stops the proxy server, closes listener, and awaits active connections.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch { }

        try
        {
            _listener?.Stop();
        }
        catch { }

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch { }
        }

        if (!_activeConnections.IsEmpty)
        {
            var timeoutTask = Task.Delay(2000);
            await Task.WhenAny(Task.WhenAll(_activeConnections.Keys), timeoutTask).ConfigureAwait(false);
        }

        // Close any lingering sockets that did not finish within timeout
        foreach (var socket in _activeSockets.Keys)
        {
            try
            {
                socket.Close(0); // Linger 0 sends TCP RST and terminates immediately
                socket.Dispose();
            }
            catch { }
        }
        _activeSockets.Clear();
        _activeConnections.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        if (_ownsCa)
        {
            _ca.Dispose();
        }
    }

    private async Task AcceptConnectionsLoopAsync()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                var clientSocket = client.Client;
                _activeSockets.TryAdd(clientSocket, 0);

                var connTask = Task.Run(() => ProcessConnectionAsync(client, token), token);
                _activeConnections.TryAdd(connTask, true);
                _ = connTask.ContinueWith(t => _activeConnections.TryRemove(t, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[AgyLogger] Accept error: {ex.Message}");
                }
            }
        }
    }

    private async Task ProcessConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var networkStream = client.GetStream())
        {
            try
            {
                // Read the initial request line and headers from client
                var parsedRequest = await HttpWireProtocol.ReadHttpRequestAsync(networkStream, cancellationToken).ConfigureAwait(false);
                if (parsedRequest is null)
                {
                    return;
                }

                if (parsedRequest.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleMitmConnectAsync(networkStream, parsedRequest, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await HandleReverseProxyAsync(networkStream, parsedRequest, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ioEx) when (StreamRelay.IsClientAbort(ioEx)) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AgyLogger] Connection error: {ex.Message}");
            }
            finally
            {
                try
                {
                    var clientSocket = client.Client;
                    if (clientSocket is not null)
                    {
                        _activeSockets.TryRemove(clientSocket, out _);
                        if (clientSocket.Connected)
                        {
                            clientSocket.Shutdown(SocketShutdown.Send);
                        }
                    }
                }
                catch { }
            }
        }
    }

    #region Reverse Proxy Pipeline

    private async Task HandleReverseProxyAsync(
        Stream clientStream,
        ParsedHttpRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Check WebSocket Upgrade Guard
        if (_options.RejectWebSocketUpgrades && IsWebSocketUpgrade(request.Headers))
        {
            await RejectUpgradeAsync(clientStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reqTimestamp = DateTimeOffset.UtcNow;

        // 2. Resolve Upstream Target URL
        Uri targetBaseUri;
        string requestPath;

        if (request.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(request.Path, UriKind.Absolute, out var reqUri))
            {
                targetBaseUri = reqUri;
                requestPath = reqUri.PathAndQuery;
            }
            else
            {
                targetBaseUri = new Uri(_options.ReverseTargetUrl);
                requestPath = request.Path;
            }
        }
        else
        {
            targetBaseUri = new Uri(_options.ReverseTargetUrl);
            requestPath = HttpWireProtocol.CombineUrlPaths(targetBaseUri.AbsolutePath, request.Path);
        }

        var targetHost = targetBaseUri.DnsSafeHost;
        var targetPort = targetBaseUri.Port > 0 ? targetBaseUri.Port : (targetBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var useTls = targetBaseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        // 3. Connect Upstream
        using var upstreamClient = new TcpClient();
        try
        {
            await upstreamClient.ConnectAsync(targetHost, targetPort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpWireProtocol.Send502BadGatewayAsync(clientStream, $"Failed to connect to upstream {targetHost}:{targetPort}: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        Stream upstreamTransportStream = upstreamClient.GetStream();
        if (useTls)
        {
            var sslStream = new SslStream(upstreamTransportStream, leaveInnerStreamOpen: false, (sender, cert, chain, errors) => ValidateServerCertificate(cert, chain, errors));
            var clientSslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            };
            try
            {
                await sslStream.AuthenticateAsClientAsync(clientSslOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
                await HttpWireProtocol.Send502BadGatewayAsync(clientStream, $"Failed upstream TLS handshake with {targetHost}:{targetPort}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return;
            }
            upstreamTransportStream = sslStream;
        }

        await using (upstreamTransportStream)
        {
            // 4. Send Forwarded Request to Upstream
            await HttpWireProtocol.SendForwardedRequestAsync(upstreamTransportStream, request, targetHost, requestPath, cancellationToken).ConfigureAwait(false);

            // 5. Read Upstream Response Headers
            var (responseMeta, remainingBytes) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(upstreamTransportStream, cancellationToken).ConfigureAwait(false);
            if (responseMeta is null)
            {
                await HttpWireProtocol.Send502BadGatewayAsync(clientStream, "Upstream returned empty response.", cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var prefixStream = remainingBytes.Length > 0
                ? new PrefixStream(remainingBytes, upstreamTransportStream, leaveInnerStreamOpen: true)
                : null;
            Stream responseBodyStream = (Stream?)prefixStream ?? upstreamTransportStream;

            var isChunked = responseMeta.Headers.TryGetValue("Transfer-Encoding", out var te) &&
                            te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            long? contentLength = responseMeta.Headers.TryGetValue("Content-Length", out var clStr) &&
                                  long.TryParse(clStr, out var cl) ? cl : null;

            // 6. Forward Response Status & Headers to Client
            await HttpWireProtocol.SendResponseHeadersToClientAsync(clientStream, responseMeta, isChunked, closeConnection: true, cancellationToken).ConfigureAwait(false);

            // 7. Relay & Capture Response Body
            var relayResult = await StreamRelay.RelayAsync(
                responseBodyStream,
                clientStream,
                isChunked,
                contentLength,
                StreamRelay.DefaultMaxCaptureBytes,
                StreamRelay.DefaultBufferSize,
                cancellationToken).ConfigureAwait(false);

            var respTimestamp = DateTimeOffset.UtcNow;

            // 8. Execute Logging Lifecycle
            ProcessCompletedExchange(
                request,
                targetHost,
                responseMeta,
                relayResult.CapturedBytes,
                reqTimestamp,
                respTimestamp);
        }
    }

    #endregion

    #region Forward MitM HTTPS CONNECT Pipeline

    private async Task HandleMitmConnectAsync(
        Stream clientStream,
        ParsedHttpRequest connectRequest,
        CancellationToken cancellationToken)
    {
        // 1. Parse target host and port from CONNECT authority
        var authority = connectRequest.Path.Trim();
        var colonIdx = authority.LastIndexOf(':');
        var targetHost = colonIdx > 0 ? authority[..colonIdx] : authority;
        var targetPort = colonIdx > 0 && int.TryParse(authority[(colonIdx + 1)..], out var p) ? p : 443;

        // Strip IPv6 brackets if present
        if (targetHost.StartsWith('[') && targetHost.EndsWith(']'))
        {
            targetHost = targetHost[1..^1];
        }

        // 2. Respond 200 Connection Established
        var established = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
        await clientStream.WriteAsync(established, cancellationToken).ConfigureAwait(false);
        await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 3. Upgrade client socket to SslStream using dynamic leaf certificate
        var leafCert = _ca.GetOrCreateLeafCertificate(targetHost);
        var clientSsl = new SslStream(clientStream, leaveInnerStreamOpen: false);

        var serverSslOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = leafCert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        };

        try
        {
            await clientSsl.AuthenticateAsServerAsync(serverSslOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AgyLogger] MitM TLS Handshake failed with client for {targetHost}: {ex.Message}");
            return;
        }

        await using (clientSsl)
        {
            // 4. Process requests inside the decrypted tunnel
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await HttpWireProtocol.ReadHttpRequestAsync(clientSsl, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    break; // EOF or client closed connection
                }

                if (_options.RejectWebSocketUpgrades && IsWebSocketUpgrade(request.Headers))
                {
                    await RejectUpgradeAsync(clientSsl, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var reqTimestamp = DateTimeOffset.UtcNow;

                // 5. Connect upstream to targetHost:targetPort via SslStream
                using var upstreamClient = new TcpClient();
                try
                {
                    await upstreamClient.ConnectAsync(targetHost, targetPort, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await HttpWireProtocol.Send502BadGatewayAsync(clientSsl, $"MitM failed to connect upstream to {targetHost}:{targetPort}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                    break;
                }

                var useTls = targetPort == 443 || targetPort == 8443 || !IPAddress.TryParse(targetHost, out _);
                Stream upstreamTransportStream = upstreamClient.GetStream();

                if (useTls)
                {
                    var upstreamSsl = new SslStream(upstreamTransportStream, leaveInnerStreamOpen: false, (sender, cert, chain, errors) => ValidateServerCertificate(cert, chain, errors));
                    var clientSslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = targetHost,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http11]
                    };
                    try
                    {
                        await upstreamSsl.AuthenticateAsClientAsync(clientSslOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await upstreamSsl.DisposeAsync().ConfigureAwait(false);
                        await HttpWireProtocol.Send502BadGatewayAsync(clientSsl, $"MitM failed upstream TLS handshake with {targetHost}:{targetPort}: {ex.Message}", cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    upstreamTransportStream = upstreamSsl;
                }

                await using (upstreamTransportStream)
                {
                    await HttpWireProtocol.SendForwardedRequestAsync(upstreamTransportStream, request, targetHost, request.Path, cancellationToken).ConfigureAwait(false);

                    var (responseMeta, remainingBytes) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(upstreamTransportStream, cancellationToken).ConfigureAwait(false);
                    if (responseMeta is null)
                    {
                        await HttpWireProtocol.Send502BadGatewayAsync(clientSsl, "MitM upstream returned empty response.", cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    await using var prefixStream = remainingBytes.Length > 0
                        ? new PrefixStream(remainingBytes, upstreamTransportStream, leaveInnerStreamOpen: true)
                        : null;
                    Stream responseBodyStream = (Stream?)prefixStream ?? upstreamTransportStream;

                    var isChunked = responseMeta.Headers.TryGetValue("Transfer-Encoding", out var te) &&
                                    te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
                    long? contentLength = responseMeta.Headers.TryGetValue("Content-Length", out var clStr) &&
                                          long.TryParse(clStr, out var cl) ? cl : null;

                    var closeConnection = HttpWireProtocol.ShouldCloseConnection(request.Headers, responseMeta.Headers);
                    await HttpWireProtocol.SendResponseHeadersToClientAsync(clientSsl, responseMeta, isChunked, closeConnection, cancellationToken).ConfigureAwait(false);

                    var relayResult = await StreamRelay.RelayAsync(
                        responseBodyStream,
                        clientSsl,
                        isChunked,
                        contentLength,
                        StreamRelay.DefaultMaxCaptureBytes,
                        StreamRelay.DefaultBufferSize,
                        cancellationToken).ConfigureAwait(false);

                    var respTimestamp = DateTimeOffset.UtcNow;

                    ProcessCompletedExchange(
                        request,
                        targetHost,
                        responseMeta,
                        relayResult.CapturedBytes,
                        reqTimestamp,
                        respTimestamp);

                    if (closeConnection)
                    {
                        break;
                    }
                }
            }
        }
    }

    private bool ValidateServerCertificate(X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // Allow certificates signed by our own local Root CA (useful in integration test environments)
        if (cert is not null)
        {
            try
            {
                using var customChain = new X509Chain();
                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                customChain.ChainPolicy.CustomTrustStore.Clear();
                customChain.ChainPolicy.CustomTrustStore.Add(_ca.RootCertificate);

                using var x509 = new X509Certificate2(cert);
                if (customChain.Build(x509))
                {
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    #endregion

    #region Defensive Traffic Guards

    public static bool IsWebSocketUpgrade(IReadOnlyDictionary<string, string> headers)
    {
        var hasUpgradeConn = headers.TryGetValue("Connection", out var conn) &&
                             conn.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
        var isWs = headers.TryGetValue("Upgrade", out var up) &&
                   up.Contains("websocket", StringComparison.OrdinalIgnoreCase);

        return hasUpgradeConn && isWs;
    }

    public static (int StatusCode, string Response) RejectUpgrade(string? connection, string? upgrade)
    {
        var isUpgrade = (connection?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) ?? false) &&
                        (upgrade?.Contains("websocket", StringComparison.OrdinalIgnoreCase) ?? false);

        if (isUpgrade)
        {
            return (426, "HTTP/1.1 426 Upgrade Required\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        }

        return (200, "OK");
    }

    public static async Task RejectUpgradeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var resp = "HTTP/1.1 426 Upgrade Required\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(resp, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool ShouldLogRequest(string method, string path, WireFormat format, bool filterHousekeeping)
    {
        if (!filterHousekeeping)
        {
            return true;
        }

        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (format == WireFormat.Gemini)
        {
            return path.Contains("generateContent", StringComparison.OrdinalIgnoreCase);
        }

        return !Regex.IsMatch(path, @"count[_-]?tokens", RegexOptions.IgnoreCase);
    }

    public static bool ShouldLogRequest(string method, string path, string renderer, bool filterHousekeeping = true)
    {
        var format = WireFormatExtensions.FromWireTag(renderer);
        return ShouldLogRequest(method, path, format, filterHousekeeping);
    }

    public static (bool Suppressed, bool JustDetected) TrackBurst(int requestCountInWindow, int threshold = 20)
    {
        if (requestCountInWindow <= threshold) return (false, false);
        if (requestCountInWindow == threshold + 1) return (true, true);
        return (true, false);
    }

    public (bool Suppressed, bool JustDetected) TrackBurst(string method, string path, int statusCode)
    {
        var key = $"{method.ToUpperInvariant()} {path} {statusCode}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var state = _burstStates.AddOrUpdate(
            key,
            k => new BurstState { Key = k, WindowStart = now, Count = 1, Warned = false },
            (k, existing) =>
            {
                var fresh = (now - existing.WindowStart) > _options.BurstWindow.TotalMilliseconds;
                var count = fresh ? 1 : existing.Count + 1;
                var start = fresh ? now : existing.WindowStart;
                var wasWarned = !fresh && existing.Warned;
                var suppressed = count > _options.BurstThreshold;

                return new BurstState
                {
                    Key = k,
                    WindowStart = start,
                    Count = count,
                    Warned = wasWarned || suppressed
                };
            });

        var isSuppressed = state.Count > _options.BurstThreshold;
        var justDetected = isSuppressed && (state.Count == _options.BurstThreshold + 1);

        if (justDetected)
        {
            Console.Error.WriteLine($"\n[AgyLogger] {method} {path} -> {statusCode} repeated {_options.BurstThreshold}+ times in {_options.BurstWindow.TotalSeconds:F1}s.");
            Console.Error.WriteLine("[AgyLogger] Runaway client retry burst detected. Suppressing disk writes for further repeats until burst stops.\n");
        }
        else if (isSuppressed && state.Count % 500 == 0)
        {
            Console.Error.WriteLine($"[AgyLogger] {method} {path} -> {statusCode} ({state.Count} repeats suppressed so far)");
        }

        return (isSuppressed, justDetected);
    }

    #endregion

    #region Logging Lifecycle

    private void ProcessCompletedExchange(
        ParsedHttpRequest request,
        string targetHost,
        ParsedHttpResponseHeaders response,
        byte[] rawResponseBody,
        DateTimeOffset reqTimestamp,
        DateTimeOffset respTimestamp)
    {
        var (decodedReq, reqWarn) = PayloadDecompressor.Decompress(
            request.Body,
            request.Headers.GetValueOrDefault("Content-Encoding"));

        var (decodedResp, respWarn) = PayloadDecompressor.Decompress(
            rawResponseBody,
            response.Headers.GetValueOrDefault("Content-Encoding"));

        var decodedReqText = Encoding.UTF8.GetString(decodedReq);
        var decodedRespText = Encoding.UTF8.GetString(decodedResp);

        var detectedFormat = WireFormatDetector.DetectFormat(request.Path, decodedReqText, request.Headers, request.Method);
        if (detectedFormat == WireFormat.Unknown)
        {
            detectedFormat = _options.DefaultWireFormat;
        }

        // Defensive Guard: Request filtering
        if (!ShouldLogRequest(request.Method, request.Path, detectedFormat, _options.FilterHousekeepingRequests))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.Filter) &&
            !request.Path.Contains(_options.Filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Defensive Guard: Burst tracking
        var (suppressed, _) = TrackBurst(request.Method, request.Path, response.StatusCode);
        if (suppressed)
        {
            return;
        }

        // Resolve model name
        var model = ExtractModelName(detectedFormat, request.Path, decodedReqText);

        var isStreaming = response.Headers.TryGetValue("Content-Type", out var ct) &&
                          ct.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        var exchange = new CapturedExchange
        {
            RequestTimestamp = reqTimestamp,
            ResponseTimestamp = respTimestamp,
            Method = request.Method,
            Url = request.Path,
            TargetHost = targetHost,
            StatusCode = response.StatusCode,
            StatusDescription = response.StatusDescription,
            IsStreaming = isStreaming,
            RawRequestHeaders = request.Headers,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(request.Headers),
            RawResponseHeaders = response.Headers,
            RedactedResponseHeaders = SensitiveDataRedactor.RedactHeaders(response.Headers),
            RawRequestBody = request.Body,
            RequestContentEncoding = request.Headers.GetValueOrDefault("Content-Encoding"),
            DecodedRequestBody = decodedReqText,
            RequestDecodingWarning = reqWarn,
            RawResponseBody = rawResponseBody,
            ResponseContentEncoding = response.Headers.GetValueOrDefault("Content-Encoding"),
            DecodedResponseBody = decodedRespText,
            ResponseDecodingWarning = respWarn,
            WireFormat = detectedFormat,
            AgentName = _options.DefaultAgentName,
            ModelName = model
        };

        // Asynchronous non-blocking file write
        _ = Task.Run(async () =>
        {
            try
            {
                await RequestMarkdownRenderer.WriteToFileAsync(
                    exchange,
                    _options.LogsDirectory,
                    _options.SaveRawCompanionFiles).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AgyLogger] Failed to write markdown log: {ex.Message}");
            }
        });
    }

    private static string ExtractModelName(WireFormat format, string path, string reqBodyText)
    {
        if (format == WireFormat.Gemini)
        {
            try
            {
                using var doc = JsonDocument.Parse(reqBodyText);
                var m = GeminiPayloadParser.FindModel(doc.RootElement, path);
                if (!string.IsNullOrEmpty(m)) return m;
            }
            catch { }
        }

        var match = Regex.Match(path, @"models\/([^:/?]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(reqBodyText))
        {
            try
            {
                using var doc = JsonDocument.Parse(reqBodyText);
                if (doc.RootElement.TryGetProperty("model", out var mElem) && mElem.ValueKind == JsonValueKind.String)
                {
                    return mElem.GetString() ?? "unknown";
                }
            }
            catch { }
        }

        return "unknown";
    }

    #endregion
}