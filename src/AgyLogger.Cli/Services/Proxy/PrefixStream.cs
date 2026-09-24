namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A wrapping stream that drains an initial memory buffer before reading from an underlying stream.
/// Supports leaveInnerStreamOpen for deterministic outer disposal control.
/// </summary>
public sealed class PrefixStream : Stream
{
    private ReadOnlyMemory<byte> _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveInnerStreamOpen;

    public PrefixStream(ReadOnlyMemory<byte> prefix, Stream inner, bool leaveInnerStreamOpen = false)
    {
        _prefix = prefix;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveInnerStreamOpen = leaveInnerStreamOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!_prefix.IsEmpty)
        {
            var toCopy = Math.Min(_prefix.Length, buffer.Length);
            _prefix.Span[..toCopy].CopyTo(buffer);
            _prefix = _prefix[toCopy..];
            return toCopy;
        }
        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_prefix.IsEmpty)
        {
            var toCopy = Math.Min(_prefix.Length, buffer.Length);
            _prefix.Span[..toCopy].CopyTo(buffer.Span);
            _prefix = _prefix[toCopy..];
            return toCopy;
        }
        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _prefix = default;
            if (!_leaveInnerStreamOpen)
            {
                _inner.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _prefix = default;
        if (!_leaveInnerStreamOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}