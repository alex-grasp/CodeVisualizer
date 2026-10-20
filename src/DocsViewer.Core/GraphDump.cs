using System.Text;
using DocsViewer.Core.Model;

namespace DocsViewer.Core;

/// <summary>Plain-text rendering of a graph, for debugging the parser and for the --dump command line switch.</summary>
public static class GraphDump
{
    public static string Render(DocGraph graph, int maxDepth = 6)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {graph.Title}  ({graph.FilePath})");
        if (graph.Summary != null) sb.AppendLine($"summary: {graph.Summary}");
        sb.AppendLine($"nodes: {graph.Nodes.Count}, edges: {graph.Edges.Count}, sections: {graph.Sections.Count}, entry points: {graph.EntryPoints.Count}, components: {graph.Components.Count}");
        sb.AppendLine();

        sb.AppendLine("## Entry points (Flow view)");
        foreach (var n in graph.EntryPoints)
        {
            RenderTree(sb, n, 0, maxDepth, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            sb.AppendLine();
        }

        sb.AppendLine("## Components (Components view)");
        foreach (var n in graph.Components)
        {
            RenderTree(sb, n, 0, Math.Min(maxDepth, 3), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        sb.AppendLine();

        sb.AppendLine("## Sections");
        foreach (var s in graph.Sections)
        {
            sb.AppendLine($"- {s.Title}: {string.Join(", ", s.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Contains).Select(e => e.To.Title))}");
        }
        sb.AppendLine();

        sb.AppendLine("## All nodes");
        foreach (var n in graph.Nodes.OrderBy(n => n.Kind).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"- [{n.Kind}] {n.Title}");
            if (n.Path != null) sb.Append($"  ({n.Path})");
            if (n.Owner != null) sb.Append($"  owner={n.Owner.Title}");
            sb.AppendLine($"  out={n.Outgoing.Count} in={n.Incoming.Count}");
        }
        return sb.ToString();
    }

    private static void RenderTree(StringBuilder sb, DocNode node, int depth, int maxDepth, HashSet<string> path)
    {
        sb.Append(new string(' ', depth * 2));
        sb.Append($"[{node.Kind}] {node.Title}");
        if (node.Path != null) sb.Append($"  ({node.Path})");
        if (!path.Add(node.Id))
        {
            sb.AppendLine("  (cycle)");
            return;
        }
        sb.AppendLine();
        if (depth < maxDepth)
        {
            foreach (var e in node.ChildrenInOrder())
            {
                if (e.Kind == EdgeKind.Mentions) continue;
                sb.Append(new string(' ', (depth + 1) * 2));
                sb.Append($"-{e.Kind}{(e.Label != null ? $" ({e.Label})" : "")}-> ");
                var inner = new StringBuilder();
                RenderTree(inner, e.To, 0, maxDepth - depth - 1, path);
                var lines = inner.ToString().TrimEnd('\r', '\n').Split('\n');
                sb.AppendLine(lines[0].TrimEnd('\r'));
                foreach (var l in lines.Skip(1))
                    sb.AppendLine(new string(' ', (depth + 1) * 2) + l.TrimEnd('\r'));
            }
        }
        path.Remove(node.Id);
    }
}
