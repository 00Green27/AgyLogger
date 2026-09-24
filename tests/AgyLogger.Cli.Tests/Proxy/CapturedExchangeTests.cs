namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.Collections.Generic;
using System.Text;

using AgyLogger.Cli.Models.Proxy;

using Xunit;

public class CapturedExchangeTests
{
    [Fact]
    public void DefaultValues_AreInitializedSafely()
    {
        var exchange = new CapturedExchange();

        Assert.NotEqual(Guid.Empty, exchange.ExchangeId);
        Assert.NotNull(exchange.RawRequestBody);
        Assert.Empty(exchange.RawRequestBody);
        Assert.NotNull(exchange.RawResponseBody);
        Assert.Empty(exchange.RawResponseBody);
        Assert.NotNull(exchange.RawRequestHeaders);
        Assert.NotNull(exchange.RedactedRequestHeaders);
        Assert.NotNull(exchange.RawResponseHeaders);
        Assert.NotNull(exchange.RedactedResponseHeaders);
        Assert.Null(exchange.ResponseTimestamp);
        Assert.Null(exchange.Duration);
        Assert.False(exchange.HasErrors);
        Assert.Equal(WireFormat.Unknown, exchange.WireFormat);
        Assert.Equal("unknown", exchange.WireFormatTag);
        Assert.Equal("Antigravity CLI", exchange.AgentName);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("/", exchange.Url);
    }

    [Fact]
    public void TimestampsAndDuration_ComputeCorrectly()
    {
        var t0 = new DateTimeOffset(2026, 9, 20, 19, 5, 30, 123, TimeSpan.Zero);
        var t1 = t0.AddMilliseconds(150);

        var exchange = new CapturedExchange
        {
            RequestTimestamp = t0,
            ResponseTimestamp = t1
        };

        Assert.Equal(TimeSpan.FromMilliseconds(150), exchange.Duration);
        Assert.Equal("2026-09-20T19:05:30.123Z", exchange.FormattedTimestamp);
    }

    [Fact]
    public void Endpoint_CombinesMethodAndUrl()
    {
        var exchange = new CapturedExchange
        {
            Method = "POST",
            Url = "/v1beta/models"
        };

        Assert.Equal("POST /v1beta/models", exchange.Endpoint);

        var noMethodExchange = new CapturedExchange
        {
            Method = "",
            Url = "/v1beta/models"
        };

        Assert.Equal("/v1beta/models", noMethodExchange.Endpoint);
    }

    [Fact]
    public void BuildLogFileName_FormatsCleanTimestampAndAgent()
    {
        var t0 = new DateTimeOffset(2026, 9, 20, 19, 5, 30, 123, TimeSpan.Zero);
        var exchange = new CapturedExchange
        {
            RequestTimestamp = t0,
            AgentName = "Antigravity CLI"
        };

        var fileName = exchange.BuildLogFileName();
        Assert.Equal("2026-09-20T19-05-30-123_antigravity-cli.md", fileName);

        var overrideFileName = exchange.BuildLogFileName("Claude Code (Beta)");
        Assert.Equal("2026-09-20T19-05-30-123_claude-code-beta.md", overrideFileName);

        var emptyAgentFileName = exchange.BuildLogFileName("   ");
        Assert.Equal("2026-09-20T19-05-30-123_antigravity-cli.md", emptyAgentFileName);

        var fallbackExchange = new CapturedExchange
        {
            RequestTimestamp = t0,
            AgentName = ""
        };
        Assert.Equal("2026-09-20T19-05-30-123_agent.md", fallbackExchange.BuildLogFileName());
    }

    [Fact]
    public void GetEffectiveRequestBodyText_ReturnsDecodedOrFallback()
    {
        var exchangeWithDecoded = new CapturedExchange
        {
            DecodedRequestBody = "{\"prompt\":\"hello\"}",
            RawRequestBody = Encoding.UTF8.GetBytes("raw")
        };
        Assert.Equal("{\"prompt\":\"hello\"}", exchangeWithDecoded.GetEffectiveRequestBodyText());

        var exchangeWithRawOnly = new CapturedExchange
        {
            RawRequestBody = Encoding.UTF8.GetBytes("{\"prompt\":\"raw\"}")
        };
        Assert.Equal("{\"prompt\":\"raw\"}", exchangeWithRawOnly.GetEffectiveRequestBodyText());

        var exchangeWithWarning = new CapturedExchange
        {
            RawRequestBody = Encoding.UTF8.GetBytes("compressed-bytes"),
            RequestDecodingWarning = "[request-logger] COULD NOT DECODE THIS BODY \u2014 error"
        };
        var warningResult = exchangeWithWarning.GetEffectiveRequestBodyText();
        Assert.Contains("[request-logger] COULD NOT DECODE THIS BODY \u2014 error", warningResult);
        Assert.Contains("The bytes below are still compressed. They are NOT what was actually sent as text:", warningResult);
        Assert.Contains("compressed-bytes", warningResult);
    }

    [Fact]
    public void GetEffectiveResponseBodyText_ReturnsDecodedOrFallback()
    {
        var exchangeWithDecoded = new CapturedExchange
        {
            DecodedResponseBody = "{\"response\":\"world\"}",
            RawResponseBody = Encoding.UTF8.GetBytes("raw")
        };
        Assert.Equal("{\"response\":\"world\"}", exchangeWithDecoded.GetEffectiveResponseBodyText());

        var exchangeWithRawOnly = new CapturedExchange
        {
            RawResponseBody = Encoding.UTF8.GetBytes("{\"response\":\"raw\"}")
        };
        Assert.Equal("{\"response\":\"raw\"}", exchangeWithRawOnly.GetEffectiveResponseBodyText());

        var exchangeWithWarning = new CapturedExchange
        {
            RawResponseBody = Encoding.UTF8.GetBytes("compressed-resp"),
            ResponseDecodingWarning = "[request-logger] COULD NOT DECODE THIS BODY \u2014 error"
        };
        var warningResult = exchangeWithWarning.GetEffectiveResponseBodyText();
        Assert.Contains("[request-logger] COULD NOT DECODE THIS BODY \u2014 error", warningResult);
        Assert.Contains("The bytes below are still compressed. They are NOT what was actually sent as text:", warningResult);
        Assert.Contains("compressed-resp", warningResult);
    }

    [Fact]
    public void HasErrors_DetectsWarningsAndErrors()
    {
        var clean = new CapturedExchange();
        Assert.False(clean.HasErrors);

        var withError = new CapturedExchange { ErrorMessage = "Upstream socket disconnected" };
        Assert.True(withError.HasErrors);

        var withReqWarning = new CapturedExchange { RequestDecodingWarning = "decompression error" };
        Assert.True(withReqWarning.HasErrors);

        var withRespWarning = new CapturedExchange { ResponseDecodingWarning = "decompression error" };
        Assert.True(withRespWarning.HasErrors);
    }

    [Fact]
    public void WithExpression_PreservesImmutability()
    {
        var original = new CapturedExchange
        {
            StatusCode = 200,
            Method = "POST",
            Url = "/api/test",
            WireFormat = WireFormat.Gemini
        };

        var modified = original with { StatusCode = 404 };

        Assert.Equal(200, original.StatusCode);
        Assert.Equal(404, modified.StatusCode);
        Assert.Equal(original.ExchangeId, modified.ExchangeId);
        Assert.Equal(original.Method, modified.Method);
        Assert.Equal(original.Url, modified.Url);
        Assert.Equal(original.WireFormat, modified.WireFormat);
    }
}