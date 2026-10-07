using Sizoscope.Tests.Fixtures;
using System.Windows.Forms;
using Xunit;

namespace Sizoscope.Tests;

public sealed class TreeLogicTests
{
    [Fact]
    public void RefreshTree_SameNameSatelliteAssemblies_UsesCultureAwareDisplayNames()
    {
        var builder = new MstatPeBuilder();
        var french = builder.AddAssembly("App.resources", "fr");
        var german = builder.AddAssembly("App.resources", "de");
        builder.Method(
            builder.AddMethod("Get", builder.AddType("Strings", "Resources", french)),
            11);
        builder.Method(
            builder.AddMethod("Get", builder.AddType("Strings", "Resources", german)),
            13);
        byte[] bytes = builder.Build();
        using var data = MstatData.Read(new MemoryStream(bytes), bytes.Length);
        using var tree = new TreeView();

        sizoscope.TreeLogic.RefreshTree(
            tree,
            data,
            sizoscope.TreeLogic.Sorter.ByName());

        string[] labels = tree.Nodes.Cast<TreeNode>().Select(node => node.Text).ToArray();
        Assert.Contains("App.resources [fr] (11 B)", labels);
        Assert.Contains("App.resources [de] (13 B)", labels);
        Assert.Equal(
            2,
            labels.Count(
                label => label.StartsWith("App.resources [", StringComparison.Ordinal)));
        Assert.NotEqual(
            labels.Single(label => label.Contains("[fr]", StringComparison.Ordinal)),
            labels.Single(label => label.Contains("[de]", StringComparison.Ordinal)));
    }
}
