using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;

namespace DocsViewer.Core.Tests;

/// <summary>Parses docs/Login-Plumbing.md once for the whole test class (xunit class fixture).</summary>
public sealed class ReferenceDoc
{
    public ReferenceDoc()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Login-Plumbing.md");
        Markdown = File.ReadAllText(Path);
        Graph = new MarkdownDocParser().Parse(Markdown, Path);
    }

    public string Path { get; }
    public string Markdown { get; }
    public DocGraph Graph { get; }
}

/// <summary>Helpers that resolve nodes by canonical id or by a readable spelling and walk non-Mentions edges.</summary>
public static class GraphAssert
{
    /// <summary>Finds a node by id, or by title / alias ignoring case, argument lists and an interface "I" prefix.</summary>
    public static DocNode Node(DocGraph g, string idOrTitle)
    {
        var byId = g.Find(idOrTitle);
        if (byId != null) return byId;
        var wanted = Normalise(idOrTitle);
        var matches = g.Nodes.Where(n => Normalise(n.Title) == wanted || n.Aliases.Any(a => Normalise(a) == wanted)).ToList();
        if (matches.Count == 0)
        {
            // Allow "Owner.Method" to match a node titled "Method()" owned by Owner, and vice versa.
            matches = g.Nodes.Where(n => n.Kind == NodeKind.Method && n.Owner != null &&
                                         Normalise($"{n.Owner.Title}.{MethodName(n.Title)}") == wanted).ToList();
        }
        Assert.True(matches.Count > 0, $"No node resolves to '{idOrTitle}'. Candidates: {Candidates(g, idOrTitle)}");
        if (matches.Count > 1)
        {
            // Prefer an exact title match.
            var exact = matches.Where(n => string.Equals(n.Title, idOrTitle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
        }
        Assert.True(matches.Count == 1, $"'{idOrTitle}' is ambiguous: {string.Join(", ", matches.Select(m => m.Id))}");
        return matches[0];
    }

    public static DocNode? TryNode(DocGraph g, string idOrTitle)
    {
        var byId = g.Find(idOrTitle);
        if (byId != null) return byId;
        var wanted = Normalise(idOrTitle);
        return g.Nodes.FirstOrDefault(n => Normalise(n.Title) == wanted || n.Aliases.Any(a => Normalise(a) == wanted));
    }

    /// <summary>Children reachable over one edge whose kind is not Mentions.</summary>
    public static IReadOnlyList<DocNode> Children(DocNode node)
        => node.ChildrenInOrder().Where(e => e.Kind != EdgeKind.Mentions).Select(e => e.To).ToList();

    public static DocEdge Edge(DocNode from, string childIdOrTitle)
    {
        var wanted = Normalise(childIdOrTitle);
        var edge = from.Outgoing.FirstOrDefault(e => e.Kind != EdgeKind.Mentions &&
            (e.To.Id.Equals(childIdOrTitle, StringComparison.OrdinalIgnoreCase) || Normalise(e.To.Title) == wanted || e.To.Aliases.Any(a => Normalise(a) == wanted)));
        // "submit()" / "SignInAsync" as the simple name of a member of the parent.
        edge ??= from.Outgoing.FirstOrDefault(e => e.Kind != EdgeKind.Mentions && e.To.Kind == NodeKind.Method &&
            Normalise(MethodName(e.To.Title)) == wanted);
        Assert.True(edge != null, $"'{from.Title}' has no non-Mentions child '{childIdOrTitle}'. Children: {Describe(from)}");
        return edge!;
    }

    /// <summary>Follows a chain of titles along non-Mentions edges and returns the last node.</summary>
    public static DocNode Path(DocGraph g, params string[] titles)
    {
        var node = Node(g, titles[0]);
        for (var i = 1; i < titles.Length; i++) node = Edge(node, titles[i]).To;
        return node;
    }

    public static void HasChild(DocNode from, string childIdOrTitle, EdgeKind? kind = null)
    {
        var edge = Edge(from, childIdOrTitle);
        if (kind != null) Assert.True(edge.Kind == kind, $"'{from.Title}' → '{childIdOrTitle}' is {edge.Kind}, expected {kind}. Children: {Describe(from)}");
    }

    public static void HasNoChild(DocNode from, string childIdOrTitle)
    {
        var wanted = Normalise(childIdOrTitle);
        var hit = from.Outgoing.FirstOrDefault(e => e.Kind != EdgeKind.Mentions && (Normalise(e.To.Title) == wanted || e.To.Aliases.Any(a => Normalise(a) == wanted)));
        Assert.True(hit == null, $"'{from.Title}' unexpectedly has child '{childIdOrTitle}'");
    }

    public static string Describe(DocNode node)
        => string.Join(" | ", node.ChildrenInOrder().Select(e => $"-{e.Kind}{(e.Label != null ? $"({e.Label})" : "")}-> {e.To.Title} [{e.To.Id}]"));

    private static string Candidates(DocGraph g, string text)
    {
        var key = MethodName(text).ToLowerInvariant();
        var c = g.Nodes.Where(n => n.Id.Contains(key, StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).Take(8);
        return string.Join(", ", c);
    }

    private static string MethodName(string title)
    {
        var t = StripArgs(title);
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    private static string StripArgs(string s)
    {
        var open = s.IndexOf('(');
        if (open > 0 && s.TrimEnd().EndsWith(')')) return s[..open].Trim();
        return s.Trim();
    }

    /// <summary>Lower-case, no argument list, no leading interface "I" on the owner, no trailing "()".</summary>
    public static string Normalise(string s)
    {
        var t = StripArgs(s);
        var dot = t.IndexOf('.');
        if (dot > 0)
        {
            var owner = t[..dot];
            if (owner.Length >= 2 && owner[0] == 'I' && char.IsUpper(owner[1])) owner = owner[1..];
            t = owner + t[dot..];
        }
        else if (t.Length >= 2 && t[0] == 'I' && char.IsUpper(t[1]) && !t.Contains(' ') && !t.Contains('/'))
        {
            t = t[1..];
        }
        return t.ToLowerInvariant();
    }
}
