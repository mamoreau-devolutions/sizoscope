namespace Sizoscope.Tests.Fixtures;

internal static class StreamingGraphGenerator
{
    public static int Id(int ordinal) => checked(ordinal * 17);
    public static string Name(int ordinal) => "label-" + ordinal % 11;

    public static IEnumerable<GraphNode> Nodes(int count)
    {
        for (int ordinal = count - 1; ordinal >= 0; ordinal--)
            yield return new GraphNode(Id(ordinal), Name(ordinal));
    }

    public static IEnumerable<GraphLink> Links(int nodes, long edges, int seed)
    {
        for (long i = 0; i < edges; i++)
        {
            long group = i / 2; // Adjacent pairs intentionally repeat the entire edge.
            int source = (int)((group + seed) % nodes);
            int target = (int)((group / 4 + seed) % nodes); // Four sources share each target.
            string reason = (group % 3) switch { 0 => null, 1 => "", _ => "理由 Ω" };
            yield return new GraphLink(Id(source), Id(target), reason);
        }
    }

    // Invert the target's eight-edge blocks instead of building an expected graph/list.
    public static IEnumerable<(int Id, string Name, string Reason)> ExpectedIncoming(
        int targetOrdinal, int nodes, long edges, int seed)
    {
        long residue = (targetOrdinal - (long)seed % nodes + nodes) % nodes;
        for (long block = residue; block <= (edges - 1) / 8; block += nodes)
        {
            for (int slot = 0; slot < 8 && 8 * block + slot < edges; slot++)
            {
                long group = 4 * block + slot / 2;
                int source = (int)((group + seed) % nodes);
                yield return (checked(source * 17), "label-" + source % 11,
                    (group % 3) switch { 0 => null, 1 => "", _ => "理由 Ω" });
            }
        }
    }
}
