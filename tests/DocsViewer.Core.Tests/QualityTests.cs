using DocsViewer.Core.Model;
using static DocsViewer.Core.Tests.GraphAssert;

namespace DocsViewer.Core.Tests;

/// <summary>Graph hygiene on the reference document: no noise nodes, no self loops, readable titles, owners and sources.</summary>
public sealed class QualityTests : IClassFixture<ReferenceDoc>
{
    private readonly ReferenceDoc _doc;
    private DocGraph G => _doc.Graph;

    public QualityTests(ReferenceDoc doc) => _doc = doc;

    [Theory]
    [InlineData("#login-app")]
    [InlineData("[Authorize]")]
    [InlineData("[Required]")]
    [InlineData("[ValidateAntiForgeryToken]")]
    [InlineData("nextUrl")]
    [InlineData("document.title")]
    [InlineData("location.assign")]
    [InlineData("applicationTitle")]
    [InlineData("errorMessage")]
    [InlineData("humanVerificationRequired")]
    [InlineData("<noscript>")]
    [InlineData("grasp\\")]
    [InlineData("audit")]
    [InlineData("settings")]
    [InlineData("login/")]
    [InlineData("Logging-EventId-Registry.md")]
    public void No_node_is_created_for_prose_noise(string text)
    {
        Assert.DoesNotContain(G.Nodes, n => n.Title.Equals(text, StringComparison.OrdinalIgnoreCase)
                                            || n.Title.Equals(text + "()", StringComparison.OrdinalIgnoreCase)
                                            || n.Id.EndsWith(":" + text.ToLowerInvariant(), StringComparison.Ordinal));
    }

    [Fact]
    public void No_self_loops_and_no_duplicate_edges()
    {
        Assert.All(G.Edges, e => Assert.NotSame(e.From, e.To));
        var dupes = G.Edges.GroupBy(e => (e.From.Id, e.To.Id, e.Kind)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dupes);
        foreach (var n in G.Nodes)
        {
            var children = n.ChildrenInOrder();
            Assert.Equal(children.Count, children.Select(e => (e.To.Id, e.Kind)).Distinct().Count());
        }
    }

    [Fact]
    public void Children_are_in_document_order_within_a_kind()
    {
        var main = Node(G, "type:login/main");
        var orders = main.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.Order).ToList();
        Assert.Equal(orders.OrderBy(o => o), orders);
        var staff = Node(G, "method:staffsigninservice.signinasync");
        var calls = staff.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To.Title).ToList();
        Assert.Equal(new[] { "SettingsRepository.GetGlobalAsync", "EncryptionService.Decrypt", "DirectoryCredentialClient.Validate", "TwoFactorClient.SignRequest" }, calls);
    }

    [Theory]
    [InlineData("method:signincontroller.signinasync", "SignInController.SignInAsync")]
    [InlineData("endpoint:get /api/public/settings", "GET /api/public/settings")]
    [InlineData("browser:get /login", "Browser GET /Login (or /)")]
    [InlineData("type:login/main", "main.ts")]
    [InlineData("type:loginpage", "LoginPage.vue")]
    [InlineData("type:logincontroller", "LoginController")]
    [InlineData("method:loginpage.submit", "LoginPage.submit()")]
    [InlineData("method:login/api.loadloginpage", "loadLoginPage()")]
    [InlineData("route:/auth/start", "/auth/start")]
    [InlineData("destination:reportshomedestination", "ReportsHomeDestination")]
    [InlineData("type:signinusecase", "SignInUseCase")]
    public void Titles_use_the_readable_spelling(string id, string title)
    {
        var node = G.Find(id);
        Assert.NotNull(node);
        Assert.Equal(title, node!.Title);
    }

    [Fact]
    public void Methods_have_owners_and_fall_back_to_the_owner_path()
    {
        var methods = G.Nodes.Where(n => n.Kind == NodeKind.Method).ToList();
        Assert.NotEmpty(methods);
        foreach (var m in methods.Where(m => m.Title.Contains('.') && char.IsUpper(m.Title[0])))
            Assert.True(m.Owner != null, $"{m.Title} has no owner");
        foreach (var m in methods.Where(m => m.Owner?.Path != null))
        {
            Assert.Equal(m.Owner!.Path, m.Path);
            Assert.NotNull(m.Subtitle);
        }
        foreach (var m in methods.Where(m => m.Owner != null))
            Assert.Contains(m.Owner!.Outgoing, e => e.Kind == EdgeKind.Contains && e.To == m);
    }

    [Fact]
    public void Sources_point_at_lines_that_mention_the_node()
    {
        foreach (var n in G.Nodes.Where(n => n.Kind is not (NodeKind.Document or NodeKind.Section)))
        {
            Assert.NotEmpty(n.Sources);
            foreach (var s in n.Sources)
            {
                Assert.InRange(s.Line, 1, G.Lines.Count);
                var line = G.Lines[s.Line - 1];
                // A wrapped tree line ("..., \n otherwise ReportsHomeDestination") is recorded at its first line.
                var next = s.Line < G.Lines.Count ? G.Lines[s.Line] : string.Empty;
                var probe = Probe(n);
                Assert.True(line.Contains(probe, StringComparison.OrdinalIgnoreCase) || next.Contains(probe, StringComparison.OrdinalIgnoreCase),
                    $"{n.Title}: line {s.Line} '{line.Trim()}' does not mention '{probe}'");
            }
        }
    }

    private static string Probe(DocNode n)
    {
        var t = n.Title;
        var paren = t.IndexOf('(');
        if (paren > 0) t = t[..paren];
        var dot = t.LastIndexOf('.');
        if (n.Kind == NodeKind.Method && dot > 0) t = t[(dot + 1)..];
        if (n.Kind is NodeKind.TypeScript or NodeKind.VueComponent or NodeKind.Html) t = t[..t.LastIndexOf('.')];
        if (n.Kind == NodeKind.Browser) t = "Browser";
        return t;
    }

    [Fact]
    public void Every_section_has_a_source_and_items_in_document_order()
    {
        foreach (var s in G.Sections)
        {
            Assert.NotEmpty(s.Sources);
            var lines = s.Outgoing.Where(e => e.Kind == EdgeKind.Contains && e.Source != null).Select(e => e.Source!.Line).ToList();
            Assert.Equal(lines.OrderBy(l => l), lines);
        }
    }

    [Fact]
    public void Flow_items_get_the_section_as_subtitle_when_they_are_entry_points()
    {
        foreach (var e in G.EntryPoints)
        {
            Assert.NotNull(e.Section);
            Assert.Equal(e.Section!.Title, e.Subtitle);
        }
    }

    [Fact]
    public void Prose_descriptions_attach_to_the_node_they_start_with()
    {
        Assert.StartsWith("VitePageResolver reads the file", Node(G, "type:vitepageresolver").Description);
        Assert.StartsWith("/Login answers with status 401", Node(G, "route:/login").Description);
        Assert.Contains(Node(G, "method:signinusecase.executeasync").Notes, n => n.Contains("EventId 21002"));
        Assert.Contains(Node(G, "method:signincontroller.signinasync").Notes, n => n.Contains("[ValidateAntiForgeryToken]"));
        Assert.Contains(Node(G, "endpoint:post /api/sign-in").Notes, n => n.Contains("RequestVerificationToken"));
    }

    [Fact]
    public void Search_finds_nodes_by_title_alias_and_path()
    {
        Assert.Contains(G.Search("ExecuteAsync"), n => n.Id == "method:signinusecase.executeasync");
        Assert.Contains(G.Search("ISignInUseCase"), n => n.Id == "method:signinusecase.executeasync" || n.Id == "type:signinusecase");
        Assert.Contains(G.Search("VitePageResolver.cs"), n => n.Id == "type:vitepageresolver");
    }
}
