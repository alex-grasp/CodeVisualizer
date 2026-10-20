using DocsViewer.Core.Model;

namespace DocsViewer.Core.Parsing;

public interface IDocParser
{
    /// <summary>Builds the navigable graph for one markdown document. Never throws on odd input; an empty or
    /// unstructured document yields a graph with just the Document root and any sections.</summary>
    DocGraph Parse(string markdown, string filePath);
}
