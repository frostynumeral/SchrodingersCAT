namespace SchrodingersCat.Core.Xliff;

/// <summary>
/// A read-only view of a stream that refuses to read past a byte limit, so an oversized or endless input fails
/// with <see cref="XliffFormatException"/> (or the exception <c>error</c> makes) instead of exhausting memory.
/// </summary>
internal sealed class LimitedReadStream(Stream inner, long maxBytes, Func<string, Exception>? error = null) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(Limit(buffer)));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(Limit(buffer), cancellationToken).ConfigureAwait(false));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Asks for at most one byte more than the limit allows, so reaching the limit exactly is not an error.
    private Span<byte> Limit(Span<byte> buffer) => buffer[..(int)Math.Min(buffer.Length, maxBytes - _read + 1)];

    private Memory<byte> Limit(Memory<byte> buffer) => buffer[..(int)Math.Min(buffer.Length, maxBytes - _read + 1)];

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
            throw error?.Invoke($"The file is larger than the {maxBytes:N0}-byte limit.")
                ?? new XliffFormatException($"The file is larger than the {maxBytes:N0}-byte limit (Xliff20Serializer.MaxInputBytes).");
        return read;
    }
}
