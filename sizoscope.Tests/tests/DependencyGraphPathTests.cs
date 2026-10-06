using Xunit;

namespace Sizoscope.Tests;

public sealed class DependencyGraphPathTests
{
    [Fact]
    public void Contains_UsesOnlyTheCurrentBranch()
    {
        var root = new DependencyGraphPath(10, null);
        var left = new DependencyGraphPath(20, root);
        var right = new DependencyGraphPath(30, root);

        Assert.True(left.Contains(10));
        Assert.True(left.Contains(20));
        Assert.False(left.Contains(30));
        Assert.True(right.Contains(10));
        Assert.True(right.Contains(30));
        Assert.False(right.Contains(20));
        Assert.Same(root, left.Parent);
        Assert.Null(root.Parent);
    }

    [Fact]
    public void Contains_DeepChainDoesNotUseRecursiveTraversal()
    {
        var path = new DependencyGraphPath(0, null);
        for (int i = 1; i <= 100_000; i++)
            path = new DependencyGraphPath(i, path);

        Assert.True(path.Contains(0));
        Assert.True(path.Contains(50_000));
        Assert.True(path.Contains(100_000));
        Assert.False(path.Contains(-1));
        Assert.False(path.Contains(100_001));
    }
}
