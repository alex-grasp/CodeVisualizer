namespace DocsViewer.Core.Model;

/// <summary>Where in the markdown a node or edge came from. Line is 1-based.</summary>
public sealed record SourceRef(string SectionTitle, int Line, string Text);
