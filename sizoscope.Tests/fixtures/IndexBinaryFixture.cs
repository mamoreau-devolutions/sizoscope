using System.Text;

namespace Sizoscope.Tests.Fixtures;

internal static class IndexBinaryFixture
{
    // Inspected completed format: four little-endian int64s (Key, Value, Data, Sequence).
    // Strings are little-endian int32 UTF-8 byte length followed by exactly those bytes.
    public const int RecordSize = 32;
    public const int ValueField = 8;
    public const int DataField = 16;

    public static long ReadField(string path, long record, int field)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = checked(record * RecordSize + field);
        return reader.ReadInt64();
    }

    public static void PatchField(string path, long record, int field, long value)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Open, FileAccess.Write));
        writer.BaseStream.Position = checked(record * RecordSize + field);
        writer.Write(value);
    }

    public static long WriteString(FileStream stream, long offset, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        stream.Position = offset;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        writer.Flush();
        return checked(offset + sizeof(int) + bytes.Length);
    }

    public static void PatchInt32(string path, long offset, int value)
    {
        using var writer = new BinaryWriter(File.Open(path, FileMode.Open, FileAccess.Write));
        writer.BaseStream.Position = offset;
        writer.Write(value);
    }
}
