using System.Reflection.Metadata;

partial class MstatData
{
    private readonly DependencyGraphSession _graph;
    internal DependencyGraphSession Graph => _graph;
    public bool DgmlSupported => _version.Major >= 2;
    public bool DgmlAvailable => DgmlSupported && _graph.State == DependencyGraphState.Ready;

    private string GetNameForId(int id)
    {
        BlobReader reader = _peReader.GetSectionData(".names").GetReader();
        reader.Offset = id;
        return reader.ReadSerializedString();
    }

    public Node GetNodeForId(int id, out string name)
    {
        name = GetNameForId(id);
        var node = _graph.Store.FindNode(name);
        return node == null ? null : new Node(node.Id, node.Name);
    }

    public bool ContainsNamedNode(string name) => _graph.Store.FindNode(name) != null;

    public sealed class Node
    {
        public readonly int Id;
        public readonly string Name;
        public Node(int id, string name) => (Id, Name) = (id, name);
    }
}
