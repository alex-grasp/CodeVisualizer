using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;

namespace DocsViewer.Core;

/// <summary>A folder of markdown documents, each parsed into a <see cref="DocGraph"/>, with the parent/child
/// hierarchy between them (Home-Plumbing.md is the parent of Home-Export-Plumbing.md).</summary>
public sealed class DocLibrary
{
    private readonly IDocParser _parser;

    public DocLibrary(IDocParser? parser = null)
    {
        _parser = parser ?? new MarkdownDocParser();
    }

    public string? FolderPath { get; private set; }

    public List<DocGraph> Documents { get; } = new();

    /// <summary>Errors from files that could not be read or parsed, keyed by path.</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads every *.md under <paramref name="folderPath"/> (recursively), replacing the current contents.</summary>
    public void LoadFolder(string folderPath)
    {
        FolderPath = folderPath;
        Documents.Clear();
        Errors.Clear();
        if (!Directory.Exists(folderPath)) return;

        var files = Directory.EnumerateFiles(folderPath, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var graph = LoadFile(file);
            if (graph != null) Documents.Add(graph);
        }
    }

    /// <summary>Parses (or re-parses) one file. Returns null and records the error when it cannot be read.</summary>
    public DocGraph? LoadFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var graph = _parser.Parse(text, filePath);
            var index = Documents.FindIndex(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) Documents[index] = graph;
            Errors.Remove(filePath);
            return graph;
        }
        catch (Exception ex)
        {
            Errors[filePath] = ex.Message;
            return null;
        }
    }

    /// <summary>Searches every loaded document.</summary>
    public IEnumerable<(DocGraph Doc, DocNode Node)> SearchAll(string text)
        => Documents.SelectMany(d => d.Search(text).Select(n => (d, n)));

    // ------------------------------------------------------------------ hierarchy

    /// <summary>Finds a document by file name ("Home-Plumbing.md" or "Home-Plumbing"), case-insensitively.</summary>
    public DocGraph? FindDocument(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        var bare = Path.GetFileName(name);
        return Documents.FirstOrDefault(d => string.Equals(d.FileName, bare, StringComparison.OrdinalIgnoreCase))
            ?? Documents.FirstOrDefault(d => string.Equals(Path.GetFileNameWithoutExtension(d.FilePath), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The document <paramref name="doc"/> is part of: its front matter "parent:" when that names a loaded
    /// document, else the file-name rule (Home-Export-Plumbing.md → Home-Plumbing.md). Null for a top-level document.</summary>
    public DocGraph? ParentOf(DocGraph doc)
    {
        DocGraph? parent = null;
        if (doc.ParentDocName != null) parent = FindDocument(doc.ParentDocName);
        parent ??= FindDocument(InferParentName(doc.FileName));
        return parent == null || ReferenceEquals(parent, doc) ? null : parent;
    }

    public IReadOnlyList<DocGraph> ChildrenOf(DocGraph doc)
        => Documents.Where(d => ReferenceEquals(ParentOf(d), doc)).OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// "Home-AIQuery-Plumbing.md" → "Home-Plumbing.md": keep the last dash segment as the kind suffix and drop the
    /// innermost middle segment. Nested names walk outwards ("A-B-C-Plumbing.md" → "A-B-Plumbing.md" → "A-Plumbing.md").
    /// Two-segment and single-segment names have no inferred parent.
    /// </summary>
    public static string? InferParentName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        var suffix = parts[^1];
        var basePart = string.Join('-', parts.Take(parts.Length - 2));
        return $"{basePart}-{suffix}{ext}";
    }

    /// <summary>Every document in sidebar order: top-level documents by file name, each followed by its children
    /// (recursively), with the nesting depth. A document whose parent chain loops is shown at the top level.</summary>
    public IReadOnlyList<(DocGraph Doc, int Depth)> OrderedWithDepth()
    {
        var result = new List<(DocGraph, int)>();
        var placed = new HashSet<DocGraph>();
        var roots = Documents.Where(d => ParentOf(d) == null).OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in roots) Add(root, 0);
        // Cycles (a: parent b, b: parent a) never reach a root; list them flat so nothing disappears.
        foreach (var d in Documents.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase))
            if (placed.Add(d)) result.Add((d, 0));
        return result;

        void Add(DocGraph doc, int depth)
        {
            if (!placed.Add(doc)) return;
            result.Add((doc, depth));
            foreach (var child in ChildrenOf(doc)) Add(child, depth + 1);
        }
    }
}
