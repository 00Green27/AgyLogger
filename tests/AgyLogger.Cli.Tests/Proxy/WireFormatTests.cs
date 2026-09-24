namespace AgyLogger.Cli.Tests.Proxy;

using AgyLogger.Cli.Models.Proxy;

using Xunit;

public class WireFormatTests
{
    [Fact]
    public void ToWireTag_MapsCorrectly()
    {
        Assert.Equal("gemini", WireFormat.Gemini.ToWireTag());
        Assert.Equal("anthropic", WireFormat.Anthropic.ToWireTag());
        Assert.Equal("openai", WireFormat.OpenAi.ToWireTag());
        Assert.Equal("raw", WireFormat.Raw.ToWireTag());
        Assert.Equal("unknown", WireFormat.Unknown.ToWireTag());
        Assert.Equal("unknown", ((WireFormat)99).ToWireTag());
    }

    [Theory]
    [InlineData("gemini", WireFormat.Gemini)]
    [InlineData("GEMINI", WireFormat.Gemini)]
    [InlineData("  gemini  ", WireFormat.Gemini)]
    [InlineData("anthropic", WireFormat.Anthropic)]
    [InlineData("ANTHROPIC", WireFormat.Anthropic)]
    [InlineData("openai", WireFormat.OpenAi)]
    [InlineData("OpenAI", WireFormat.OpenAi)]
    [InlineData("raw", WireFormat.Raw)]
    [InlineData("RAW", WireFormat.Raw)]
    [InlineData(null, WireFormat.Unknown)]
    [InlineData("", WireFormat.Unknown)]
    [InlineData("   ", WireFormat.Unknown)]
    [InlineData("invalid", WireFormat.Unknown)]
    [InlineData("unrecognized", WireFormat.Unknown)]
    public void FromWireTag_ParsesCorrectly(string? tag, WireFormat expected)
    {
        var result = WireFormatExtensions.FromWireTag(tag);
        Assert.Equal(expected, result);
    }
}