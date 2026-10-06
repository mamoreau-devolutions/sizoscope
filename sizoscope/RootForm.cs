namespace sizoscope
{
    public partial class RootForm : Form
    {
        private readonly MstatData _data, _compare;
        private readonly CancellationTokenSource _closing = new();
        private readonly HashSet<TreeNode> _loading = new();

        private sealed record PageRequest(TreeNode Parent, long Offset);

        public RootForm()
        {
            InitializeComponent();
            _tree.BeforeExpand += async (_, e) =>
            {
                if (e.Node.Tag is DependencyGraphPath && e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is PageRequest request)
                    await LoadPageAsync(request);
            };
            _tree.NodeMouseDoubleClick += async (_, e) =>
            {
                if (e.Node.Tag is PageRequest request)
                    await LoadPageAsync(request);
            };
            _tree.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _tree.SelectedNode?.Tag is PageRequest request)
                {
                    e.Handled = true;
                    await LoadPageAsync(request);
                }
            };
        }

        public RootForm(MstatData data, MstatData.Node node, MstatData compare = null, bool rootMissing = false) : this()
        {
            _data = data;
            _compare = compare;
            Text = $"Path from roots to {node.Name}" +
                (compare != null && !compare.DgmlAvailable ? " (comparison dependencies not loaded)" : "");
            var root = MakeNode(new DependencyGraphNode(node.Id, node.Name), null, null, rootMissing);
            _tree.Nodes.Add(root);
        }

        private TreeNode MakeNode(DependencyGraphNode node, string reason, DependencyGraphPath parent, bool missing)
        {
            string text = reason == null ? node.Name : $"({reason}) {node.Name}";
            var result = new TreeNode(text);
            if (missing)
                result.ForeColor = Color.RebeccaPurple;
            if (parent?.Contains(node.Id) == true)
            {
                result.Text += " [cycle]";
                return result;
            }
            result.Tag = new DependencyGraphPath(node.Id, parent);
            result.Nodes.Add(new TreeNode("Expand to load dependencies") { Tag = new PageRequest(result, 0) });
            return result;
        }

        private async Task LoadPageAsync(PageRequest request)
        {
            var parent = request.Parent;
            if (IsDisposed || !_loading.Add(parent))
                return;
            var branch = (DependencyGraphPath)parent.Tag;
            CancellationToken token = _closing.Token;
            var marker = parent.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is PageRequest);
            if (marker != null)
                marker.Text = "Loading dependencies...";
            try
            {
                var result = await Task.Run(() =>
                {
                    if (!_data.DgmlAvailable)
                        throw new InvalidOperationException("Dependencies are loading or were canceled. Load/retry them from the parent window.");
                    var page = _data.Graph.Store.ReadIncoming(branch.Id, request.Offset, token: token);
                    bool compareReady = _compare?.DgmlAvailable == true;
                    var missing = page.Edges.Select(edge => compareReady && !_compare.ContainsNamedNode(edge.Node.Name)).ToArray();
                    return (page, missing);
                }, token);
                if (IsDisposed || token.IsCancellationRequested)
                    return;
                _tree.BeginUpdate();
                try
                {
                    marker?.Remove();
                    for (int i = 0; i < result.page.Edges.Count; i++)
                    {
                        var edge = result.page.Edges[i];
                        parent.Nodes.Add(MakeNode(edge.Node, edge.Reason, branch, result.missing[i]));
                    }
                    if (result.page.NextOffset.HasValue)
                        parent.Nodes.Add(new TreeNode("Load more (double-click or Enter)")
                        { Tag = new PageRequest(parent, result.page.NextOffset.Value) });
                }
                finally
                {
                    _tree.EndUpdate();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ObjectDisposedException)
            {
                if (!IsDisposed && marker != null)
                    marker.Text = $"{exception.Message} (double-click to retry)";
            }
            finally
            {
                _loading.Remove(parent);
            }
        }
    }
}
