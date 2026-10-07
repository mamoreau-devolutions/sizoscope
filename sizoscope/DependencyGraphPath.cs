internal sealed class DependencyGraphPath(int id, DependencyGraphPath parent)
{
    public int Id { get; } = id;
    public DependencyGraphPath Parent { get; } = parent;
    public bool Contains(int id)
    {
        for (var current = this; current != null; current = current.Parent)
            if (current.Id == id)
                return true;
        return false;
    }
}
