namespace Sizoscope.Tests.Fixtures;

internal sealed class SessionProgressGate : IDisposable
{
    private readonly DependencyGraphSession _session;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public SessionProgressGate(DependencyGraphSession session)
    {
        _session = session;
        session.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (_session.Progress?.Stage != "Sorting node directory" || Interlocked.Exchange(ref _entered, 1) != 0)
            return;
        _reached.SetResult();
        // Watchdog is diagnostic only; test actions synchronize on Reached, never on time.
        if (!_release.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Test did not release the synchronous session progress gate.");
    }

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    public void Release() => _release.Set();

    public void Dispose()
    {
        _session.Changed -= OnChanged;
        _release.Dispose();
    }
}
