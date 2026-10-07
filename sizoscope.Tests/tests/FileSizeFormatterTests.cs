using System.Globalization;
using Xunit;

namespace Sizoscope.Tests;

public sealed class FileSizeFormatterTests
{
    [Theory]
    [InlineData(1_073_741_823L, "1024.0 MB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    [InlineData(1_073_741_825L, "1.0 GB")]
    [InlineData(-1_073_741_823L, "-1024.0 MB")]
    [InlineData(-1_073_741_824L, "-1.0 GB")]
    [InlineData(3_300_000_000L, "3.1 GB")]
    public void Format_GigabyteBoundariesAndLargeValues(long size, string expected) =>
        AssertInvariantFormat(size, expected);

    [Theory]
    [InlineData(1_099_511_627_775L, "1024.0 GB")]
    [InlineData(1_099_511_627_776L, "1.0 TB")]
    [InlineData(1_099_511_627_777L, "1.0 TB")]
    [InlineData(-1_099_511_627_775L, "-1024.0 GB")]
    [InlineData(-1_099_511_627_776L, "-1.0 TB")]
    [InlineData(12_345_678_901_234L, "11.2 TB")]
    public void Format_TerabyteBoundariesAndAdjacentValues(long size, string expected) =>
        AssertInvariantFormat(size, expected);

    [Theory]
    [InlineData(long.MaxValue, "8388608.0 TB")]
    [InlineData(long.MinValue, "-8388608.0 TB")]
    public void Format_Int64Extrema(long size, string expected) =>
        AssertInvariantFormat(size, expected);

    private static void AssertInvariantFormat(long size, string expected)
    {
        CultureInfo caller = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, FileSizeFormatter.Format(size));
        }
        finally
        {
            CultureInfo.CurrentCulture = caller;
        }
    }
}
