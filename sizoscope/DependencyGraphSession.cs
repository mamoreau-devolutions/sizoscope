internal enum DependencyGraphState { NotRequested, Unavailable, Loading, Ready, Canceled, Failed }

internal sealed class DependencyGraphSession : IDisposable
{
    private readonly object _gate = new();
    private readonly DependencyGraphInput _input;
    private CancellationTokenSource _cancellation;
    private Task _loading;
    private DependencyGraphIndex _store;
    private int _references = 1;
    private int _operations;
    private bool _disposed;
    private volatile DependencyGraphState _state;
    private volatile Exception _error;
    private volatile DependencyGraphProgress _progress;
    public DependencyGraphState State { get => _state; private set => _state = value; }
    public Exception Error { get => _error; private set => _error = value; }
    public DependencyGraphProgress Progress { get => _progress; private set => _progress = value; }
    public event Action Changed;
    public DependencyGraphIndex Store
    {
        get
        {
            lock (_gate)
                return _store ?? throw new InvalidOperationException("Dependencies are not ready.");
        }
    }

    public DependencyGraphSession(DependencyGraphInput input)
    {
        _input = input;
        State = input == null ? DependencyGraphState.Unavailable : DependencyGraphState.NotRequested;
    }

    public DependencyGraphSession Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _references++;
            return this;
        }
    }

    internal IDisposable RetainOperation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _references++;
            _operations++;
            return new OperationLease(this);
        }
    }

    public Task LoadAsync(bool rebuild = false, string cacheRoot = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_input == null)
                throw new InvalidOperationException("The companion dependency graph was not found.");
            if (_loading is { IsCompleted: false })
                return _loading;
            if (State == DependencyGraphState.Ready && !rebuild)
                return Task.CompletedTask;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            State = DependencyGraphState.Loading;
            Error = null;
            Progress = null;
            _loading = LoadCoreAsync(rebuild, cacheRoot, _cancellation.Token);
        }
        Changed?.Invoke();
        return _loading;
    }

    private async Task LoadCoreAsync(bool rebuild, string cacheRoot, CancellationToken token)
    {
        // Yield before taking the session lock again, including on immediate cache errors.
        await Task.Yield();
        try
        {
            var progress = new ProgressRelay(value =>
            {
                lock (_gate)
                    Progress = value;
                Changed?.Invoke();
            });
            var store = await DependencyGraphCache.OpenAsync(_input, rebuild, token, progress, cacheRoot).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || token.IsCancellationRequested)
                {
                    store.Dispose();
                    token.ThrowIfCancellationRequested();
                    return;
                }
                _store?.Dispose();
                _store = store;
                State = DependencyGraphState.Ready;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
                State = DependencyGraphState.Canceled;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or OverflowException)
        {
            lock (_gate)
            {
                Error = exception;
                State = DependencyGraphState.Failed;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_disposed)
                    _cancellation?.Dispose();
            }
            Changed?.Invoke();
        }
    }

    public void Cancel()
    {
        lock (_gate)
            _cancellation?.Cancel();
    }

    public void Dispose() => Release(false);

    private void Release(bool operation)
    {
        lock (_gate)
        {
            if (_references == 0)
                return;
            if (operation)
                _operations--;
            _references--;
            if (_references == _operations)
                _cancellation?.Cancel();
            if (_references != 0)
                return;
            _disposed = true;
            _cancellation?.Cancel();
            _store?.Dispose();
            if (_loading == null || _loading.IsCompleted)
                _cancellation?.Dispose();
        }
    }

    private sealed class OperationLease(DependencyGraphSession session) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                session.Release(true);
        }
    }

    private sealed class ProgressRelay(Action<DependencyGraphProgress> report) : IProgress<DependencyGraphProgress>
    {
        public void Report(DependencyGraphProgress value) => report(value);
    }
}
