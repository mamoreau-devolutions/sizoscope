using System.Globalization;
using Xunit;

namespace Sizoscope.Tests;

public sealed class FileSizeFormatterTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(-1L, "-1 B")]
    [InlineData(41L, "41 B")]
    [InlineData(-41L, "-41 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(-1023L, "-1023 B")]
    [InlineData(1536L, "1.5 kB")]
    [InlineData(-1536L, "-1.5 kB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(-1572864L, "-1.5 MB")]
    [InlineData(1610612736L, "1.5 GB")]
    [InlineData(-1610612736L, "-1.5 GB")]
    [InlineData(1649267441664L, "1.5 TB")]
    [InlineData(-1649267441664L, "-1.5 TB")]
    [InlineData(1234L, "1.2 kB")]
    [InlineData(-1234L, "-1.2 kB")]
    [InlineData(12345678L, "11.8 MB")]
    [InlineData(-12345678L, "-11.8 MB")]
    [InlineData(12345678901L, "11.5 GB")]
    [InlineData(-12345678901L, "-11.5 GB")]
    [InlineData(12345678901234L, "11.2 TB")]
    [InlineData(-12345678901234L, "-11.2 TB")]
    public void Format_BytesAndFractionalUnitsMatchLiteralOracles(long size, string expected)
    {
        AssertInvariantFormat(size, expected);
    }

    [Theory]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 kB")]
    [InlineData(1025L, "1.0 kB")]
    [InlineData(-1023L, "-1023 B")]
    [InlineData(-1024L, "-1.0 kB")]
    [InlineData(-1025L, "-1.0 kB")]
    [InlineData(1048575L, "1024.0 kB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1048577L, "1.0 MB")]
    [InlineData(-1048575L, "-1024.0 kB")]
    [InlineData(-1048576L, "-1.0 MB")]
    [InlineData(-1048577L, "-1.0 MB")]
    [InlineData(1073741823L, "1024.0 MB")]
    [InlineData(1073741824L, "1.0 GB")]
    [InlineData(1073741825L, "1.0 GB")]
    [InlineData(-1073741823L, "-1024.0 MB")]
    [InlineData(-1073741824L, "-1.0 GB")]
    [InlineData(-1073741825L, "-1.0 GB")]
    [InlineData(1099511627775L, "1024.0 GB")]
    [InlineData(1099511627776L, "1.0 TB")]
    [InlineData(1099511627777L, "1.0 TB")]
    [InlineData(-1099511627775L, "-1024.0 GB")]
    [InlineData(-1099511627776L, "-1.0 TB")]
    [InlineData(-1099511627777L, "-1.0 TB")]
    public void Format_ExactAndAdjacentUnitBoundaries(long size, string expected)
    {
        AssertInvariantFormat(size, expected);
    }

    [Theory]
    [InlineData(3300000000L, "3.1 GB")]
    [InlineData(-3300000000L, "-3.1 GB")]
    [InlineData(long.MaxValue, "8388608.0 TB")]
    [InlineData(long.MinValue, "-8388608.0 TB")]
    public void Format_LargeAndInt64Extrema(long size, string expected)
    {
        AssertInvariantFormat(size, expected);
    }

    [Fact]
    public void Format_UsesCurrentCultureAndRestoresCallerCulture()
    {
        CultureInfo caller = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1,5 kB", FileSizeFormatter.Format(1536));
            Assert.Equal("-1,5 kB", FileSizeFormatter.Format(-1536));
            Assert.Equal("41 B", FileSizeFormatter.Format(41));
        }
        finally
        {
            CultureInfo.CurrentCulture = caller;
        }
        Assert.Same(caller, CultureInfo.CurrentCulture);
    }

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
        Assert.Same(caller, CultureInfo.CurrentCulture);
    }
}
