namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

public class StreamRelayTests
{
    #region Test Helper Streams

    private sealed class TrackingStream : MemoryStream
    {
        public int WriteCallCount { get; private set; }
        public int FlushCallCount { get; private set; }
        public List<int> ChunkSizes { get; } = [];

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCallCount++;
            ChunkSizes.Add(buffer.Length);
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCallCount++;
            await base.FlushAsync(cancellationToken);
        }
    }

    private sealed class SlowChunkStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunkIndex;

        public SlowChunkStream(params string[] chunks)
        {
            _chunks = chunks.Select(Encoding.UTF8.GetBytes).ToArray();
        }

        public SlowChunkStream(params byte[][] chunks)
        {
            _chunks = chunks;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_chunkIndex >= _chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = _chunks[_chunkIndex++];
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AbortingStream : MemoryStream
    {
        private readonly int _failAfterWrites;
        private int _currentWrites;

        public AbortingStream(int failAfterWrites)
        {
            _failAfterWrites = failAfterWrites;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _currentWrites++;
            if (_currentWrites > _failAfterWrites)
            {
                throw new IOException("An existing connection was forcibly closed by the remote host.",
                    new SocketException((int)SocketError.ConnectionReset));
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    #endregion

    [Fact]
    public async Task RelayDirect_FlushesImmediatelyOnEveryChunk()
    {
        using var source = new SlowChunkStream("chunk1", "chunk2", "chunk3");
        using var dest = new TrackingStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, bufferSize: 1024);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, dest.WriteCallCount);
        Assert.Equal(3, dest.FlushCallCount);
        Assert.Equal(dest.WriteCallCount, dest.FlushCallCount);
        Assert.Equal("chunk1chunk2chunk3", Encoding.UTF8.GetString(result.CapturedBytes));
    }

    [Fact]
    public async Task RelayDirect_CapturesAllBytesInOrder()
    {
        var rawData = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 50)));
        using var source = new MemoryStream(rawData);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, bufferSize: 128);

        Assert.True(result.IsSuccess);
        Assert.True(result.UpstreamCompleted);
        Assert.False(result.ClientAborted);
        Assert.False(result.IsTruncated);
        Assert.Equal(rawData.Length, result.TotalBytesRelayed);
        Assert.Equal(rawData, result.CapturedBytes);
        Assert.Equal(rawData, dest.ToArray());
    }

    [Fact]
    public async Task RelayDirect_RespectsContentLength()
    {
        var fullPayload = "0123456789012345678901234567890123456789"u8.ToArray();
        using var source = new MemoryStream(fullPayload);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, contentLength: 20, bufferSize: 8);

        Assert.True(result.IsSuccess);
        Assert.Equal(20, result.TotalBytesRelayed);
        Assert.Equal(20, dest.Length);
        Assert.Equal(fullPayload[..20], result.CapturedBytes);
        // Source should still have remaining unread bytes
        Assert.Equal(20, source.Position);
    }

    [Fact]
    public async Task RelayDirect_CapsCaptureAtMaxBytes()
    {
        var largeData = new byte[10000];
        Array.Fill<byte>(largeData, 0x42);
        using var source = new MemoryStream(largeData);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, maxCaptureBytes: 1024, bufferSize: 512);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsTruncated);
        Assert.Equal(10000, result.TotalBytesRelayed);
        Assert.Equal(10000, dest.Length); // Pass-through was NOT truncated
        Assert.Equal(1024, result.CapturedBytes.Length); // Capture buffer was capped
        Assert.Equal(largeData[..1024], result.CapturedBytes);
    }

    [Fact]
    public async Task RelayDirect_HandlesClientDisconnect_PreservingCapturedBytes()
    {
        using var source = new SlowChunkStream("chunk_1", "chunk_2", "chunk_3", "chunk_4");
        using var dest = new AbortingStream(failAfterWrites: 2);

        var result = await StreamRelay.RelayDirectAsync(source, dest, bufferSize: 128);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.NotNull(result.Exception);
        Assert.True(result.CapturedBytes.Length > 0);
        // Chunks up to the abort point were safely captured
        var capturedText = Encoding.UTF8.GetString(result.CapturedBytes);
        Assert.Contains("chunk_1", capturedText);
        Assert.Contains("chunk_2", capturedText);
    }

    [Fact]
    public async Task RelayChunked_ForwardsFramingToDestination()
    {
        var rawChunked = "4\r\nWiki\r\n6\r\npedia \r\ne\r\nin \r\n\r\nchunks.\r\n0\r\n\r\n";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(rawChunked));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.UpstreamCompleted);
        // Destination received full HTTP framing
        var destWire = Encoding.ASCII.GetString(dest.ToArray());
        Assert.Equal(rawChunked, destWire);
    }

    [Fact]
    public async Task RelayChunked_DechunksPayloadForCapture()
    {
        var rawChunked = "4\r\nWiki\r\n6\r\npedia \r\ne\r\nin \r\n\r\nchunks.\r\n0\r\n\r\n";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(rawChunked));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true);

        Assert.True(result.IsSuccess);
        // Captured bytes contains only the de-chunked payload
        var captured = Encoding.ASCII.GetString(result.CapturedBytes);
        Assert.Equal("Wikipedia in \r\n\r\nchunks.", captured);
        Assert.DoesNotContain("4\r\n", captured);
        Assert.DoesNotContain("0\r\n\r\n", captured);
    }

    [Fact]
    public async Task RelayChunked_SupportsChunkExtensions()
    {
        var rawChunked = "5;ext=val;flag\r\nhello\r\n0\r\n\r\n";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(rawChunked));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", Encoding.ASCII.GetString(result.CapturedBytes));
        Assert.Equal(rawChunked, Encoding.ASCII.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task RelayChunked_HandlesTrailers()
    {
        var rawChunked = "5\r\nworld\r\n0\r\nExpires: never\r\nX-Checksum: abc123\r\n\r\n";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(rawChunked));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.UpstreamCompleted);
        Assert.Equal("world", Encoding.ASCII.GetString(result.CapturedBytes));
        var destStr = Encoding.ASCII.GetString(dest.ToArray());
        Assert.Contains("Expires: never", destStr);
        Assert.Contains("X-Checksum: abc123", destStr);
    }

    [Fact]
    public async Task RelayChunked_WithSseReassembler_ReconstructsTokens()
    {
        var sseEvent1 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hello \"}]}}]}\n\n";
        var sseEvent2 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"world!\"}]}}]}\n\n";

        var chunk1Hex = $"{sseEvent1.Length:x}\r\n";
        var chunk2Hex = $"{sseEvent2.Length:x}\r\n";
        var rawChunked = $"{chunk1Hex}{sseEvent1}\r\n{chunk2Hex}{sseEvent2}\r\n0\r\n\r\n";

        using var source = new MemoryStream(Encoding.UTF8.GetBytes(rawChunked));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true);

        Assert.True(result.IsSuccess);
        var sseCaptured = Encoding.UTF8.GetString(result.CapturedBytes);
        var parsed = SseChunkReassembler.ReassembleGemini(sseCaptured);
        Assert.Equal("Hello world!", parsed.AssistantText);
    }

    [Fact]
    public async Task Relay_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var source = new SlowChunkStream("chunk1");
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayAsync(source, dest, isChunked: false, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
    }

    [Fact]
    public async Task RelayChunked_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var source = new SlowChunkStream("4\r\ntest\r\n0\r\n\r\n");
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayAsync(source, dest, isChunked: true, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.NetworkReset, true)]
    [InlineData(SocketError.Shutdown, true)]
    [InlineData(SocketError.TimedOut, true)]
    [InlineData(SocketError.OperationAborted, true)]
    [InlineData(SocketError.HostUnreachable, false)]
    [InlineData(SocketError.AccessDenied, false)]
    public void IsClientAbort_AccuratelyClassifiesExceptions(SocketError error, bool expected)
    {
        var sex = new SocketException((int)error);
        Assert.Equal(expected, StreamRelay.IsClientAbort(sex));

        var iex = new IOException("IO wrapper", sex);
        Assert.Equal(expected, StreamRelay.IsClientAbort(iex));
    }

    [Fact]
    public void IsClientAbort_RecognizesCancellationAndDisposal()
    {
        Assert.True(StreamRelay.IsClientAbort(new OperationCanceledException()));
        Assert.True(StreamRelay.IsClientAbort(new TaskCanceledException()));
        Assert.True(StreamRelay.IsClientAbort(new ObjectDisposedException("Stream")));
        Assert.False(StreamRelay.IsClientAbort(new InvalidOperationException()));
        Assert.False(StreamRelay.IsClientAbort(new ArgumentNullException()));
    }

    [Fact]
    public void IsClientAbort_RecognizesNestedAndAggregateExceptions()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionReset);
        var nestedIo = new IOException("Outer IO", new IOException("Inner IO", socketEx));
        Assert.True(StreamRelay.IsClientAbort(nestedIo));

        var aggregate = new AggregateException("Aggregate error", new Exception("Generic"), socketEx);
        Assert.True(StreamRelay.IsClientAbort(aggregate));
    }
}