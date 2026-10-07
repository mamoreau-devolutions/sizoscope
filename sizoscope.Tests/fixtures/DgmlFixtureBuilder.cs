using System.Text;
using System.Xml;

namespace Sizoscope.Tests.Fixtures;

internal sealed record GraphNode(int Id, string Name);
internal sealed record GraphLink(int Source, int Target, string Reason = null);

internal static class DgmlFixtureBuilder
{
    public static MemoryStream Open(string xml) => new(Encoding.UTF8.GetBytes(xml));

    public static byte[] Bytes(IEnumerable<GraphNode> nodes, IEnumerable<GraphLink> links,
        string xmlNamespace = "", bool paired = false, bool linksFirst = false)
    {
        using var stream = new MemoryStream();
        Write(stream, nodes, links, xmlNamespace, paired, linksFirst);
        return stream.ToArray();
    }

    // Enumerables are consumed once: the opt-in runner can generate without a graph DOM/list.
    public static void Write(Stream stream, IEnumerable<GraphNode> nodes, IEnumerable<GraphLink> links,
        string xmlNamespace = "", bool paired = false, bool linksFirst = false)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false
        });
        writer.WriteStartElement("DirectedGraph", xmlNamespace);
        if (linksFirst) { WriteLinks(); WriteNodes(); }
        else { WriteNodes(); WriteLinks(); }
        writer.WriteEndElement();

        void WriteNodes()
        {
            writer.WriteStartElement("Nodes", xmlNamespace);
            foreach (var node in nodes)
            {
                writer.WriteStartElement("Node", xmlNamespace);
                writer.WriteAttributeString("Id", node.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("Label", node.Name);
                if (paired) writer.WriteFullEndElement();
                else writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        void WriteLinks()
        {
            writer.WriteStartElement("Links", xmlNamespace);
            foreach (var link in links)
            {
                writer.WriteStartElement("Link", xmlNamespace);
                writer.WriteAttributeString("Source", link.Source.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteAttributeString("Target", link.Target.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (link.Reason != null) writer.WriteAttributeString("Reason", link.Reason);
                if (paired) writer.WriteFullEndElement();
                else writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
    }
}
