namespace sizoscope;

internal sealed class DependencyGraphToolbar : IDisposable
{
    private readonly Func<MstatData> _getData;
    private readonly Form _owner;
    private readonly ToolStripMenuItem _load, _cancel, _rebuild;
    private readonly ToolStripStatusLabel _status;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly string _caption;
    private readonly CancellationTokenSource _closing = new();

    public DependencyGraphToolbar(Form owner, ToolStrip toolbar, StatusStrip status, Func<MstatData> getData, string caption)
    {
        _owner = owner;
        _getData = getData;
        _caption = caption;
        var menu = new ToolStripDropDownButton(caption);
        _load = new ToolStripMenuItem("Load / retry dependencies", null, async (_, _) => await LoadAsync(false));
        _cancel = new ToolStripMenuItem("Cancel loading", null, (_, _) => _getData()?.Graph.Cancel());
        _rebuild = new ToolStripMenuItem("Rebuild dependency index", null, async (_, _) => await LoadAsync(true));
        var location = new ToolStripMenuItem("Show cache location", null, (_, _) =>
            MessageBox.Show(_owner, DependencyGraphCache.Root, "Dependency cache"));
        var clear = new ToolStripMenuItem("Remove inactive cached indexes", null, async (_, _) =>
        {
            try
            {
                var result = await DependencyGraphCache.ClearInactiveAsync(_closing.Token);
                if (!_owner.IsDisposed)
                    MessageBox.Show(_owner, $"Removed {result.Removed} indexes; {result.InUse} remain in use.", "Dependency cache");
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (!_owner.IsDisposed)
                    MessageBox.Show(_owner, exception.Message, "Could not remove cached indexes");
            }
        });
        menu.DropDownItems.AddRange([_load, _cancel, _rebuild, new ToolStripSeparator(), location, clear]);
        toolbar.Items.Add(menu);
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_status);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private async Task LoadAsync(bool rebuild)
    {
        var data = _getData();
        if (data == null || !data.DgmlSupported)
            return;
        using var lease = data.RetainBacking();
        await data.Graph.LoadAsync(rebuild);
        if (_owner.IsDisposed || _getData() != data)
            return;
        Refresh();
        if (data.Graph.State == DependencyGraphState.Failed)
            MessageBox.Show(_owner, data.Graph.Error.Message, "Dependency loading failed");
    }

    private void Refresh()
    {
        var data = _getData();
        var state = data?.Graph.State ?? DependencyGraphState.Unavailable;
        bool supported = data?.DgmlSupported == true;
        _load.Enabled = supported && state is not (DependencyGraphState.Loading or DependencyGraphState.Unavailable);
        _cancel.Enabled = state == DependencyGraphState.Loading;
        _rebuild.Enabled = _load.Enabled;
        var progress = data?.Graph.Progress;
        string detail = !supported && data != null ? "unsupported MSTAT version" : state switch
        {
            DependencyGraphState.NotRequested => "not loaded",
            DependencyGraphState.Unavailable => "no companion graph",
            DependencyGraphState.Loading when progress != null =>
                $"{progress.Stage}: {progress.Nodes:N0} nodes, {progress.Edges:N0} links, {TreeLogic.AsFileSize(progress.Bytes)} read",
            DependencyGraphState.Loading => "waiting / loading",
            DependencyGraphState.Ready => "ready",
            DependencyGraphState.Canceled => "canceled; retry available",
            DependencyGraphState.Failed => "failed; retry or rebuild",
            _ => "not loaded"
        };
        _status.Text = $"{_caption}: {detail}";
        _status.ToolTipText = data?.Graph.Error?.Message ?? _status.Text;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _closing.Cancel();
        _closing.Dispose();
    }

    internal static async Task ShowRootsAsync(Form owner, MstatData data, int id, MstatData compare = null, Func<bool> isCurrent = null)
    {
        if (!data.DgmlSupported)
        {
            MessageBox.Show(owner, "Dependency graph information is only available in .NET 8 or later.");
            return;
        }
        if (id < 0)
        {
            MessageBox.Show(owner, "This node was not used directly and is included for display purposes only. Try analyzing sub nodes.");
            return;
        }
        if (data.Graph.State == DependencyGraphState.Unavailable)
        {
            MessageBox.Show(owner, "Dependency graph data was not found. Ensure IlcGenerateDgmlFile=true is specified.");
            return;
        }
        using var lease = data.RetainBacking();
        using var compareLease = compare?.RetainBacking();
        await data.Graph.LoadAsync();
        if (owner.IsDisposed || isCurrent?.Invoke() == false || data.Graph.State != DependencyGraphState.Ready)
        {
            if (!owner.IsDisposed && data.Graph.Error != null)
                MessageBox.Show(owner, data.Graph.Error.Message, "Dependency loading failed");
            return;
        }
        try
        {
            string name = null;
            var result = await Task.Run(() =>
            {
                var node = data.GetNodeForId(id, out name);
                bool missing = node != null && compare?.DgmlAvailable == true && !compare.ContainsNamedNode(node.Name);
                return (node, missing);
            });
            if (owner.IsDisposed || isCurrent?.Invoke() == false)
                return;
            if (result.node == null)
                MessageBox.Show(owner, $"Could not find path to roots from {name}.");
            else
            {
                using var form = new RootForm(data, result.node, compare, result.missing);
                form.ShowDialog(owner);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or BadImageFormatException or ArgumentOutOfRangeException)
        {
            if (!owner.IsDisposed)
                MessageBox.Show(owner, exception.Message, "Could not open dependency roots");
        }
    }
}
