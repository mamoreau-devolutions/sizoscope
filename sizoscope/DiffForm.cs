using static MstatData;

namespace sizoscope
{
    public partial class DiffForm : Form
    {
        private MstatData _leftDiff, _rightDiff;
        private TreeLogic.Sorter _sorter;
        private DependencyGraphToolbar _leftDependencies, _rightDependencies;
        private readonly CancellationTokenSource _closing = new();

        /// <summary>
        /// Creates a DiffForm with pre-computed diff data (used from MainForm diff button).
        /// </summary>
        public DiffForm(MstatData leftDiff, MstatData rightDiff, long diffSize)
        {
            InitializeComponent();
            InitializeCommon();

            _leftDiff = leftDiff;
            _rightDiff = rightDiff;

            TreeLogic.RefreshTree(_leftTree, _leftDiff, _sorter);
            TreeLogic.RefreshTree(_rightTree, _rightDiff, _sorter);

            _toolStripStatusLabel.Text = $"Total accounted difference: {TreeLogic.AsFileSize(diffSize)}";
        }

        /// <summary>
        /// Creates a DiffForm that loads and diffs the files asynchronously after showing the UI.
        /// Used from CLI when two file paths are passed.
        /// </summary>
        public DiffForm(string leftFilePath, string rightFilePath)
        {
            InitializeComponent();
            InitializeCommon();

            _toolStripStatusLabel.Text = "Loading and computing diff...";
            _leftTree.Enabled = false;
            _rightTree.Enabled = false;

            Shown += async (s, e) => await LoadDiffAsync(leftFilePath, rightFilePath);
        }

        private void InitializeCommon()
        {
            _sorter = TreeLogic.Sorter.BySize();

            using var imageSource = new MainForm();
            _leftTree.ImageList = new ImageList { ImageStream = imageSource._imageList.ImageStream };
            _rightTree.ImageList = _leftTree.ImageList;
            var toolbar = new ToolStrip();
            toolStripContainer1.TopToolStripPanel.Controls.Add(toolbar);
            _leftDependencies = new DependencyGraphToolbar(this, toolbar, statusStrip1, () => _leftDiff, "Left dependencies");
            _rightDependencies = new DependencyGraphToolbar(this, toolbar, statusStrip1, () => _rightDiff, "Right dependencies");
        }

        private async Task LoadDiffAsync(string leftFilePath, string rightFilePath)
        {
            try
            {
                var result = await Task.Run(() =>
                {
                    using var rl = ResolvedFile.Open(leftFilePath);
                    using var rr = ResolvedFile.Open(rightFilePath);

                    MstatData left;
                    using (var ms = rl.OpenMstat())
                        left = MstatData.Read(ms, rl.MstatLength, rl.GraphInput);
                    using var leftLifetime = left;

                    MstatData right;
                    using (var ms = rr.OpenMstat())
                        right = MstatData.Read(ms, rr.MstatLength, rr.GraphInput);
                    using var rightLifetime = right;

                    _closing.Token.ThrowIfCancellationRequested();
                    (MstatData ld, MstatData rd) = MstatData.Diff(left, right);
                    long ds = checked(right.Size - left.Size);

                    return (ld, rd, ds);
                }, _closing.Token);

                if (IsDisposed || _closing.IsCancellationRequested)
                {
                    result.ld.Dispose();
                    result.rd.Dispose();
                    return;
                }
                _leftDiff = result.ld;
                _rightDiff = result.rd;

                TreeLogic.RefreshTree(_leftTree, _leftDiff, _sorter);
                TreeLogic.RefreshTree(_rightTree, _rightDiff, _sorter);

                _toolStripStatusLabel.Text = $"Total accounted difference: {TreeLogic.AsFileSize(result.ds)}";
                _leftTree.Enabled = true;
                _rightTree.Enabled = true;
            }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or OverflowException)
            {
                if (!IsDisposed)
                    _toolStripStatusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void _leftTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeLogic.BeforeExpand(e.Node, _sorter);
        }

        private void _rightTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeLogic.BeforeExpand(e.Node, _sorter);
        }

        private async void _leftTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            await NodeMouseDoubleClickCommon(e.Node, _leftDiff, _rightDiff);
        }

        private Task NodeMouseDoubleClickCommon(TreeNode treeNode, MstatData data, MstatData compare)
        {
            int? id = treeNode.Tag switch
            {
                MstatTypeDefinition typedef => typedef.NodeId,
                MstatTypeSpecification typespec => typespec.NodeId,
                MstatMemberDefinition memberdef => memberdef.NodeId,
                MstatMethodSpecification methodspec => methodspec.NodeId,
                int val => val,
                _ => null
            };

            if (id.HasValue)
                return DependencyGraphToolbar.ShowRootsAsync(this, data, id.Value, compare);
            return Task.CompletedTask;
        }

        private async void _rightTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            await NodeMouseDoubleClickCommon(e.Node, _rightDiff, _leftDiff);
        }
    }
}
