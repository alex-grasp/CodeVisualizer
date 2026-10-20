namespace DocsViewer.Core.Model;

/// <summary>Everything the viewer needs for one markdown document.</summary>
public sealed class DocGraph
{
    private readonly Dictionary<string, DocNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DocEdge> _edges = new();

    public DocGraph(string filePath, string rawMarkdown)
    {
        FilePath = filePath;
        RawMarkdown = rawMarkdown;
        Lines = rawMarkdown.Replace("\r\n", "\n").Split('\n');
        Title = System.IO.Path.GetFileNameWithoutExtension(filePath);
    }

    public string FilePath { get; }
    public string RawMarkdown { get; }

    /// <summary>Lines of the markdown, 0-based (SourceRef.Line is 1-based).</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>From the H1, or the file name.</summary>
    public string Title { get; set; }

    /// <summary>From the YAML front matter "summary:" key.</summary>
    public string? Summary { get; set; }

    /// <summary>From the YAML front matter "area:" key.</summary>
    public string? Area { get; set; }

    /// <summary>From the YAML front matter "parent:" key: the file name of the document this one is part of
    /// (e.g. "Home-Plumbing.md"). When absent, <see cref="DocLibrary"/> infers the parent from the file names.</summary>
    public string? ParentDocName { get; set; }

    /// <summary>Other markdown documents this one mentions ("see Home-Plumbing.md", "[`AIPanel-Plumbing.md`](...)"),
    /// by file name, in order of first appearance, without the document itself.</summary>
    public List<string> ReferencedDocs { get; } = new();

    public string FileName => System.IO.Path.GetFileName(FilePath);

    /// <summary>The node representing the document itself.</summary>
    public DocNode? Root { get; set; }

    /// <summary>Root cards for the Flow view, in document order: flow items nobody calls (e.g. "Browser GET /Login").</summary>
    public List<DocNode> EntryPoints { get; } = new();

    /// <summary>Root cards for the Components view: files/types from the "pieces" table plus implicit types, in document order.</summary>
    public List<DocNode> Components { get; } = new();

    /// <summary>Section nodes (## / ###) in document order.</summary>
    public List<DocNode> Sections { get; } = new();

    public IReadOnlyCollection<DocNode> Nodes => _nodes.Values;
    public IReadOnlyList<DocEdge> Edges => _edges;

    public DocNode? Find(string id) => _nodes.TryGetValue(id, out var n) ? n : null;

    /// <summary>Returns the existing node with this id or creates it. A Concept node is upgraded to a more specific kind.</summary>
    public DocNode GetOrAdd(string id, string title, NodeKind kind)
    {
        if (_nodes.TryGetValue(id, out var existing))
        {
            if (existing.Kind == NodeKind.Concept && kind != NodeKind.Concept) existing.Kind = kind;
            return existing;
        }
        var node = new DocNode(id, title, kind);
        _nodes[id] = node;
        return node;
    }

    public bool Remove(DocNode node)
    {
        if (!_nodes.Remove(node.Id)) return false;
        foreach (var e in node.Outgoing.ToList()) RemoveEdge(e);
        foreach (var e in node.Incoming.ToList()) RemoveEdge(e);
        EntryPoints.Remove(node);
        Components.Remove(node);
        Sections.Remove(node);
        return true;
    }

    /// <summary>Adds an edge unless the same (from, to, kind) already exists; self-edges are ignored.</summary>
    public DocEdge? Link(DocNode from, DocNode to, EdgeKind kind, string? label = null, SourceRef? source = null)
    {
        if (ReferenceEquals(from, to)) return null;
        var dup = from.Outgoing.FirstOrDefault(e => e.To == to && e.Kind == kind);
        if (dup != null) return dup;
        var edge = new DocEdge(from, to, kind, from.Outgoing.Count, label, source);
        from.Outgoing.Add(edge);
        to.Incoming.Add(edge);
        _edges.Add(edge);
        return edge;
    }

    public void RemoveEdge(DocEdge edge)
    {
        edge.From.Outgoing.Remove(edge);
        edge.To.Incoming.Remove(edge);
        _edges.Remove(edge);
    }

    /// <summary>Case-insensitive search over title, aliases, path, description and notes. Every term must match.</summary>
    public IEnumerable<DocNode> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Empty<DocNode>();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _nodes.Values
            .Where(n => n.Kind != NodeKind.Document)
            .Select(n => (node: n, score: Score(n, terms)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.node.Title)
            .Select(t => t.node);
    }

    private static int Score(DocNode n, string[] terms)
    {
        var score = 0;
        foreach (var t in terms)
        {
            var hit = 0;
            if (n.Title.Contains(t, StringComparison.OrdinalIgnoreCase))
                hit = Math.Max(hit, n.Title.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 10 : 6);
            if (n.Aliases.Any(a => a.Contains(t, StringComparison.OrdinalIgnoreCase))) hit = Math.Max(hit, 5);
            if (n.Path?.Contains(t, StringComparison.OrdinalIgnoreCase) == true) hit = Math.Max(hit, 4);
            if (n.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) == true) hit = Math.Max(hit, 2);
            if (n.Notes.Any(x => x.Contains(t, StringComparison.OrdinalIgnoreCase))) hit = Math.Max(hit, 1);
            if (hit == 0) return 0;
            score += hit;
        }
        return score;
    }
}
