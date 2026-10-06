namespace Sizoscope.Tests.Fixtures;

internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
{
    public int Reads { get; private set; }

    public override int Read(Span<byte> buffer)
    {
        int count = base.Read(buffer);
        if (++Reads == 1) cancellation.Cancel();
        return count;
    }
}
