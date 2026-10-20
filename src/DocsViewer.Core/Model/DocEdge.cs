namespace DocsViewer.Core.Model;

public sealed class DocEdge
{
    public DocEdge(DocNode from, DocNode to, EdgeKind kind, int order, string? label = null, SourceRef? source = null)
    {
        From = from;
        To = to;
        Kind = kind;
        Order = order;
        Label = label;
        Source = source;
    }

    public DocNode From { get; }
    public DocNode To { get; }
    public EdgeKind Kind { get; }

    /// <summary>Position among the parent's edges, in document order.</summary>
    public int Order { get; }

    /// <summary>Optional short text shown on the child card, e.g. "when no password set", "302", "section 6a".</summary>
    public string? Label { get; }

    public SourceRef? Source { get; }

    public override string ToString() => $"{From.Title} -{Kind}-> {To.Title}";
}
