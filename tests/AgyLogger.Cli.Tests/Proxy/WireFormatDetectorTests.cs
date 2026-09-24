namespace AgyLogger.Cli.Tests.Proxy;

using System.Reflection;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class WireFormatDetectorTests
{
    [Fact]
    public void Detect_GeminiDirectApiKeyPath_ReturnsGemini()
    {
        var format = WireFormatDetector.DetectFormat("/v1beta/models/gemini-2.5-pro:streamGenerateContent", null);
        Assert.Equal(WireFormat.Gemini, format);

        var tag = WireFormatDetector.Detect("/v1beta/models/gemini-2.5-pro:streamGenerateContent", null);
        Assert.Equal("gemini", tag);
    }

    [Fact]
    public void Detect_GeminiCodeAssistOAuthPath_ReturnsGemini()
    {
        var format = WireFormatDetector.DetectFormat("/v1internal:streamGenerateContent", null);
        Assert.Equal(WireFormat.Gemini, format);

        var tag = WireFormatDetector.Detect("/v1internal:streamGenerateContent", null);
        Assert.Equal("gemini", tag);
    }

    [Fact]
    public void Detect_GeminiBodyContents_ReturnsGemini()
    {
        var body = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}]}";
        var format = WireFormatDetector.DetectFormat("/custom/endpoint", body);
        Assert.Equal(WireFormat.Gemini, format);

        var tag = WireFormatDetector.Detect("/custom/endpoint", body);
        Assert.Equal("gemini", tag);
    }

    [Fact]
    public void Detect_GeminiWrappedOAuthBody_ReturnsGemini()
    {
        var body = "{\"model\":\"gemini-2.5-pro\",\"request\":{\"contents\":[]}}";
        var format = WireFormatDetector.DetectFormat("/unknown", body);
        Assert.Equal(WireFormat.Gemini, format);

        var tag = WireFormatDetector.Detect("/unknown", body);
        Assert.Equal("gemini", tag);
    }

    [Fact]
    public void Detect_AnthropicPath_ReturnsAnthropic()
    {
        var format = WireFormatDetector.DetectFormat("/v1/messages", null);
        Assert.Equal(WireFormat.Anthropic, format);

        var tag = WireFormatDetector.Detect("/v1/messages", null);
        Assert.Equal("anthropic", tag);
    }

    [Fact]
    public void Detect_AnthropicBody_ReturnsAnthropic()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":1000}";
        var format = WireFormatDetector.DetectFormat("/api/generate", body);
        Assert.Equal(WireFormat.Anthropic, format);

        var tag = WireFormatDetector.Detect("/api/generate", body);
        Assert.Equal("anthropic", tag);
    }

    [Fact]
    public void Detect_OpenAiChatPath_ReturnsOpenAi()
    {
        var format = WireFormatDetector.DetectFormat("/v1/chat/completions", null);
        Assert.Equal(WireFormat.OpenAi, format);

        var tag = WireFormatDetector.Detect("/v1/chat/completions", null);
        Assert.Equal("openai", tag);
    }

    [Fact]
    public void Detect_OpenAiResponsesPath_ReturnsOpenAi()
    {
        var format = WireFormatDetector.DetectFormat("/v1/responses", null);
        Assert.Equal(WireFormat.OpenAi, format);

        var tag = WireFormatDetector.Detect("/v1/responses", null);
        Assert.Equal("openai", tag);
    }

    [Fact]
    public void Detect_OpenAiResponsesBody_ReturnsOpenAi()
    {
        var body = "{\"instructions\":\"assistant\",\"input\":[{\"type\":\"message\"}]}";
        var format = WireFormatDetector.DetectFormat("/custom/route", body);
        Assert.Equal(WireFormat.OpenAi, format);

        var tag = WireFormatDetector.Detect("/custom/route", body);
        Assert.Equal("openai", tag);
    }

    [Fact]
    public void Detect_UnknownEndpointAndPayload_ReturnsRaw()
    {
        var format = WireFormatDetector.DetectFormat("/healthz", "{\"status\":\"ok\"}");
        Assert.Equal(WireFormat.Raw, format);

        var tag = WireFormatDetector.Detect("/healthz", "{\"status\":\"ok\"}");
        Assert.Equal("raw", tag);
    }

    [Fact]
    public void Detect_MalformedJsonBody_ReturnsRaw()
    {
        var format = WireFormatDetector.DetectFormat("/unknown", "{ not valid json ... ");
        Assert.Equal(WireFormat.Raw, format);

        var tag = WireFormatDetector.Detect("/unknown", "{ not valid json ... ");
        Assert.Equal("raw", tag);
    }

    [Fact]
    public void Detect_NullOrEmptyInputs_SafeFallback()
    {
        var format = WireFormatDetector.DetectFormat(null, null);
        Assert.Equal(WireFormat.Raw, format);

        var tag = WireFormatDetector.Detect(null, null);
        Assert.Equal("raw", tag);

        var tagEmpty = WireFormatDetector.Detect("", "");
        Assert.Equal("raw", tagEmpty);
    }

    [Fact]
    public void Detect_ReflectionHookCompatibility_ReturnsStringTagWithoutAmbiguity()
    {
        var type = typeof(WireFormatDetector);
        var method = type.GetMethod("Detect", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method.Invoke(null, ["/v1beta/models/gemini-2.5-pro:generateContent", null]);
        Assert.Equal("gemini", result);
    }
}