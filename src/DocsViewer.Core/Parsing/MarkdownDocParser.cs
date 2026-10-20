using DocsViewer.Core.Model;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace DocsViewer.Core.Parsing;

/// <summary>
/// Turns an AI-generated "plumbing" document (front matter, a pieces table, numbered sections with
/// box-drawing call trees in ```text blocks, call tables, and "`X.method()`:" step lists) into a <see cref="DocGraph"/>.
/// See SPEC.md at the repository root for the conventions this parser understands.
/// </summary>
public sealed class MarkdownDocParser : IDocParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseYamlFrontMatter()
        .Build();

    public DocGraph Parse(string markdown, string filePath)
    {
        markdown ??= string.Empty;
        filePath ??= "document.md";
        var graph = new DocGraph(filePath, markdown);
        var fileName = System.IO.Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName)) fileName = "document.md";
        var root = graph.GetOrAdd("doc:" + fileName.ToLowerInvariant(), graph.Title, NodeKind.Document);
        graph.Root = root;
        try
        {
            var doc = Markdown.Parse(markdown, Pipeline);
            new Walker(graph, root).Walk(doc);
        }
        catch (Exception ex)
        {
            root.Notes.Add("Parser error: " + ex.Message);
        }
        CollectReferencedDocs(graph, markdown);
        return graph;
    }

    private static readonly System.Text.RegularExpressions.Regex DocNameRx =
        new(@"(?<![\w/.-])([A-Za-z0-9_][\w.-]*\.md)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Every "Some-Doc.md" mentioned anywhere in the text, except this document's own name.</summary>
    private static void CollectReferencedDocs(DocGraph graph, string markdown)
    {
        var own = graph.FileName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in DocNameRx.Matches(markdown))
        {
            var name = m.Groups[1].Value;
            if (string.Equals(name, own, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(name)) graph.ReferencedDocs.Add(name);
        }
        if (graph.ParentDocName != null && seen.Add(graph.ParentDocName)) graph.ReferencedDocs.Insert(0, graph.ParentDocName);
    }

    /// <summary>Walks the top-level blocks in order, keeping the current section.</summary>
    private sealed class Walker
    {
        private readonly DocGraph _graph;
        private readonly DocNode _root;
        private readonly GraphBuilder _b;
        private readonly TreeParser _trees;
        private readonly TableParser _tables;
        private readonly ProseParser _prose;

        private DocNode? _lastH2;
        private DocNode? _ownerContext;
        private string? _pendingThenLabel;
        private bool _sawTitle;

        public Walker(DocGraph graph, DocNode root)
        {
            _graph = graph;
            _root = root;
            _b = new GraphBuilder(graph, root);
            _trees = new TreeParser(_b);
            _tables = new TableParser(_b);
            _prose = new ProseParser(_b, _trees, _tables);
        }

        public void Walk(MarkdownDocument doc)
        {
            var blocks = doc.ToList();
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
                try
                {
                    if (Visit(block, next)) i++;
                }
                catch (Exception ex)
                {
                    _root.Notes.Add($"Parser error at line {block.Line + 1}: {ex.Message}");
                }
            }
            _b.Finish();
        }

        /// <summary>Visits one block; returns true when the following block was consumed as well.</summary>
        private bool Visit(Block block, Block? next)
        {
            switch (block)
            {
                case YamlFrontMatterBlock yaml:
                    FrontMatter(yaml);
                    return false;
                case HeadingBlock heading:
                    Heading(heading);
                    return false;
                case FencedCodeBlock fence:
                    Fence(fence);
                    return false;
                case CodeBlock code:
                    foreach (var l in code.Lines.Lines.Take(code.Lines.Count)) _b.Note(_b.CurrentFlowItem ?? _b.CurrentSection, l.Slice.ToString());
                    return false;
                case Table table:
                    _tables.Emit(table, null);
                    return false;
                case ParagraphBlock para:
                    return Paragraph(para, next);
                case ListBlock list:
                    _prose.Bullets(list);
                    return false;
                case QuoteBlock quote:
                    foreach (var inner in quote.OfType<ParagraphBlock>()) _prose.Prose(inner);
                    return false;
                default:
                    return false;
            }
        }

        private void FrontMatter(YamlFrontMatterBlock yaml)
        {
            for (var i = 0; i < yaml.Lines.Count; i++)
            {
                var text = yaml.Lines.Lines[i].Slice.ToString();
                var colon = text.IndexOf(':');
                if (colon <= 0) continue;
                var key = text[..colon].Trim().ToLowerInvariant();
                var value = text[(colon + 1)..].Trim().Trim('"', '\'');
                if (key == "summary") _graph.Summary = value;
                else if (key == "area") _graph.Area = value;
                else if (key == "parent") _graph.ParentDocName = value.Length == 0 ? null : value;
                else if (key == "title" && !_sawTitle) { _graph.Title = value; _root.Title = value; }
                else _root.Notes.Add($"{key}: {value}");
            }
        }

        private void Heading(HeadingBlock heading)
        {
            var title = InlineText.Plain(heading);
            var line = heading.Line + 1;
            if (heading.Level == 1)
            {
                if (!_sawTitle && title.Length > 0)
                {
                    _graph.Title = title;
                    _root.Title = title;
                    _sawTitle = true;
                }
                _root.Sources.Add(new SourceRef(title, line, title));
                return;
            }
            if (heading.Level > 3 || title.Length == 0)
            {
                if (title.Length > 0) _b.Note(_b.CurrentFlowItem ?? _b.CurrentSection, title);
                return;
            }

            var id = "section:" + title.ToLowerInvariant();
            var section = _graph.Find(id);
            if (section == null)
            {
                section = _graph.GetOrAdd(id, title, NodeKind.Section);
                _graph.Sections.Add(section);
            }
            section.Sources.Add(new SourceRef(title, line, title));
            section.Subtitle ??= heading.Level == 2 ? "Section" : "Subsection";
            _graph.Link(_root, section, EdgeKind.Contains, null, new SourceRef(title, line, title));
            if (heading.Level == 2) _lastH2 = section;
            else if (_lastH2 != null) _graph.Link(_lastH2, section, EdgeKind.Contains, null, new SourceRef(title, line, title));

            _b.EnterSection(section);
            _pendingThenLabel = null;
            _ownerContext = null;

            // The back-ticked subject of the heading: owner context (and the method whose tree follows).
            foreach (var span in InlineText.CodeSpans(heading))
            {
                var r = Identifiers.Parse(span.Code);
                if (r == null) continue;
                if (r.Shape == RefShape.QualifiedMethod && r.OwnerIsType)
                {
                    var method = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
                    _ownerContext = method?.Owner;
                    if (method != null) section.Description ??= method.Title;
                    break;
                }
                if (r.Shape == RefShape.Word && Identifiers.IsTypeLike(r.Name))
                {
                    _ownerContext = _b.ResolveType(r.Name, true, line);
                    break;
                }
                if (r.Shape == RefShape.FilePath)
                {
                    _ownerContext = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
                    break;
                }
            }
        }

        private void Fence(FencedCodeBlock fence)
        {
            if (!TreeParser.LooksLikeTree(fence))
            {
                var target = _b.CurrentFlowItem ?? _b.CurrentSection;
                foreach (var (_, text) in TreeParser.FenceLines(fence)) _b.Note(target, text);
                _pendingThenLabel = null;
                return;
            }
            var previous = _b.CurrentFlowItem;
            var roots = _trees.Emit(fence, _ownerContext);
            var first = true;
            foreach (var rootNode in roots)
            {
                if (first && _pendingThenLabel != null && previous != null && previous != rootNode)
                {
                    _b.Link(previous, rootNode, EdgeKind.Then, _pendingThenLabel, fence.Line + 1);
                    _b.RegisterFlowItem(rootNode, fence.Line + 1, chain: false);
                }
                else
                {
                    _b.RegisterFlowItem(rootNode, fence.Line + 1);
                }
                first = false;
            }
            _pendingThenLabel = null;
        }

        private bool Paragraph(ParagraphBlock para, Block? next)
        {
            var text = InlineText.Plain(para);
            if (text.EndsWith(':'))
            {
                if (next is ListBlock or Table && _prose.TryStepList(para, next, _ownerContext)) return true;
                if (next is FencedCodeBlock fence && TreeParser.LooksLikeTree(fence))
                {
                    var intro = text.TrimEnd(':').Trim();
                    _pendingThenLabel = intro.Equals("On the server", StringComparison.OrdinalIgnoreCase) ? "handled by" : intro;
                    // A subject in the intro ("`X` does this:") still gets described.
                    _prose.Prose(para);
                    return false;
                }
            }
            _prose.Prose(para);
            return false;
        }
    }
}
