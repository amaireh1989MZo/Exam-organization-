namespace QuestionBank.Core.Extraction;

/// <summary>
/// غلاف قراءة فقط يرمي InvalidDataException إذا تجاوزت البيانات الحد. يحمي من الأحجام المزيفة داخل ZIP
/// ومن الطلبات الأكبر من المسموح، دون الاعتماد على ما يعلنه المصدر عن نفسه.
/// </summary>
public sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int read)
    {
        BytesRead += read;
        if (BytesRead > maxBytes)
            throw new InvalidDataException("Stream exceeds the allowed size.");
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
