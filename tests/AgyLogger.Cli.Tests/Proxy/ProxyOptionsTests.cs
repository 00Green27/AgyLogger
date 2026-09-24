namespace AgyLogger.Cli.Tests.Proxy;

using System;

using AgyLogger.Cli.Models.Proxy;

using Xunit;

public class ProxyOptionsTests
{
    [Fact]
    public void DefaultValues_MatchSpecification()
    {
        var options = new ProxyOptions();

        Assert.Equal(8888, options.Port);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal("https://generativelanguage.googleapis.com", options.ReverseTargetUrl);
        Assert.Equal("./.agylogs/requests", options.LogsDirectory);
        Assert.False(options.SaveRawCompanionFiles);
        Assert.True(options.FilterHousekeepingRequests);
        Assert.True(options.RejectWebSocketUpgrades);
        Assert.Equal(20, options.BurstThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), options.BurstWindow);
        Assert.Equal("Antigravity CLI", options.DefaultAgentName);
        Assert.Equal(WireFormat.Gemini, options.DefaultWireFormat);
        Assert.Null(options.CaCertPath);
        Assert.False(options.AutoTrustRootCertificate);
    }

    [Fact]
    public void Validate_SucceedsOnDefaultOptions()
    {
        var options = new ProxyOptions();
        var exception = Record.Exception(() => options.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(8888)]
    [InlineData(65535)]
    public void Validate_SucceedsOnValidPort(int port)
    {
        var options = new ProxyOptions { Port = port };
        var exception = Record.Exception(() => options.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-8888)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void Validate_ThrowsOnInvalidPort(int port)
    {
        var options = new ProxyOptions { Port = port };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsOnInvalidHost(string? host)
    {
        var options = new ProxyOptions { Host = host! };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("/relative/path")]
    public void Validate_ThrowsOnInvalidReverseTargetUrl(string? url)
    {
        var options = new ProxyOptions { ReverseTargetUrl = url! };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsOnInvalidLogsDirectory(string? dir)
    {
        var options = new ProxyOptions { LogsDirectory = dir! };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_ThrowsOnInvalidBurstThreshold(int threshold)
    {
        var options = new ProxyOptions { BurstThreshold = threshold };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsOnInvalidBurstWindow()
    {
        var zeroWindow = new ProxyOptions { BurstWindow = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => zeroWindow.Validate());

        var negativeWindow = new ProxyOptions { BurstWindow = TimeSpan.FromMilliseconds(-100) };
        Assert.Throws<ArgumentOutOfRangeException>(() => negativeWindow.Validate());
    }

    [Fact]
    public void WithExpression_CreatesDistinctConfig()
    {
        var original = new ProxyOptions();
        var modified = original with { Port = 9090, Host = "0.0.0.0" };

        Assert.Equal(8888, original.Port);
        Assert.Equal("127.0.0.1", original.Host);
        Assert.Equal(9090, modified.Port);
        Assert.Equal("0.0.0.0", modified.Host);
        Assert.Equal(original.ReverseTargetUrl, modified.ReverseTargetUrl);
    }
}