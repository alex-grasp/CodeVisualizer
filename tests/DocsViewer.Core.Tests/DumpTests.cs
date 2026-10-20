using DocsViewer.Core;

namespace DocsViewer.Core.Tests;

/// <summary>Writes the text dump of the reference document next to the temp folder so the output can be inspected.</summary>
public sealed class DumpTests : IClassFixture<ReferenceDoc>
{
    private readonly ReferenceDoc _doc;

    public DumpTests(ReferenceDoc doc) => _doc = doc;

    [Fact]
    public void Writes_dump_of_reference_document()
    {
        var text = GraphDump.Render(_doc.Graph, maxDepth: 8);
        var path = Path.Combine(Path.GetTempPath(), "parser-dump.txt");
        File.WriteAllText(path, text);
        var extra = Environment.GetEnvironmentVariable("DOCSVIEWER_DUMP_PATH");
        if (!string.IsNullOrEmpty(extra))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
            File.WriteAllText(extra, text);
        }
        Assert.Contains("Browser GET /Login", text);
        Assert.True(File.Exists(path));
    }
}
