using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;
using static DocsViewer.Core.Tests.GraphAssert;

namespace DocsViewer.Core.Tests;

/// <summary>SPEC.md 2.11: the parser never throws and odd input still yields a usable graph.</summary>
public sealed class RobustnessTests
{
    private static readonly MarkdownDocParser Parser = new();

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("# Only a title")]
    [InlineData("---\nsummary: x\n---\n")]
    [InlineData("## a\n## b\n### c\n")]
    [InlineData("just a paragraph with `code` and `GET /x` and `/y` and `Foo.Bar()`")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |\n")]
    [InlineData("```text\n```\n")]
    [InlineData("```text\n├─ orphan connector\n   └─ deeper\n```\n")]
    [InlineData("- a\n  - b\n    - c\n      - `Deep.Call()`\n- `X.y()`:\n  1. `z()`\n")]
    [InlineData("## s\n`Thing.Do()`:\n\n| Call | Server | Returns |\n|---|---|---|\n| `GET /a` | | |\n")]
    [InlineData("## s\n\n| Piece | Where | What it does |\n|---|---|---|\n| only | | |\n| | `A.cs` | |\n")]
    [InlineData("## s\n```text\nA → B → C\n   → D\nE\n```\n")]
    [InlineData("## s\n```text\n→ starts with arrow\n```\n")]
    [InlineData("## s\n```text\n└─ `X`   (section 99)\n```\n")]
    public void Never_throws(string markdown)
    {
        var graph = Parser.Parse(markdown, "odd.md");
        Assert.NotNull(graph.Root);
        Assert.Equal("doc:odd.md", graph.Root!.Id);
        Assert.All(graph.Edges, e => Assert.NotSame(e.From, e.To));
    }

    [Fact]
    public void Null_inputs_are_tolerated()
    {
        var graph = Parser.Parse(null!, null!);
        Assert.NotNull(graph.Root);
    }

    [Fact]
    public void No_front_matter_uses_the_H1_and_has_no_summary()
    {
        var graph = Parser.Parse("# Title here\n\n## One\n\ntext\n", "a.md");
        Assert.Equal("Title here", graph.Title);
        Assert.Null(graph.Summary);
        Assert.Single(graph.Sections);
        Assert.Equal("One", graph.Sections[0].Title);
    }

    [Fact]
    public void Headings_only_yield_document_and_sections()
    {
        var graph = Parser.Parse("# T\n## A\n## B\n### B1\n### B2\n## C\n", "h.md");
        Assert.Equal(new[] { "A", "B", "B1", "B2", "C" }, graph.Sections.Select(s => s.Title));
        var b = Node(graph, "section:b");
        Assert.Equal(new[] { "B1", "B2" }, b.Outgoing.Where(e => e.Kind == EdgeKind.Contains).Select(e => e.To.Title));
        Assert.Equal(5, graph.Root!.Outgoing.Count);
        Assert.Empty(graph.EntryPoints);
        Assert.Empty(graph.Components);
    }

    [Fact]
    public void Trees_with_tabs_and_crlf_still_parse()
    {
        var md = "## S\r\n```text\r\nRoot.Go\r\n\t├─ A.One\r\n\t│\t└─ B.Two → Done\r\n\t└─ C.Three\r\n```\r\n";
        var graph = Parser.Parse(md, "t.md");
        var root = Node(graph, "method:root.go");
        HasChild(root, "A.One", EdgeKind.Calls);
        HasChild(root, "C.Three", EdgeKind.Calls);
        var two = Edge(Node(graph, "method:a.one"), "B.Two").To;
        HasChild(two, "Done", EdgeKind.Returns);
        Assert.Equal(NodeKind.Outcome, Node(graph, "outcome:done").Kind);
        Assert.Equal(new[] { root }, graph.EntryPoints);
    }

    [Fact]
    public void Line_numbers_are_one_based_and_point_at_the_right_text()
    {
        var md = "---\nsummary: s\n---\n\n# T\n\n## Sec\n\n```text\nRoot.Go\n  └─ A.One\n```\n\n`X.y()`:\n\n1. calls `z()`\n";
        var graph = Parser.Parse(md, "l.md");
        Assert.Equal(7, Node(graph, "section:sec").Sources[0].Line);
        Assert.Equal(10, Node(graph, "method:root.go").Sources[0].Line);
        Assert.Equal(11, Node(graph, "method:a.one").Sources[0].Line);
        Assert.Equal(14, Node(graph, "method:x.y").Sources[0].Line);
        Assert.Equal(16, Node(graph, "method:z").Sources[0].Line);
        foreach (var n in graph.Nodes)
            foreach (var s in n.Sources)
                Assert.True(s.Line >= 1 && s.Line <= graph.Lines.Count, $"{n.Title}: line {s.Line} out of range");
    }

    [Fact]
    public void Bullets_with_weird_nesting_become_notes_and_mentions()
    {
        var md = "## S\n\n```text\nMain.Run\n```\n\n- first note\n  - nested `Main.Run` mention\n    - deeper `#id` and `[Attr]` noise\n- `Main.Run` describes the root\n";
        var graph = Parser.Parse(md, "b.md");
        var run = Node(graph, "method:main.run");
        Assert.Equal("Main.Run describes the root", run.Description);
        Assert.Contains(run.Notes, n => n.Contains("first note"));
        Assert.DoesNotContain(graph.Nodes, n => n.Title.Contains("#id") || n.Title.Contains("[Attr]"));
    }

    [Fact]
    public void Multi_root_tree_chains_roots_with_Then()
    {
        var md = "## S\n```text\nFirst            note one\nSecond           note two\nThirdMiddleware\n  └─ check → 302 /Login\n```\n";
        var graph = Parser.Parse(md, "m.md");
        var first = Node(graph, "First");
        var second = Edge(first, "Second");
        Assert.Equal(EdgeKind.Then, second.Kind);
        var third = Edge(second.To, "ThirdMiddleware");
        Assert.Equal(EdgeKind.Then, third.Kind);
        Assert.Equal(NodeKind.Middleware, third.To.Kind);
        HasChild(third.To, "route:/login", EdgeKind.Redirects);
        Assert.Equal(new[] { first }, graph.EntryPoints);
        Assert.Contains("note one", first.Notes);
    }

    [Fact]
    public void Pipeline_style_lines_become_a_chain()
    {
        var md = "## S\n```text\n/start?x=1      Ext.Begin → IFooUseCase → 302 to the provider\n```\n";
        var graph = Parser.Parse(md, "p.md");
        var route = Node(graph, "route:/start");
        Assert.Equal("/start", route.Title);
        var begin = Edge(route, "Ext.Begin");
        Assert.Equal(EdgeKind.Calls, begin.Kind);
        var useCase = Edge(begin.To, "IFooUseCase");
        Assert.Equal(EdgeKind.Uses, useCase.Kind);
        Assert.Equal(NodeKind.UseCase, useCase.To.Kind);
        Assert.Contains(useCase.To.Notes, n => n.Contains("302 to the provider"));
    }

    [Fact]
    public void Section_label_links_to_that_sections_first_flow_item()
    {
        var md = "## 1. A\n```text\nA.Run\n  └─ when x → B.Go   (section 2)\n```\n\n## 2. B\n```text\nOther.Start\n```\n";
        var graph = Parser.Parse(md, "s.md");
        var go = Node(graph, "method:b.go");
        var edge = Edge(go, "Other.Start");
        Assert.Equal(EdgeKind.Calls, edge.Kind);
        Assert.Equal("section 2", edge.Label);
        Assert.Equal("when x", Edge(Node(graph, "method:a.run"), "B.Go").Label);
        Assert.DoesNotContain(Node(graph, "method:other.start"), graph.EntryPoints);
    }

    [Fact]
    public void Owner_less_function_is_rehomed_when_its_owner_appears()
    {
        var md = "## S\n\n| Piece | Where | What it does |\n|---|---|---|\n| Page | `src/Page.vue` | calls `go` |\n| Api | `src/api.ts` | `go()` and `other()` |\n";
        var graph = Parser.Parse(md, "r.md");
        var go = Node(graph, "method:src/api.go");
        Assert.Equal("api.ts", go.Owner?.Title);
        Assert.Null(graph.Find("method:go"));
        HasChild(Node(graph, "type:page"), "go()", EdgeKind.Calls);
        HasChild(Node(graph, "type:src/api"), "other()", EdgeKind.Contains);
        Assert.Equal(2, graph.Components.Count);
    }
}
