namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

/// <summary>
/// Adversarial stress tests for PrefixStream and StreamRelay cancellation across all phases.
/// </summary>
public sealed class PrefixStreamAndRelayAdversarialTests
{
    #region Test Helper Streams

    private sealed class DisposableTrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        public DisposableTrackingStream(byte[] data) : base(data) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class DelayedStream : Stream
    {
        private readonly byte[] _data;
        private readonly TimeSpan _delay;
        private int _position;

        public DelayedStream(byte[] data, TimeSpan delay)
        {
            _data = data;
            _delay = delay;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            var toCopy = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
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

    private sealed class StepCancellingStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly int _cancelAtChunkIndex;
        private readonly CancellationTokenSource _cts;
        private int _currentIndex;
        private int _currentOffset;

        public StepCancellingStream(byte[][] chunks, int cancelAtChunkIndex, CancellationTokenSource cts)
        {
            _chunks = chunks;
            _cancelAtChunkIndex = cancelAtChunkIndex;
            _cts = cts;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_currentIndex >= _chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            if (_currentIndex == _cancelAtChunkIndex && _currentOffset == 0)
            {
                _cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var chunk = _chunks[_currentIndex];
            var available = chunk.Length - _currentOffset;
            var toCopy = Math.Min(buffer.Length, available);
            chunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
            _currentOffset += toCopy;
            if (_currentOffset >= chunk.Length)
            {
                _currentIndex++;
                _currentOffset = 0;
            }

            return ValueTask.FromResult(toCopy);
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

    #endregion

    #region PrefixStream Stress Tests

    [Fact]
    public async Task PrefixStream_PartialReads_DrainsPrefixAcrossMultipleCalls()
    {
        var prefixData = "PrefixPart-"u8.ToArray(); // 11 bytes
        var innerData = "InnerRemainingData"u8.ToArray(); // 18 bytes
        using var innerStream = new MemoryStream(innerData);
        using var prefixStream = new PrefixStream(prefixData, innerStream, leaveInnerStreamOpen: true);

        var smallBuffer = new byte[4];
        var combined = new MemoryStream();

        while (true)
        {
            int read = await prefixStream.ReadAsync(smallBuffer.AsMemory(0, smallBuffer.Length));
            if (read == 0)
            {
                break;
            }
            combined.Write(smallBuffer, 0, read);
        }

        var expected = "PrefixPart-InnerRemainingData";
        var actual = Encoding.UTF8.GetString(combined.ToArray());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PrefixStream_EmptyPrefix_ImmediatelyDelegatesToInnerStream()
    {
        var innerData = "OnlyInnerContent"u8.ToArray();
        using var innerStream = new MemoryStream(innerData);
        using var prefixStream = new PrefixStream(ReadOnlyMemory<byte>.Empty, innerStream, leaveInnerStreamOpen: true);

        var buffer = new byte[64];
        int read = await prefixStream.ReadAsync(buffer.AsMemory(0, buffer.Length));

        Assert.Equal(innerData.Length, read);
        Assert.Equal("OnlyInnerContent", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task PrefixStream_ZeroByteRead_ReturnsZeroWithoutConsumingPrefix()
    {
        var prefixData = "ImportantPrefix"u8.ToArray();
        var innerData = "AndInner"u8.ToArray();
        using var innerStream = new MemoryStream(innerData);
        using var prefixStream = new PrefixStream(prefixData, innerStream, leaveInnerStreamOpen: true);

        // 1. Zero-byte read
        int zeroRead = await prefixStream.ReadAsync(Memory<byte>.Empty);
        Assert.Equal(0, zeroRead);

        // 2. Subsequent full read must still read the entire prefix intact
        var fullBuffer = new byte[128];
        int read = await prefixStream.ReadAsync(fullBuffer.AsMemory(0, fullBuffer.Length));
        Assert.Equal(prefixData.Length, read);
        Assert.Equal("ImportantPrefix", Encoding.UTF8.GetString(fullBuffer, 0, read));

        // 3. Inner read follows
        int innerRead = await prefixStream.ReadAsync(fullBuffer.AsMemory(0, fullBuffer.Length));
        Assert.Equal(innerData.Length, innerRead);
        Assert.Equal("AndInner", Encoding.UTF8.GetString(fullBuffer, 0, innerRead));
    }

    [Fact]
    public async Task PrefixStream_CrossBoundaryReads_PreservesAllByteValuesInOrder()
    {
        var prefix = new byte[50];
        for (int i = 0; i < 50; i++) prefix[i] = (byte)i;

        var inner = new byte[50];
        for (int i = 0; i < 50; i++) inner[i] = (byte)(i + 50);

        using var innerStream = new MemoryStream(inner);
        using var prefixStream = new PrefixStream(prefix, innerStream, leaveInnerStreamOpen: true);
        using var output = new MemoryStream();

        await prefixStream.CopyToAsync(output);

        var result = output.ToArray();
        Assert.Equal(100, result.Length);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((byte)i, result[i]);
        }
    }

    [Fact]
    public void PrefixStream_Disposal_LeaveInnerStreamOpenTrue_DoesNotDisposeInnerStream()
    {
        var inner = new DisposableTrackingStream("test"u8.ToArray());
        var prefixStream = new PrefixStream("pre"u8.ToArray(), inner, leaveInnerStreamOpen: true);

        prefixStream.Dispose();

        Assert.False(inner.IsDisposed);
        Assert.True(inner.CanRead);
    }

    [Fact]
    public void PrefixStream_Disposal_LeaveInnerStreamOpenFalse_DisposesInnerStream()
    {
        var inner = new DisposableTrackingStream("test"u8.ToArray());
        var prefixStream = new PrefixStream("pre"u8.ToArray(), inner, leaveInnerStreamOpen: false);

        prefixStream.Dispose();

        Assert.True(inner.IsDisposed);
    }

    [Fact]
    public async Task PrefixStream_AsyncDisposal_LeaveInnerStreamOpenTrue_PreservesInnerStream()
    {
        var inner = new DisposableTrackingStream("test"u8.ToArray());
        var prefixStream = new PrefixStream("pre"u8.ToArray(), inner, leaveInnerStreamOpen: true);

        await prefixStream.DisposeAsync();

        Assert.False(inner.IsDisposed);
        Assert.True(inner.CanRead);
    }

    [Fact]
    public async Task PrefixStream_AsyncDisposal_LeaveInnerStreamOpenFalse_DisposesInnerStreamAsync()
    {
        var inner = new DisposableTrackingStream("test"u8.ToArray());
        var prefixStream = new PrefixStream("pre"u8.ToArray(), inner, leaveInnerStreamOpen: false);

        await prefixStream.DisposeAsync();

        Assert.True(inner.IsDisposed);
    }

    #endregion

    #region StreamRelay Cancellation Stress Tests

    [Fact]
    public async Task RelayDirect_PreCancelledToken_AbortsImmediatelyWithoutWriting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var source = new MemoryStream("data"u8.ToArray());
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.Equal(0, dest.Length);
        Assert.Empty(result.CapturedBytes);
    }

    [Fact]
    public async Task RelayDirect_MidStreamCancellation_PreservesPartialCapture()
    {
        using var cts = new CancellationTokenSource();
        var chunks = new[]
        {
            "chunk_1_saved"u8.ToArray(),
            "chunk_2_trigger_cancel"u8.ToArray(),
            "chunk_3_never_reached"u8.ToArray()
        };

        using var source = new StepCancellingStream(chunks, cancelAtChunkIndex: 1, cts);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);

        var captured = Encoding.UTF8.GetString(result.CapturedBytes);
        Assert.Contains("chunk_1_saved", captured);
        Assert.DoesNotContain("chunk_3_never_reached", captured);
    }

    [Fact]
    public async Task RelayDirect_SlowStreamCancellation_AbortsGracefullyDuringWait()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50); // cancel after 50ms

        // Stream with 500ms delay per chunk
        using var source = new DelayedStream("payload"u8.ToArray(), TimeSpan.FromMilliseconds(500));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayDirectAsync(source, dest, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
    }

    [Fact]
    public async Task RelayChunked_PreCancelledToken_AbortsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var source = new MemoryStream("5\r\nhello\r\n0\r\n\r\n"u8.ToArray());
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.Equal(0, dest.Length);
    }

    [Fact]
    public async Task RelayChunked_MidChunkCancellation_PreservesBytesRelayedBeforeAbort()
    {
        using var cts = new CancellationTokenSource();
        var chunks = new[]
        {
            "5\r\nhello\r\n"u8.ToArray(),
            "5\r\nworld\r\n"u8.ToArray(),
            "0\r\n\r\n"u8.ToArray()
        };

        using var source = new StepCancellingStream(chunks, cancelAtChunkIndex: 1, cts);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);

        var captured = Encoding.UTF8.GetString(result.CapturedBytes);
        Assert.Contains("hello", captured);
        Assert.DoesNotContain("world", captured);
    }

    [Fact]
    public async Task RelayChunked_CancellationDuringTrailerParsing_PreservesPayloadAndAborts()
    {
        using var cts = new CancellationTokenSource();
        var chunks = new[]
        {
            "5\r\nhello\r\n"u8.ToArray(),
            "0\r\n"u8.ToArray(),
            "Trailer-Header: cancel_now\r\n"u8.ToArray(),
            "\r\n"u8.ToArray()
        };

        using var source = new StepCancellingStream(chunks, cancelAtChunkIndex: 2, cts);
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, dechunkForCapture: true, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);

        var captured = Encoding.UTF8.GetString(result.CapturedBytes);
        Assert.Equal("hello", captured);
    }

    [Fact]
    public async Task RelayChunked_SlowChunkHeaderCancellation_AbortsGracefully()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        using var source = new DelayedStream("5\r\nhello\r\n0\r\n\r\n"u8.ToArray(), TimeSpan.FromMilliseconds(500));
        using var dest = new MemoryStream();

        var result = await StreamRelay.RelayChunkedAsync(source, dest, cancellationToken: cts.Token);

        Assert.False(result.IsSuccess);
        Assert.True(result.ClientAborted);
        Assert.False(result.UpstreamCompleted);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
    }

    #endregion
}