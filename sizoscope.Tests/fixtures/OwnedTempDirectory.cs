namespace Sizoscope.Tests.Fixtures;

internal sealed class OwnedTempDirectory : IDisposable
{
    private readonly string _root;
    private readonly string _marker = Guid.NewGuid().ToString("N");
    public string Path { get; }

    public OwnedTempDirectory(string absoluteRoot)
    {
        if (!System.IO.Path.IsPathFullyQualified(absoluteRoot))
            throw new ArgumentException("An absolute temporary root is required.", nameof(absoluteRoot));
        _root = System.IO.Path.GetFullPath(absoluteRoot);
        Path = System.IO.Path.Combine(_root, "sizoscope-tests-" + _marker);
        if (Directory.Exists(Path))
            throw new IOException("The exclusive temporary directory already exists.");
        Directory.CreateDirectory(Path);
        File.WriteAllText(System.IO.Path.Combine(Path, ".test-owner"), _marker);
    }

    public string FilePath(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;
        if (System.IO.Path.GetDirectoryName(Path) != _root.TrimEnd(System.IO.Path.DirectorySeparatorChar)
            || File.ReadAllText(FilePath(".test-owner")) != _marker
            || (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to clean a directory not owned by this fixture.");
        Directory.Delete(Path, recursive: true);
    }
}
