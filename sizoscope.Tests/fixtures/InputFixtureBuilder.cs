using System.IO.Compression;

namespace Sizoscope.Tests.Fixtures;

internal static class InputFixtureBuilder
{
    public static string Plain(OwnedTempDirectory directory, string name, byte[] bytes)
    {
        string path = directory.FilePath(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Zip(OwnedTempDirectory directory, string name, params (string Name, byte[] Bytes)[] entries)
    {
        string path = directory.FilePath(name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, bytes) in entries)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        return path;
    }

    public static byte[] Bytes(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
