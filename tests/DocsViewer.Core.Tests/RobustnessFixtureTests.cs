using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;
using static DocsViewer.Core.Tests.GraphAssert;

namespace DocsViewer.Core.Tests;

/// <summary>Loads one markdown fixture from the Fixtures folder and parses it once.</summary>
public static class FixtureDoc
{
    private static readonly Dictionary<string, Lazy<(string Markdown, DocGraph Graph)>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Markdown, DocGraph Graph) Load(string fileName)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(fileName, out var lazy))
            {
                lazy = new Lazy<(string, DocGraph)>(() =>
                {
                    var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
                    var markdown = File.ReadAllText(path);
                    return (markdown, new MarkdownDocParser().Parse(markdown, path));
                });
                Cache[fileName] = lazy;
            }
            return lazy.Value;
        }
    }

    /// <summary>1-based line of the first line that contains <paramref name="text"/>.</summary>
    public static int LineOf(DocGraph g, string text)
    {
        for (var i = 0; i < g.Lines.Count; i++)
            if (g.Lines[i].Contains(text, StringComparison.Ordinal)) return i + 1;
        throw new InvalidOperationException($"'{text}' not found in {g.FilePath}");
    }

    /// <summary>1-based (first, last) line range of the body of the fenced block whose opening fence contains <paramref name="infoString"/>.</summary>
    public static (int First, int Last) FenceBody(DocGraph g, string infoString)
    {
        var open = LineOf(g, "```" + infoString);
        var close = open + 1;
        while (close <= g.Lines.Count && !g.Lines[close - 1].TrimStart().StartsWith("```", StringComparison.Ordinal)) close++;
        return (open + 1, close - 1);
    }

    /// <summary>All fenced block body ranges whose opening fence info string equals <paramref name="infoString"/>.</summary>
    public static List<(int First, int Last)> AllFenceBodies(DocGraph g, string infoString)
    {
        var ranges = new List<(int, int)>();
        for (var i = 0; i < g.Lines.Count; i++)
        {
            if (g.Lines[i].Trim() != "```" + infoString) continue;
            var close = i + 1;
            while (close < g.Lines.Count && !g.Lines[close].TrimStart().StartsWith("```", StringComparison.Ordinal)) close++;
            ranges.Add((i + 2, close));
            i = close;
        }
        return ranges;
    }

    public static bool InRange((int First, int Last) range, int line) => line >= range.First && line <= range.Last;

    /// <summary>Nodes reachable from <paramref name="from"/> over at most <paramref name="hops"/> non-Mentions edges.</summary>
    public static HashSet<DocNode> Reachable(DocNode from, int hops)
    {
        var seen = new HashSet<DocNode> { from };
        var frontier = new List<DocNode> { from };
        for (var i = 0; i < hops; i++)
        {
            var next = new List<DocNode>();
            foreach (var n in frontier)
                foreach (var e in n.Outgoing.Where(e => e.Kind != EdgeKind.Mentions))
                    if (seen.Add(e.To)) next.Add(e.To);
            frontier = next;
        }
        seen.Remove(from);
        return seen;
    }

    public static void NoParserErrors(DocGraph g)
    {
        Assert.NotNull(g.Root);
        var errors = g.Root!.Notes.Where(n => n.StartsWith("Parser error", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(errors.Count == 0, "Parser errors on the Document node: " + string.Join(" | ", errors));
    }
}

/// <summary>Every fixture: Parse never throws and never swallows an exception into a "Parser error" note.</summary>
public sealed class AllFixturesTests
{
    [Theory]
    [InlineData("Login-Plumbing.md")]
    [InlineData("ChangePassword-Plumbing.md")]
    [InlineData("Report-Export-Variations.md")]
    [InlineData("Plain-README.md")]
    public void Fixture_parses_without_exceptions_or_parser_error_notes(string file)
    {
        var (_, g) = FixtureDoc.Load(file);
        FixtureDoc.NoParserErrors(g);
        Assert.False(string.IsNullOrWhiteSpace(g.Title));
        Assert.All(g.Edges, e => Assert.NotSame(e.From, e.To));
        foreach (var n in g.Nodes)
            foreach (var s in n.Sources)
                Assert.True(s.Line >= 1 && s.Line <= g.Lines.Count, $"{n.Title}: line {s.Line} out of range");
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// (a) ChangePassword-Plumbing.md: same AI style as the reference doc, different feature.
// ---------------------------------------------------------------------------------------------------------------------

public sealed class ChangePasswordFixtureTests
{
    private static DocGraph G => FixtureDoc.Load("ChangePassword-Plumbing.md").Graph;

    [Fact]
    public void Title_summary_and_area_come_from_the_front_matter_and_H1()
    {
        var g = G;
        FixtureDoc.NoParserErrors(g);
        Assert.Equal("Change-password plumbing", g.Title);
        Assert.StartsWith("How the change-password page works", g.Summary);
        Assert.Equal("architecture", g.Area);
        Assert.Equal("doc:changepassword-plumbing.md", g.Root!.Id);
    }

    [Fact]
    public void Sections_are_in_document_order()
    {
        var g = G;
        Assert.Equal(new[]
        {
            "The pieces at a glance", "1. Loading the page", "2. Submitting the form", "3. Inside ChangePasswordUseCase",
            "4. PasswordPolicyService.Validate", "5. Back to the browser", "6. Where the follow-on addresses lead",
            "6a. Back to the login page", "6b. Expired links", "7. What gets logged",
        }, g.Sections.Select(s => s.Title));
        Assert.All(g.Sections, s => Assert.Equal(NodeKind.Section, s.Kind));
        Assert.Equal(10, g.Root!.Outgoing.Count(e => e.Kind == EdgeKind.Contains && e.To.Kind == NodeKind.Section));
        var six = Node(g, "section:6. where the follow-on addresses lead");
        Assert.Equal(2, six.Outgoing.Count(e => e.Kind == EdgeKind.Contains && e.To.Kind == NodeKind.Section));
        Assert.Equal(FixtureDoc.LineOf(g, "## 1. Loading the page"), Node(g, "section:1. loading the page").Sources[0].Line);
    }

    [Fact]
    public void Browser_entry_drills_to_the_page_controller_and_the_resolver()
    {
        var g = G;
        var browser = Node(g, "browser:get /changepassword");
        Assert.Equal(NodeKind.Browser, browser.Kind);
        Assert.Contains(browser, g.EntryPoints);
        Assert.Same(browser, g.EntryPoints[0]);
        Assert.Equal("1. Loading the page", browser.Subtitle);

        var index = Edge(browser, "ChangePasswordPageController.Index");
        Assert.Equal(EdgeKind.Calls, index.Kind);
        Assert.Equal("method:changepasswordpagecontroller.index", index.To.Id);
        Assert.Equal(NodeKind.Controller, index.To.Owner?.Kind);
        Assert.Equal("Controllers/ChangePasswordPageController.cs", index.To.Path);

        var getPagePath = Edge(index.To, "VitePageResolver.GetPagePath(...)");
        Assert.Equal(EdgeKind.Calls, getPagePath.Kind);
        Assert.Equal("method:vitepageresolver.getpagepath", getPagePath.To.Id);

        HasChild(browser, "route:/changepassword", EdgeKind.Uses);
        var redirect = Edge(index.To, "route:/login");
        Assert.Equal(EdgeKind.Redirects, redirect.Kind);
        Assert.Equal("missing u or t", redirect.Label);
    }

    [Fact]
    public void Browser_then_main_ts_with_its_steps_and_the_mount()
    {
        var g = G;
        var main = Edge(Node(g, "browser:get /changepassword"), "main.ts");
        Assert.Equal(EdgeKind.Then, main.Kind);
        Assert.Equal("type:change-password/main", main.To.Id);
        Assert.Equal(NodeKind.TypeScript, main.To.Kind);
        Assert.Equal("ClientApp/src/public/change-password/main.ts", main.To.Path);
        Assert.Equal("Page bootstrap", main.To.Subtitle);

        var calls = main.To.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To.Title).ToList();
        Assert.Equal(new[] { "setAppBaseFromBundleUrl()", "readTokenFromQuery()", "loadChangePasswordPage()" }, calls);
        HasChild(main.To, "ChangePasswordPage.vue", EdgeKind.Mounts);
    }

    [Fact]
    public void loadChangePasswordPage_calls_the_three_endpoints_in_order()
    {
        var g = G;
        var load = Node(g, "loadChangePasswordPage()");
        Assert.Equal(NodeKind.Method, load.Kind);
        Assert.Equal("api.ts", load.Owner?.Title);
        var endpoints = load.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To).ToList();
        Assert.Equal(new[] { "GET /api/public/settings", "GET /api/public/antiforgery-token", "GET /api/change-password" }, endpoints.Select(e => e.Title));
        Assert.All(endpoints, e => Assert.Equal(NodeKind.Endpoint, e.Kind));
        Assert.Contains(endpoints[2].Notes, n => n.StartsWith("Returns:") && n.Contains("tokenValid"));
        Assert.Null(TryNode(g, "tokenValid"));
    }

    [Fact]
    public void Page_state_endpoint_drills_into_GetPageState()
    {
        var g = G;
        var getPageState = Path(g, "GET /api/change-password", "ChangePasswordController.GetPageState");
        Assert.Equal("method:changepasswordcontroller.getpagestate", getPageState.Id);
        HasChild(getPageState, "IPasswordTokenService.ValidateAsync", EdgeKind.Calls);
        HasChild(getPageState, "IPasswordPolicyService.Describe()", EdgeKind.Calls);
        Assert.Equal(NodeKind.Service, Node(g, "IPasswordTokenService").Kind);
    }

    [Fact]
    public void Submit_drills_through_the_endpoint_controller_and_use_case_to_the_repository()
    {
        var g = G;
        var submit = Node(g, "method:changepasswordpage.submit");
        Assert.Contains(submit, g.EntryPoints);
        Assert.Equal("ChangePasswordPage.submit()", submit.Title);

        var change = Edge(submit, "changePassword(...)");
        Assert.Equal(EdgeKind.Calls, change.Kind);
        Assert.Equal("api.ts", change.To.Owner?.Title);

        var post = Edge(change.To, "POST /api/change-password");
        Assert.Equal(EdgeKind.Calls, post.Kind);
        Assert.Equal("endpoint:post /api/change-password", post.To.Id);

        var changeAsync = Edge(post.To, "ChangePasswordController.ChangeAsync");
        Assert.Equal(EdgeKind.Then, changeAsync.Kind);
        Assert.Equal("handled by", changeAsync.Label);

        var execute = Edge(changeAsync.To, "IChangePasswordUseCase.ExecuteAsync");
        Assert.Equal(EdgeKind.Calls, execute.Kind);
        Assert.Equal("method:changepasswordusecase.executeasync", execute.To.Id);

        var update = Edge(execute.To, "IUserRepository.UpdatePasswordAsync");
        Assert.Equal(EdgeKind.Calls, update.Kind);
        Assert.Equal("method:userrepository.updatepasswordasync", update.To.Id);
        Assert.Equal(NodeKind.Repository, update.To.Owner?.Kind);

        HasChild(execute.To, "IPasswordPolicyService.Validate", EdgeKind.Calls);
        HasChild(execute.To, "IPasswordTokenService.ExpireAsync", EdgeKind.Calls);
        var login = Edge(execute.To, "LoginDestination");
        Assert.Equal(EdgeKind.Returns, login.Kind);
        Assert.Equal(NodeKind.Destination, login.To.Kind);

        // Leaf branches collapse into labelled edges.
        var invalid = Edge(Node(g, "IUserRepository.GetActiveUserByLoginAsync"), "InvalidToken");
        Assert.Equal(EdgeKind.Returns, invalid.Kind);
        Assert.Equal("none", invalid.Label);
    }

    [Fact]
    public void Section_3_root_and_the_interface_call_are_the_same_node()
    {
        var g = G;
        var node = Node(g, "method:changepasswordusecase.executeasync");
        Assert.Contains("IChangePasswordUseCase.ExecuteAsync", node.Aliases);
        Assert.Contains("ExecuteAsync(command)", node.Aliases);
        Assert.Equal("type:changepasswordusecase", node.Owner?.Id);
        Assert.Equal(NodeKind.UseCase, node.Owner!.Kind);
        Assert.Equal("GraspANALYTICS.Core/Application/ChangePassword/UseCases/ChangePasswordUseCase.cs", node.Path);
        Assert.Contains(node.Sources, s => s.Line == FixtureDoc.LineOf(g, "└─ IChangePasswordUseCase.ExecuteAsync"));
        Assert.Contains(node.Sources, s => s.Line == FixtureDoc.LineOf(g, "ExecuteAsync(command)"));
        Assert.Single(g.Nodes, n => n.Kind == NodeKind.Method && n.Id.EndsWith(".executeasync", StringComparison.Ordinal));
    }

    [Fact]
    public void Section_4_heading_method_is_the_tree_root_and_has_its_rules()
    {
        var g = G;
        var validate = Node(g, "method:passwordpolicyservice.validate");
        Assert.Contains("Validate(user, password)", validate.Aliases);
        Assert.Contains("IPasswordPolicyService.Validate(user, newPassword)", validate.Aliases);
        Assert.Equal("PasswordPolicyService", validate.Owner?.Title);
        Assert.Equal(".../ChangePassword/Services/PasswordPolicyService.cs", validate.Path);
        Assert.Single(g.Nodes, n => n.Kind == NodeKind.Method && n.Id.EndsWith(".validate", StringComparison.Ordinal));

        HasChild(validate, "ISettingsRepository.GetGlobalAsync", EdgeKind.Calls);
        HasChild(validate, "IPasswordHistoryRepository.GetRecentAsync", EdgeKind.Calls);
        HasChild(validate, "ICommonPasswordClient.IsCommonAsync", EdgeKind.Calls);
        Assert.Equal("length below PASSWORD_MIN_LENGTH", Edge(validate, "TooShort").Label);
        Assert.Equal(EdgeKind.Returns, Edge(validate, "TooSimple").Kind);
        Assert.Equal("any rule fails", Edge(validate, "PolicyViolation").Label);
        Assert.Equal("true", Edge(Node(g, "ICommonPasswordClient.IsCommonAsync"), "TooCommon").Label);
        HasChild(Node(g, "type:commonpasswordclient"), "HaveIBeenPwnedClient", EdgeKind.Implements);
        Assert.Contains(Node(g, "type:haveibeenpwnedclient"), g.Components);
        Assert.DoesNotContain(validate, g.EntryPoints);
    }

    [Fact]
    public void Destinations_redirect_and_outcomes_carry_their_status()
    {
        var g = G;
        var login = Node(g, "destination:logindestination");
        Assert.Equal(EdgeKind.Redirects, Edge(login, "route:/login").Kind);
        HasChild(Node(g, "destination:reportshomedestination"), "route:/home", EdgeKind.Redirects);
        Assert.Contains(Node(g, "outcome:invalidtoken").Notes, n => n.Contains("401"));
        Assert.Contains(Node(g, "outcome:policyviolation").Notes, n => n.Contains("422"));
        HasChild(Node(g, "type:changepasswordcontroller"), "InvalidToken", EdgeKind.Returns);
    }

    [Fact]
    public void Components_view_shows_members_and_users()
    {
        var g = G;
        var page = Node(g, "type:changepasswordpage");
        Assert.Equal(NodeKind.VueComponent, page.Kind);
        Assert.Equal("ChangePasswordPage.vue", page.Title);
        Assert.Equal("ClientApp/src/public/change-password/ChangePasswordPage.vue", page.Path);
        Assert.Contains(page, g.Components);
        HasChild(page, "submit()", EdgeKind.Contains);
        // The page's submit reaches the POST endpoint.
        Assert.Contains(Node(g, "endpoint:post /api/change-password"), FixtureDoc.Reachable(Path(g, "type:changepasswordpage", "submit()"), 2));

        var api = Node(g, "type:change-password/api");
        Assert.Equal("api.ts", api.Title);
        HasChild(api, "loadChangePasswordPage()", EdgeKind.Contains);
        HasChild(api, "changePassword()", EdgeKind.Contains);
        Assert.Single(g.Nodes, n => n.Title.Equals("api.ts", StringComparison.OrdinalIgnoreCase));
        Assert.Null(g.Find("method:changepassword"));

        var controller = Node(g, "type:changepasswordcontroller");
        Assert.Equal(NodeKind.Controller, controller.Kind);
        Assert.Equal("Controllers/Api/ChangePasswordController.cs", controller.Path);
        HasChild(controller, "ChangeAsync", EdgeKind.Contains);
        HasChild(controller, "GetPageState", EdgeKind.Contains);
        HasChild(Node(g, "endpoint:get /api/change-password"), "ChangePasswordController", EdgeKind.Uses);
        HasChild(Node(g, "endpoint:post /api/change-password"), "ChangePasswordController", EdgeKind.Uses);

        Assert.True(g.Components.Count >= 15, $"only {g.Components.Count} components");
        Assert.Equal("index.html", g.Components[0].Title);
        Assert.Equal(g.Components.Count, g.Components.Distinct().Count());
        foreach (var expected in new[] { "main.ts", "ChangePasswordPage.vue", "api.ts", "publicSettings.ts", "ChangePasswordPageController", "VitePageResolver",
                     "ChangePasswordController", "ChangePasswordContractMapper", "ChangePasswordUseCase", "PasswordPolicyService", "PasswordTokenService",
                     "Pbkdf2HashingProvider", "PasswordChangeAuditService", "PasswordChangedMailer" })
            Assert.Contains(expected, g.Components.Select(c => c.Title));
    }

    [Fact]
    public void Entry_points_exclude_roots_reached_from_elsewhere()
    {
        var g = G;
        Assert.Equal("browser:get /changepassword", g.EntryPoints[0].Id);
        Assert.Contains(g.EntryPoints, e => e.Id == "method:changepasswordpage.submit");
        Assert.DoesNotContain(g.EntryPoints, e => e.Id.EndsWith(".executeasync", StringComparison.Ordinal));
        Assert.DoesNotContain(g.EntryPoints, e => e.Id.EndsWith(".validate", StringComparison.Ordinal));
        Assert.DoesNotContain(g.EntryPoints, e => e.Id.EndsWith(".changeasync", StringComparison.Ordinal));
        Assert.DoesNotContain(g.EntryPoints, e => e.Id == "type:change-password/main");
        Assert.All(g.EntryPoints, e => Assert.DoesNotContain(e.Incoming, i => GraphBuilderStrong(i.Kind)));
    }

    private static bool GraphBuilderStrong(EdgeKind k)
        => k is EdgeKind.Calls or EdgeKind.Then or EdgeKind.Uses or EdgeKind.Mounts or EdgeKind.Redirects or EdgeKind.Returns;
}

// ---------------------------------------------------------------------------------------------------------------------
// (b) Report-Export-Variations.md: deliberate formatting variations (CRLF, tabs, "->", mermaid, 4 columns, ...).
// ---------------------------------------------------------------------------------------------------------------------

public sealed class VariationsFixtureTests
{
    private static string Md => FixtureDoc.Load("Report-Export-Variations.md").Markdown;
    private static DocGraph G => FixtureDoc.Load("Report-Export-Variations.md").Graph;

    [Fact]
    public void Fixture_really_has_crlf_and_tabs()
    {
        Assert.Contains("\r\n", Md);
        Assert.DoesNotMatch(@"[^\r]\n", Md);
        Assert.Contains("\t├─", Md);
    }

    [Fact]
    public void Title_and_sections_in_order_including_an_unnumbered_heading_but_not_the_H4()
    {
        var g = G;
        FixtureDoc.NoParserErrors(g);
        Assert.Equal("Report export plumbing", g.Title);
        Assert.StartsWith("How a report export runs", g.Summary);
        Assert.Equal(new[]
        {
            "The pieces at a glance", "1. Starting an export", "2. On the server", "3. Inside ReportExportUseCase", "4. The worker", "Troubleshooting",
        }, g.Sections.Select(s => s.Title));
        Assert.DoesNotContain(g.Sections, s => s.Title == "Retention");
        Assert.Null(g.Find("section:retention"));
        Assert.Equal(FixtureDoc.LineOf(g, "## Troubleshooting"), Node(g, "section:troubleshooting").Sources[0].Line);
    }

    [Fact(Skip = "RB-01: a ```mermaid fence whose first line is a single word (sequenceDiagram) is detected as a call tree; the parser ignores the fence info string and invents Method 'sequenceDiagram' + Concepts 'B'/'C'")]
    public void Mermaid_fence_creates_no_nodes_or_edges()
    {
        var g = G;
        var fence = FixtureDoc.FenceBody(g, "mermaid");
        Assert.True(fence.Last > fence.First, "fixture must contain a mermaid fence");

        var nodesFromFence = g.Nodes.Where(n => n.Sources.Any(s => FixtureDoc.InRange(fence, s.Line))).Select(n => $"{n.Kind} {n.Title}").ToList();
        Assert.True(nodesFromFence.Count == 0, "nodes created from the mermaid fence: " + string.Join(", ", nodesFromFence));
        var edgesFromFence = g.Edges.Where(e => e.Source != null && FixtureDoc.InRange(fence, e.Source.Line)).Select(e => e.ToString()).ToList();
        Assert.True(edgesFromFence.Count == 0, "edges created from the mermaid fence: " + string.Join(", ", edgesFromFence));

        Assert.Null(TryNode(g, "sequenceDiagram"));
        Assert.DoesNotContain(g.Nodes, n => n.Title is "B" or "C" or "Q");
        Assert.DoesNotContain(g.EntryPoints, e => e.Title == "sequenceDiagram");
        var troubleshooting = Node(g, "section:troubleshooting");
        Assert.DoesNotContain(troubleshooting.Outgoing, e => e.Kind == EdgeKind.Contains);
    }

    [Fact]
    public void Ascii_arrow_tree_still_yields_Calls_and_Returns_edges()
    {
        var g = G;
        var execute = Node(g, "method:reportexportusecase.executeasync");
        Assert.Contains("ExecuteAsync(command)", execute.Aliases);
        Assert.Contains("IReportExportUseCase.ExecuteAsync", execute.Aliases);

        var getById = Edge(execute, "IReportRepository.GetByIdAsync");
        Assert.Equal(EdgeKind.Calls, getById.Kind);
        var reject = Edge(getById.To, "RejectAsync");
        Assert.Equal(EdgeKind.Calls, reject.Kind);
        Assert.Equal("none", reject.Label);
        HasChild(reject.To, "ReportNotFound", EdgeKind.Returns);
        Assert.Equal(NodeKind.Outcome, Node(g, "outcome:reportnotfound").Kind);

        var canExport = Edge(execute, "IReportAccessPolicy.CanExport");
        Assert.Equal(EdgeKind.Calls, canExport.Kind);
        HasChild(canExport.To, "Forbidden", EdgeKind.Returns);

        var enqueue = Edge(execute, "IExportQueue.Enqueue");
        Assert.Equal(EdgeKind.Calls, enqueue.Kind);
        HasChild(enqueue.To, "Queued", EdgeKind.Returns);

        // Right-hand annotation with an ASCII arrow: "ReportExportContractMapper -> ReportExportCommand(...)".
        var toCommand = Edge(Node(g, "method:reportexportcontroller.startasync"), "request.ToCommand()");
        var mapper = Edge(toCommand.To, "ReportExportContractMapper");
        Assert.Equal(EdgeKind.Uses, mapper.Kind);
        HasChild(mapper.To, "ReportExportCommand", EdgeKind.Returns);
        Assert.DoesNotContain(g.Nodes, n => n.Title.Contains("->", StringComparison.Ordinal) || n.Title.Contains('-') && n.Title.EndsWith(">"));
    }

    [Fact]
    public void Tab_indented_tree_nests_correctly()
    {
        var g = G;
        var download = Node(g, "method:exportpage.download");
        Assert.Equal("ExportPage.download()", download.Title);
        Assert.Contains(download, g.EntryPoints);
        var rootLine = FixtureDoc.LineOf(g, "ExportPage.download()");
        Assert.True(download.Sources.Any(s => s.Line == rootLine),
            $"expected a source at line {rootLine}; sources: {string.Join(" | ", download.Sources.Select(s => $"{s.Line}:{s.Text}"))}");

        var request = Edge(download, "requestExport");
        Assert.Equal(EdgeKind.Calls, request.Kind);
        var post = Edge(request.To, "POST /api/reports/export");
        Assert.Equal(EdgeKind.Calls, post.Kind);
        Assert.Equal("endpoint:post /api/reports/export", post.To.Id);

        var poll = Edge(download, "pollStatus");
        Assert.Equal(EdgeKind.Calls, poll.Kind);
        var status = Edge(poll.To, "GET /api/reports/export/{jobId}");
        Assert.Equal(EdgeKind.Calls, status.Kind);
        Assert.Equal("endpoint:get /api/reports/export/{jobid}", status.To.Id);

        // The endpoints hang off the functions, not off the root.
        HasNoChild(download, "POST /api/reports/export");
        HasNoChild(download, "GET /api/reports/export/{jobId}");
        HasNoChild(request.To, "GET /api/reports/export/{jobId}");
        // The endpoint is first met in the pieces table; the tree line must be among its sources too.
        var treeLine = FixtureDoc.LineOf(g, "└─ GET /api/reports/export/{jobId}");
        Assert.True(status.To.Sources.Any(s => s.Line == treeLine),
            $"expected a source at line {treeLine}; sources: {string.Join(" | ", status.To.Sources.Select(s => $"{s.Line}:{s.Text}"))}");
    }

    [Fact(Skip = "RB-04: inside a tree rooted at a Vue method (ExportPage.download()), owner-less calls requestExport(...)/pollStatus(...) are created as members of the root's owner (ExportPage.requestExport()) instead of resolving to the existing api.ts functions requestExport()/pollStatus()")]
    public void Tab_indented_tree_calls_resolve_to_the_existing_api_ts_functions()
    {
        var g = G;
        var download = Node(g, "method:exportpage.download");
        var request = Edge(download, "requestExport").To;
        Assert.Equal("api.ts", request.Owner?.Title);
        Assert.Equal("method:export/api.requestexport", request.Id);
        Assert.Single(g.Nodes, n => n.Kind == NodeKind.Method && n.Id.EndsWith(".requestexport", StringComparison.Ordinal));
        Assert.Single(g.Nodes, n => n.Kind == NodeKind.Method && n.Id.EndsWith(".pollstatus", StringComparison.Ordinal));
        Assert.Equal("api.ts", Edge(download, "pollStatus").To.Owner?.Title);
    }

    [Fact]
    public void Four_column_pieces_table_still_yields_components_with_paths()
    {
        var g = G;
        var byTitle = g.Components.ToDictionary(c => c.Title, StringComparer.OrdinalIgnoreCase);
        Assert.True(g.Components.Count >= 7, $"only {g.Components.Count} components: {string.Join(", ", byTitle.Keys)}");

        Assert.Equal("ClientApp/src/reports/export/ExportPage.vue", byTitle["ExportPage.vue"].Path);
        Assert.Equal(NodeKind.VueComponent, byTitle["ExportPage.vue"].Kind);
        Assert.Equal("Export page", byTitle["ExportPage.vue"].Subtitle);
        Assert.Equal("ClientApp/src/reports/export/api.ts", byTitle["api.ts"].Path);
        Assert.Equal("Controllers/Api/ReportExportController.cs", byTitle["ReportExportController"].Path);
        Assert.Equal(NodeKind.Controller, byTitle["ReportExportController"].Kind);
        Assert.Equal("GraspANALYTICS.Core/Application/Reports/UseCases/ReportExportUseCase.cs", byTitle["ReportExportUseCase"].Path);
        Assert.Equal("Reports/Rendering/PdfReportRenderer.cs", byTitle["PdfReportRenderer"].Path);
        Assert.Equal("Reports/Rendering/XlsxReportRenderer.cs", byTitle["XlsxReportRenderer"].Path);
        Assert.Equal("Core/Infrastructure/Storage/AzureBlobStore.cs", byTitle["AzureBlobStore"].Path);
        Assert.Equal("ExportPage.vue", g.Components[0].Title);

        // The "What it does" cell is the description; the link text survives as plain text, the Owner column is ignored.
        var controller = byTitle["ReportExportController"];
        Assert.Contains("See the export ADR for the queue decision", controller.Description);
        Assert.DoesNotContain("https://", controller.Description);
        Assert.DoesNotContain(g.Nodes, n => n.Title.Contains("Platform team") || n.Title.Contains("Reporting team"));
        HasChild(Node(g, "endpoint:post /api/reports/export"), "ReportExportController", EdgeKind.Uses);
        HasChild(Node(g, "endpoint:get /api/reports/export/{jobid}"), "ReportExportController", EdgeKind.Uses);
        HasChild(byTitle["api.ts"], "requestExport()", EdgeKind.Contains);
        HasChild(byTitle["api.ts"], "pollStatus()", EdgeKind.Contains);
    }

    [Fact(Skip = "RB-03: a pieces-table Where cell whose back-ticked path contains a space (Reports/Export Service/ReportExportService.cs) is not identifier-like, so the whole row is dropped; ReportExportService only exists as an implicit type without Path/Subtitle/Description")]
    public void Pieces_row_whose_path_contains_a_space_still_yields_a_component_with_that_path()
    {
        var g = G;
        var worker = Node(g, "type:reportexportservice");
        Assert.Contains(worker, g.Components);
        Assert.Equal("Reports/Export Service/ReportExportService.cs", worker.Path);
        Assert.Equal("Export worker", worker.Subtitle);
        Assert.StartsWith("Hosted service that dequeues jobs", worker.Description);
        // Piece rows come before implicit types.
        Assert.True(g.Components.IndexOf(worker) < g.Components.IndexOf(Node(g, "type:exportjobstore")));
    }

    [Fact]
    public void Code_span_containing_a_pipe_does_not_break_the_call_table_row()
    {
        var g = G;
        var post = Node(g, "endpoint:post /api/reports/export");
        var start = Edge(post, "ReportExportController.StartAsync");
        Assert.Equal(EdgeKind.Uses, start.Kind);
        HasChild(start.To, "IReportExportUseCase.ExecuteAsync", EdgeKind.Calls);
        Assert.Contains(post.Notes, n => n.StartsWith("Returns:") && n.Contains("queued") && n.Contains("done"));
        Assert.DoesNotContain(g.Nodes, n => n.Title.Contains("running") || n.Title.Contains("\"done\""));

        var status = Node(g, "endpoint:get /api/reports/export/{jobid}");
        HasChild(status, "ReportExportController.StatusAsync", EdgeKind.Uses);
        HasChild(Node(g, "method:reportexportcontroller.statusasync"), "IExportJobStore.GetAsync", EdgeKind.Calls);
        Assert.Contains(status.Notes, n => n.StartsWith("Returns:") && n.Contains("downloadUrl"));
    }

    [Fact]
    public void Lettered_sub_steps_inside_a_bullet_still_reach_both_functions()
    {
        var g = G;
        var mounted = Node(g, "method:exportpage.mounted");
        Assert.Equal("ExportPage.vue", mounted.Owner?.Title);
        var reachable = FixtureDoc.Reachable(mounted, 2).Select(n => n.Title).ToList();
        Assert.Contains("loadFormats()", reachable);
        Assert.Contains("loadRecentJobs()", reachable);
        Assert.Contains(mounted.Notes, n => n.Contains("Falls back to PDF"));
    }

    [Fact(Skip = "RB-05: lettered sub-steps ('a. calls `loadFormats()`', 'b. calls `loadRecentJobs()`') inside a step-list bullet are one paragraph to Markdig, so the second call becomes a Uses child of the first instead of a sibling Calls child of ExportPage.mounted()")]
    public void Lettered_sub_steps_inside_a_bullet_are_sibling_Calls_of_the_subject()
    {
        var g = G;
        var mounted = Node(g, "method:exportpage.mounted");
        HasChild(mounted, "loadFormats()", EdgeKind.Calls);
        HasChild(mounted, "loadRecentJobs()", EdgeKind.Calls);
    }

    [Fact]
    public void Tree_with_only_end_connectors_yields_siblings_and_implementations()
    {
        var g = G;
        var run = Node(g, "method:reportexportservice.runasync");
        Assert.Contains("ReportExportService.RunAsync(job)", run.Aliases);
        var calls = run.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To.Id).ToList();
        Assert.Equal(new[]
        {
            "method:exportjobstore.markrunningasync", "method:reportrepository.getbyidasync", "method:reportrenderer.renderasync",
            "method:blobstore.saveasync", "method:exportjobstore.markdoneasync",
        }, calls);
        Assert.Equal(FixtureDoc.LineOf(g, "ReportExportService.RunAsync(job)"), run.Sources[0].Line);
        HasChild(Node(g, "type:reportrenderer"), "PdfReportRenderer", EdgeKind.Implements);
        HasChild(Node(g, "type:blobstore"), "AzureBlobStore", EdgeKind.Implements);
        Assert.Contains(run, g.EntryPoints);
    }

    [Fact]
    public void Nested_bullets_three_deep_and_the_H4_become_notes_not_nodes()
    {
        var g = G;
        // Prose only resolves existing nodes, so the deep bullet's `GeneralSettings` is text, not a node, and nothing in the
        // nested bullets becomes a node.
        Assert.DoesNotContain(g.Nodes, n => n.Title.StartsWith("uses the shared", StringComparison.OrdinalIgnoreCase) || n.Title.StartsWith("headers and footers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(g.Nodes.SelectMany(n => n.Notes), n => n.Contains("headers and footers come from GeneralSettings"));
        Assert.DoesNotContain(g.Nodes, n => n.Title is "pdf" or "xlsx" or "Retention");
        var run = Node(g, "method:reportexportservice.runasync");
        Assert.Contains(run.Notes.Concat(run.Owner!.Notes).Concat(run.Owner.Description is null ? Array.Empty<string>() : new[] { run.Owner.Description }),
            n => n.Contains("hosted service", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(g.Nodes.SelectMany(n => n.Notes), n => n.Contains("24 hours"));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// (c) Plain-README.md: nothing to do with plumbing.
// ---------------------------------------------------------------------------------------------------------------------

public sealed class PlainReadmeFixtureTests
{
    private static DocGraph G => FixtureDoc.Load("Plain-README.md").Graph;

    [Fact]
    public void Title_and_sections_come_through_without_front_matter()
    {
        var g = G;
        FixtureDoc.NoParserErrors(g);
        Assert.Equal("ReportKit", g.Title);
        Assert.Null(g.Summary);
        Assert.Null(g.Area);
        Assert.Equal(new[] { "Features", "Installation", "Usage", "Configuration", "Contributing", "License" }, g.Sections.Select(s => s.Title));
        Assert.Equal("doc:plain-readme.md", g.Root!.Id);
    }

    [Fact]
    public void No_endpoint_route_or_controller_nodes_are_invented_from_prose_or_badges()
    {
        var g = G;
        Assert.DoesNotContain(g.Nodes, n => n.Kind is NodeKind.Endpoint or NodeKind.Controller or NodeKind.Route or NodeKind.Browser);
        Assert.DoesNotContain(g.Nodes, n => n.Kind is NodeKind.Setting);
        Assert.Empty(g.Components);
        Assert.DoesNotContain(g.Nodes, n => n.Title.Contains("shields.io") || n.Title.Contains("<img") || n.Title.Contains("--format"));
        Assert.Null(TryNode(g, "REPORTKIT_OUT_DIR"));
        Assert.Null(TryNode(g, "main"));
        Assert.Null(TryNode(g, "reportkit.config.json"));
    }

    [Fact]
    public void Nodes_other_than_document_and_sections_are_very_few()
    {
        var g = G;
        var extra = g.Nodes.Where(n => n.Kind is not (NodeKind.Document or NodeKind.Section)).Select(n => $"{n.Kind} {n.Title}").ToList();
        Assert.True(extra.Count <= 2, "unexpected nodes: " + string.Join(", ", extra));
    }

    [Fact(Skip = "RB-02: a ```bash fence whose first line is a bare word (make) is detected as a call tree; the parser ignores the fence info string and invents Method 'make' as an entry point")]
    public void Bash_and_json_fences_are_not_parsed_as_call_trees()
    {
        var g = G;
        var fences = FixtureDoc.AllFenceBodies(g, "bash").Concat(FixtureDoc.AllFenceBodies(g, "json")).ToList();
        Assert.True(fences.Count >= 5, "fixture must contain several bash/json fences");
        var fromFences = g.Nodes.Where(n => n.Sources.Any(s => fences.Any(f => FixtureDoc.InRange(f, s.Line)))).Select(n => $"{n.Kind} {n.Title}").ToList();
        Assert.True(fromFences.Count == 0, "nodes created from bash/json fences: " + string.Join(", ", fromFences));
        Assert.DoesNotContain(g.Nodes, n => n.Kind == NodeKind.Method);
        Assert.Empty(g.EntryPoints);
        Assert.Null(TryNode(g, "make"));
    }
}
