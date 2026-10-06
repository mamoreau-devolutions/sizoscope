internal static class FileSizeFormatter
{
    public static string Format(long size)
    {
        decimal magnitude = Math.Abs((decimal)size);
        return magnitude switch
        {
            < 1024 => $"{size:F0} B",
            < 1024 * 1024 => $"{size / 1024f:F1} kB",
            < 1024L * 1024 * 1024 => $"{size / (1024f * 1024f):F1} MB",
            < 1024L * 1024 * 1024 * 1024 => $"{size / (1024d * 1024 * 1024):F1} GB",
            _ => $"{size / (1024d * 1024 * 1024 * 1024):F1} TB"
        };
    }
}
