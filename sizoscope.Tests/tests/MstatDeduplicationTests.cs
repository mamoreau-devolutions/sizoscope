using Sizoscope.Tests.Fixtures;
using Xunit;

namespace Sizoscope.Tests;

public sealed class MstatDeduplicationTests
{
    [Fact]
    public void Read_Version22Payload_ParsesDeduplicatedMethods()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var kept = builder.AddMethod("Run", owner);
        builder.Method(kept, 48);
        builder.Folded(builder.AddMethod("RunAlias", owner), kept);

        using MstatData data = builder.Read();
        MstatData.MstatDeduplicatedBody body =
            Assert.Single(data.GetDeduplicatedBodies());

        Assert.Equal(1, data.DeduplicatedBodyCount);
        Assert.Equal(1, data.DeduplicatedAliasCount);
        Assert.Equal(48, data.DeduplicatedSavedSize);
        Assert.Equal(48, body.Size);
        Assert.Equal(48, body.SavedSize);
    }

    [Fact]
    public void GetDeduplicatedBodies_MultipleAliasesForSameTarget_GroupsByKeptBody()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var kept = builder.AddMethod("Run", owner);
        builder.Method(kept, 40);
        builder.Folded(builder.AddMethod("FirstAlias", owner), kept);
        builder.Folded(builder.AddMethod("SecondAlias", owner), kept);

        using MstatData data = builder.Read();
        MstatData.MstatDeduplicatedBody body =
            Assert.Single(data.GetDeduplicatedBodies());

        Assert.Equal(2, body.AliasCount);
        Assert.Equal(
            new[]
            {
                "Example.Worker.FirstAlias() : Void",
                "Example.Worker.SecondAlias() : Void",
            },
            body.GetAliasNames().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void DeduplicatedBody_Name_IsFullyQualified()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker", "Company.Product");
        var kept = builder.AddMethod("Execute", owner);
        builder.Method(kept, 12);
        builder.Folded(builder.AddMethod("Alias", owner), kept);

        using MstatData data = builder.Read();
        MstatData.MstatDeduplicatedBody body =
            Assert.Single(data.GetDeduplicatedBodies());

        Assert.Equal(
            "Company.Product.Worker.Execute() : Void",
            body.Name);
        Assert.Equal(body.Name, body.ToString());
    }

    [Fact]
    public void DeduplicatedCounts_ReportBodiesAndAliases()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var firstBody = builder.AddMethod("First", owner);
        var secondBody = builder.AddMethod("Second", owner);
        builder.Method(firstBody, 10);
        builder.Method(secondBody, 20);
        builder.Folded(builder.AddMethod("FirstAliasA", owner), firstBody);
        builder.Folded(builder.AddMethod("FirstAliasB", owner), firstBody);
        builder.Folded(builder.AddMethod("SecondAlias", owner), secondBody);

        using MstatData data = builder.Read();
        int[] aliasesPerBody = data.GetDeduplicatedBodies()
            .Select(body => body.AliasCount)
            .OrderBy(count => count)
            .ToArray();

        Assert.Equal(2, data.DeduplicatedBodyCount);
        Assert.Equal(3, data.DeduplicatedAliasCount);
        Assert.Equal(new[] { 1, 2 }, aliasesPerBody);
    }

    [Fact]
    public void DeduplicatedSavedSize_MultipliesBodySizeByAliasCountWithoutChangingTotalSize()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var kept = builder.AddMethod("Run", owner);
        builder.Method(kept, 64);
        builder.Folded(builder.AddMethod("AliasA", owner), kept);
        builder.Folded(builder.AddMethod("AliasB", owner), kept);
        builder.Folded(builder.AddMethod("AliasC", owner), kept);

        using MstatData data = builder.Read();
        MstatData.MstatDeduplicatedBody body =
            Assert.Single(data.GetDeduplicatedBodies());

        Assert.Equal(64, body.Size);
        Assert.Equal(3, body.AliasCount);
        Assert.Equal(192, body.SavedSize);
        Assert.Equal(192, data.DeduplicatedSavedSize);
        Assert.Equal(64, data.Size);
    }

    [Fact]
    public void Read_NegativeDeduplicatedTargetCount_ThrowsBadImageFormatException()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        builder.MalformedFoldedCount(
            builder.AddMethod("Alias", owner),
            -1);

        BadImageFormatException exception =
            Assert.Throws<BadImageFormatException>(() => builder.Read());

        Assert.Contains(
            "Invalid deduplicated method count -1",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Version21WithDeduplicatedPayload_IgnoresUnsupportedPayload()
    {
        var builder = new MstatPeBuilder("2.1.0.0");
        var owner = builder.AddType("Worker");
        var kept = builder.AddMethod("Run", owner);
        builder.Method(kept, 24);
        builder.Folded(builder.AddMethod("Alias", owner), kept);

        using MstatData data = builder.Read();

        Assert.Equal(0, data.DeduplicatedBodyCount);
        Assert.Equal(0, data.DeduplicatedAliasCount);
        Assert.Equal(0, data.DeduplicatedSavedSize);
        Assert.Empty(data.GetDeduplicatedBodies());
        Assert.Equal(24, data.Size);
    }

    [Fact]
    public void Read_Version22WithoutDeduplicatedPayload_ReturnsEmptyDeduplicationData()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        builder.Method(builder.AddMethod("Run", owner), 16);

        using MstatData data = builder.Read();

        Assert.Equal(0, data.DeduplicatedBodyCount);
        Assert.Equal(0, data.DeduplicatedAliasCount);
        Assert.Equal(0, data.DeduplicatedSavedSize);
        Assert.Empty(data.GetDeduplicatedBodies());
        Assert.Equal(16, data.Size);
    }

    [Fact]
    public void Read_ZeroDeduplicatedTargetCount_IsAcceptedAndIgnored()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        builder.MalformedFoldedCount(
            builder.AddMethod("UnmappedAlias", owner),
            0);

        using MstatData data = builder.Read();

        Assert.Equal(0, data.DeduplicatedBodyCount);
        Assert.Equal(0, data.DeduplicatedAliasCount);
        Assert.Equal(0, data.DeduplicatedSavedSize);
        Assert.Empty(data.GetDeduplicatedBodies());
    }

    [Fact]
    public void Read_MethodSpecificationKeptBody_ReportsGenericNameAndSize()
    {
        var builder = new MstatPeBuilder("2.2.0.0");
        var owner = builder.AddType("Worker");
        var definition = builder.AddMethod("Create", owner, generic: true);
        var kept = builder.AddMethodSpec(definition, 0x08);
        builder.Method(kept, 30);
        builder.Folded(builder.AddMethod("Alias", owner), kept);

        using MstatData data = builder.Read();
        MstatData.MstatDeduplicatedBody body =
            Assert.Single(data.GetDeduplicatedBodies());

        Assert.Equal("Example.Worker.Create<Int32>() : Void", body.Name);
        Assert.Equal(30, body.Size);
        Assert.Equal(1, body.AliasCount);
        Assert.Equal(30, body.SavedSize);
        Assert.Equal(30, data.DeduplicatedSavedSize);
        Assert.Equal(30, data.Size);
    }
}
