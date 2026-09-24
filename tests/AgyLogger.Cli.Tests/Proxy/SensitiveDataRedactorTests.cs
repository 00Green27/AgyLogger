namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.Collections.Generic;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

public class SensitiveDataRedactorTests
{
    // ===========================================================================
    // Category 1: Header Redaction — Known Sensitive Headers (Tests 1–7)
    // ===========================================================================

    [Fact]
    public void RedactHeaders_Authorization_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("Authorization", new[] { "Bearer my-secret-jwt-token" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["Authorization"]);
    }

    [Fact]
    public void RedactHeaders_XApiKey_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("x-api-key", new[] { "ant-api03-secret" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["x-api-key"]);
    }

    [Fact]
    public void RedactHeaders_ApiKey_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("api-key", new[] { "sk-proj-12345" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["api-key"]);
    }

    [Fact]
    public void RedactHeaders_XGoogApiKey_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("x-goog-api-key", new[] { "AIzaSyD-SecretGoogleKey" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["x-goog-api-key"]);
    }

    [Fact]
    public void RedactHeaders_Cookie_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("cookie", new[] { "SID=secret; HSID=token123" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["cookie"]);
    }

    [Fact]
    public void RedactHeaders_SetCookie_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("set-cookie", new[] { "session_id=xyz; Path=/; Secure; HttpOnly" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["set-cookie"]);
    }

    [Fact]
    public void RedactHeaders_ProxyAuthorization_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("proxy-authorization", new[] { "Basic dXNlcjpwYXNz" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["proxy-authorization"]);
    }

    // ===========================================================================
    // Category 2: Header Redaction — Case Insensitivity (Tests 8–10)
    // ===========================================================================

    [Fact]
    public void RedactHeaders_AllUppercaseHeaderName_ReplacedWithRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("AUTHORIZATION", new[] { "Bearer secret" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["AUTHORIZATION"]);
    }

    [Fact]
    public void RedactHeaders_PascalCaseHeaderName_ReplacedWithRedacted()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("X-Goog-Api-Key", new[] { "secret" }),
            new KeyValuePair<string, IEnumerable<string>>("Cookie", new[] { "val" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["X-Goog-Api-Key"]);
        Assert.Equal("[REDACTED]", result["Cookie"]);
    }

    [Fact]
    public void RedactHeaders_CaseInsensitiveLookupOnResultDictionary_Succeeds()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("X-API-KEY", new[] { "secret" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["x-api-key"]);
        Assert.Equal("[REDACTED]", result["X-API-KEY"]);
    }

    // ===========================================================================
    // Category 3: Header Redaction — Non-Sensitive Preservation (Tests 11–13)
    // ===========================================================================

    [Fact]
    public void RedactHeaders_StandardHeaders_PreservedVerbatim()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Content-Type", new[] { "application/json" }),
            new KeyValuePair<string, IEnumerable<string>>("User-Agent", new[] { "agy/1.0" }),
            new KeyValuePair<string, IEnumerable<string>>("Host", new[] { "generativelanguage.googleapis.com" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("application/json", result["Content-Type"]);
        Assert.Equal("agy/1.0", result["User-Agent"]);
        Assert.Equal("generativelanguage.googleapis.com", result["Host"]);
    }

    [Fact]
    public void RedactHeaders_OriginalKeyCasing_IsPreservedInDictionary()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Content-Type", new[] { "application/json" }),
            new KeyValuePair<string, IEnumerable<string>>("Accept-Encoding", new[] { "gzip, deflate" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.True(result.ContainsKey("Content-Type"));
        Assert.True(result.ContainsKey("Accept-Encoding"));
    }

    [Fact]
    public void RedactHeaders_CustomHeader_NotRedacted()
    {
        var headers = new[] { new KeyValuePair<string, IEnumerable<string>>("x-custom-request-id", new[] { "req_123" }) };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("req_123", result["x-custom-request-id"]);
    }

    // ===========================================================================
    // Category 4: Header Redaction — Multi-Value & Duplicate Merging (Tests 14–17)
    // ===========================================================================

    [Fact]
    public void RedactHeaders_NonSensitiveMultiValues_JoinedWithComma()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Accept", new[] { "text/html", "application/xhtml+xml", "application/xml" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("text/html, application/xhtml+xml, application/xml", result["Accept"]);
    }

    [Fact]
    public void RedactHeaders_SensitiveMultiValues_SingleRedactedToken()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("Set-Cookie", new[] { "auth=1", "session=2" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["Set-Cookie"]);
    }

    [Fact]
    public void RedactHeaders_DuplicateNonSensitiveKeysDifferingInCasing_MergedWithComma()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("accept", new[] { "text/plain" }),
            new KeyValuePair<string, IEnumerable<string>>("Accept", new[] { "application/json" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("text/plain, application/json", result["accept"]);
    }

    [Fact]
    public void RedactHeaders_DuplicateSensitiveKeysDifferingInCasing_RemainsSingleRedacted()
    {
        var headers = new[]
        {
            new KeyValuePair<string, IEnumerable<string>>("cookie", new[] { "a=1" }),
            new KeyValuePair<string, IEnumerable<string>>("Cookie", new[] { "b=2" })
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["cookie"]);
    }

    // ===========================================================================
    // Category 5: Header Redaction — Defensive Null & Empty Handling (Tests 18–20)
    // ===========================================================================

    [Fact]
    public void RedactHeaders_NullHeadersCollection_ReturnsEmptyDictionary()
    {
        var result = SensitiveDataRedactor.RedactHeaders((IEnumerable<KeyValuePair<string, IEnumerable<string>>>?)null);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void RedactHeaders_EmptyCollection_ReturnsEmptyDictionary()
    {
        var result = SensitiveDataRedactor.RedactHeaders(Array.Empty<KeyValuePair<string, IEnumerable<string>>>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void RedactHeaders_SingleStringOverload_FunctionsIdentically()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("Authorization", "Bearer secret"),
            new KeyValuePair<string, string>("Host", "api.gemini.com")
        };
        var result = SensitiveDataRedactor.RedactHeaders(headers);

        Assert.Equal("[REDACTED]", result["Authorization"]);
        Assert.Equal("api.gemini.com", result["Host"]);
    }

    // ===========================================================================
    // Category 6: URL Redaction — Known Sensitive Query Parameters (Tests 21–25)
    // ===========================================================================

    [Fact]
    public void RedactUrl_GeminiKeyParam_IsRedacted()
    {
        const string input = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?key=AIzaSyD-Secret123&alt=sse";
        const string expected = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?key=[REDACTED]&alt=sse";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_ApiKeyParam_IsRedacted()
    {
        const string input = "https://api.openai.com/v1/models?api_key=sk-1234567890";
        const string expected = "https://api.openai.com/v1/models?api_key=[REDACTED]";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_TokenParam_IsRedacted()
    {
        const string input = "/oauth/token?token=secret-token-xyz&client_id=123";
        const string expected = "/oauth/token?token=[REDACTED]&client_id=123";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_AuthParam_IsRedacted()
    {
        const string input = "https://firebase.googleapis.com/v1?auth=my-firebase-auth-token";
        const string expected = "https://firebase.googleapis.com/v1?auth=[REDACTED]";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_MultipleSensitiveParams_AllRedacted()
    {
        const string input = "/api?key=sec1&token=sec2&sig=sec3&other=safe";
        const string expected = "/api?key=[REDACTED]&token=[REDACTED]&sig=[REDACTED]&other=safe";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    // ===========================================================================
    // Category 7: URL Redaction — Case Insensitivity & Encoding (Tests 26–27)
    // ===========================================================================

    [Fact]
    public void RedactUrl_MixedCaseQueryParamName_IsRedactedPreservingKeyCasing()
    {
        const string input = "/endpoint?Key=AIzaSySecret&API_KEY=sk-secret";
        const string expected = "/endpoint?Key=[REDACTED]&API_KEY=[REDACTED]";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_UrlEncodedParamName_IsRedacted()
    {
        const string input = "/endpoint?api%5Fkey=secret-key";
        const string expected = "/endpoint?api%5Fkey=[REDACTED]";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    // ===========================================================================
    // Category 8: URL Redaction — Structure Preservation & Edge Cases (Tests 28–31)
    // ===========================================================================

    [Fact]
    public void RedactUrl_NoQueryString_ReturnsUnchanged()
    {
        const string input = "https://api.anthropic.com/v1/messages";
        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactUrl_EmptyQueryString_ReturnsUnchanged()
    {
        const string input = "https://example.com/api?";
        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void RedactUrl_ColonsInPath_PreservedIntact()
    {
        const string input = "/v1beta/models/gemini-2.5-pro:generateContent?key=secret";
        const string expected = "/v1beta/models/gemini-2.5-pro:generateContent?key=[REDACTED]";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RedactUrl_FragmentPreservedAfterQuery()
    {
        const string input = "/search?key=secret&q=gemini#top";
        const string expected = "/search?key=[REDACTED]&q=gemini#top";

        var result = SensitiveDataRedactor.RedactUrl(input);

        Assert.Equal(expected, result);
    }

    // ===========================================================================
    // Category 9: Markdown Rendering — <headers> Block Output (Test 32)
    // ===========================================================================

    [Fact]
    public void RenderHeaders_ProducesValidXmlDemarcatedBlock()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("Authorization", "Bearer secret"),
            new KeyValuePair<string, string>("Content-Type", "application/json")
        };
        var redacted = SensitiveDataRedactor.RedactHeaders(headers);
        var rendered = SensitiveDataRedactor.RenderHeaders(redacted);

        Assert.StartsWith("<headers>", rendered);
        Assert.EndsWith("</headers>", rendered);
        Assert.Contains("authorization: [REDACTED]", rendered);
        Assert.Contains("content-type: application/json", rendered);
        Assert.DoesNotContain("Bearer secret", rendered);
    }
}