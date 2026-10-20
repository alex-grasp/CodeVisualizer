namespace DocsViewer.Core.Model;

/// <summary>
/// One card. Nodes are deduplicated by <see cref="Id"/> (a canonical, lower-case key), so the same
/// method mentioned in several places (ISignInUseCase.ExecuteAsync, SignInUseCase.ExecuteAsync, ExecuteAsync(command)
/// under the "Inside SignInUseCase" section) is a single node that merges all of its children.
/// </summary>
public sealed class DocNode
{
    public DocNode(string id, string title, NodeKind kind)
    {
        Id = id;
        Title = title;
        Kind = kind;
    }

    /// <summary>Canonical key. Examples: "method:signincontroller.signinasync", "endpoint:get /api/sign-in",
    /// "type:loginpage", "route:/login", "section:1. loading the page".</summary>
    public string Id { get; }

    /// <summary>Display name as it best appears in the doc, e.g. "SignInController.SignInAsync" or "Browser GET /Login (or /)".</summary>
    public string Title { get; set; }

    /// <summary>Secondary line on the card: file path, signature, or owning section.</summary>
    public string? Subtitle { get; set; }

    public NodeKind Kind { get; set; }

    /// <summary>Source file path when known (from the "pieces" table or a path in the text).</summary>
    public string? Path { get; set; }

    /// <summary>One-paragraph description (the "What it does" cell or the first explanatory bullet).</summary>
    public string? Description { get; set; }

    /// <summary>Extra lines: bullets, annotations ([ValidateAntiForgeryToken]), returns, headers, conditions.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Other spellings that resolved to this node (ISignInUseCase.ExecuteAsync, ExecuteAsync(command), ...).</summary>
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every place in the markdown this node appears.</summary>
    public List<SourceRef> Sources { get; } = new();

    /// <summary>For members (methods/functions): the owning type/file node.</summary>
    public DocNode? Owner { get; set; }

    /// <summary>The section this node was first defined in.</summary>
    public DocNode? Section { get; set; }

    public List<DocEdge> Outgoing { get; } = new();
    public List<DocEdge> Incoming { get; } = new();

    public bool HasChildren => Outgoing.Any(e => e.Kind != EdgeKind.Mentions);

    /// <summary>Outgoing edges in display order: by <see cref="EdgeKind"/> priority, then document order, without duplicates.</summary>
    public IReadOnlyList<DocEdge> ChildrenInOrder()
        => Outgoing
            .GroupBy(e => (e.To.Id, e.Kind))
            .Select(g => g.OrderBy(e => e.Order).First())
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Order)
            .ToList();

    public override string ToString() => $"{Kind} {Title} [{Id}]";
}
