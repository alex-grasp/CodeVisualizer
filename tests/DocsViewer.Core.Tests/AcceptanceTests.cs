using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;
using static DocsViewer.Core.Tests.GraphAssert;

namespace DocsViewer.Core.Tests;

/// <summary>One test per numbered acceptance item in SPEC.md section 3, against docs/Login-Plumbing.md.</summary>
public sealed class AcceptanceTests : IClassFixture<ReferenceDoc>
{
    private readonly DocGraph _g;

    public AcceptanceTests(ReferenceDoc doc) => _g = doc.Graph;

    [Fact]
    public void Item01_Browser_entry_drills_to_LoginController_Index_and_VitePageResolver()
    {
        var browser = Node(_g, "browser:get /login");
        Assert.Equal(NodeKind.Browser, browser.Kind);
        Assert.Equal("Browser GET /Login (or /)", browser.Title);
        Assert.Contains(browser, _g.EntryPoints);

        var index = Edge(browser, "LoginController.Index");
        Assert.Equal(EdgeKind.Calls, index.Kind);
        Assert.Equal("method:logincontroller.index", index.To.Id);

        var getPagePath = Edge(index.To, "VitePageResolver.GetPagePath(...)");
        Assert.Equal(EdgeKind.Calls, getPagePath.Kind);
        Assert.Equal("method:vitepageresolver.getpagepath", getPagePath.To.Id);
        Assert.Equal("VitePageResolver", getPagePath.To.Owner?.Title);

        // The browser uses the /Login route; the controller redirects deeper paths back to it.
        HasChild(browser, "route:/login", EdgeKind.Uses);
        HasChild(index.To, "route:/login", EdgeKind.Redirects);
    }

    [Fact]
    public void Item02_Browser_then_main_ts_with_its_steps_and_mount()
    {
        var browser = Node(_g, "browser:get /login");
        var main = Edge(browser, "main.ts");
        Assert.Equal(EdgeKind.Then, main.Kind);
        Assert.Equal("type:login/main", main.To.Id);
        Assert.Equal(NodeKind.TypeScript, main.To.Kind);
        Assert.Equal("ClientApp/src/public/login/main.ts", main.To.Path);

        HasChild(main.To, "setAppBaseFromBundleUrl(...)", EdgeKind.Calls);
        HasChild(main.To, "loadLoginPage()", EdgeKind.Calls);
        HasChild(main.To, "LoginPage.vue", EdgeKind.Mounts);

        // Document order of the steps.
        var calls = main.To.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To.Title).ToList();
        Assert.Equal(new[] { "setAppBaseFromBundleUrl()", "loadLoginPage()" }, calls);
    }

    [Fact]
    public void Item03_loadLoginPage_calls_the_three_endpoints_in_order()
    {
        var load = Node(_g, "loadLoginPage()");
        Assert.Equal(NodeKind.Method, load.Kind);
        Assert.Equal("api.ts", load.Owner?.Title);
        var endpoints = load.ChildrenInOrder().Where(e => e.Kind == EdgeKind.Calls).Select(e => e.To).ToList();
        Assert.Equal(new[] { "GET /api/public/settings", "GET /api/public/antiforgery-token", "GET /api/sign-in" }, endpoints.Select(e => e.Title));
        Assert.All(endpoints, e => Assert.Equal(NodeKind.Endpoint, e.Kind));
        Assert.Equal("endpoint:get /api/public/settings", endpoints[0].Id);

        // Returns cells are notes, not nodes.
        Assert.Contains(endpoints[0].Notes, n => n.StartsWith("Returns:") && n.Contains("applicationTitle"));
        Assert.Null(TryNode(_g, "applicationTitle"));
        Assert.Null(TryNode(_g, "requestToken"));
    }

    [Fact]
    public void Item04_public_settings_endpoint_drills_into_controller_dependencies()
    {
        var controller = Path(_g, "GET /api/public/settings", "PublicSettingsController");
        Assert.Equal(NodeKind.Controller, controller.Kind);
        Assert.Equal("Controllers/Api/PublicSettingsController.cs", controller.Path);
        var deps = Children(controller).Select(n => n.Title).ToList();
        Assert.Contains("GeneralSettings", deps);
        Assert.Contains(deps, d => Normalise(d) == Normalise("IAppVersionService"));
        Assert.Contains(deps, d => Normalise(d) == Normalise("IGoogleAnalyticsIdResolver"));
        Assert.Contains(deps, d => Normalise(d) == Normalise("IHumanVerificationWidgetResolver"));
        Assert.Equal(NodeKind.Service, Node(_g, "IAppVersionService").Kind);
        Assert.Contains("IAppVersionService", Node(_g, "IAppVersionService").Aliases);
    }

    [Fact]
    public void Item05_sign_in_page_state_endpoint_drills_into_GetPageState()
    {
        var getPageState = Path(_g, "GET /api/sign-in", "SignInController.GetPageState");
        Assert.Equal("method:signincontroller.getpagestate", getPageState.Id);
        Assert.Equal("SignInController", getPageState.Owner?.Title);
        HasChild(getPageState, "ISignInNoticeStore.Take()");
        HasChild(getPageState, "IFailedSignInStore.IsHumanVerificationRequired(...)");
        Assert.Equal("method:failedsigninstore.ishumanverificationrequired", Edge(getPageState, "IFailedSignInStore.IsHumanVerificationRequired").To.Id);
    }

    [Fact]
    public void Item06_submit_drills_all_the_way_to_the_repository()
    {
        var submit = Node(_g, "method:loginpage.submit");
        Assert.Contains(submit, _g.EntryPoints);
        Assert.Equal("LoginPage.submit()", submit.Title);

        var signIn = Edge(submit, "signIn(...)");
        Assert.Equal(EdgeKind.Calls, signIn.Kind);
        Assert.Equal("api.ts", signIn.To.Owner?.Title);

        var post = Edge(signIn.To, "POST /api/sign-in");
        Assert.Equal(EdgeKind.Calls, post.Kind);
        Assert.Equal("endpoint:post /api/sign-in", post.To.Id);

        var signInAsync = Edge(post.To, "SignInController.SignInAsync");
        Assert.Equal(EdgeKind.Then, signInAsync.Kind);
        Assert.Equal("handled by", signInAsync.Label);

        var execute = Edge(signInAsync.To, "ISignInUseCase.ExecuteAsync");
        Assert.Equal(EdgeKind.Calls, execute.Kind);
        Assert.Equal("method:signinusecase.executeasync", execute.To.Id);

        var inner = Edge(execute.To, "SignInAsync");
        Assert.Equal("method:signinusecase.signinasync", inner.To.Id);

        HasChild(inner.To, "IUserRepository.GetActiveUserByLoginAsync", EdgeKind.Calls);
        HasChild(inner.To, "ISignInCompletionService.CompleteAsync", EdgeKind.Calls);
        Assert.Equal("otherwise", Edge(inner.To, "IUserRepository.GetActiveUserByLoginAsync").Label);
        Assert.Equal(NodeKind.Repository, Node(_g, "IUserRepository").Kind);

        // The leaf branch "none → RejectUnknownLoginAsync → InvalidCredentials" collapses into labelled edges.
        var reject = Edge(Node(_g, "IUserRepository.GetActiveUserByLoginAsync"), "RejectUnknownLoginAsync");
        Assert.Equal(EdgeKind.Calls, reject.Kind);
        Assert.Equal("none", reject.Label);
        HasChild(reject.To, "InvalidCredentials", EdgeKind.Returns);
    }

    [Fact]
    public void Item07_interface_call_and_section_root_are_the_same_node()
    {
        var node = Node(_g, "method:signinusecase.executeasync");
        Assert.Equal(NodeKind.Method, node.Kind);
        Assert.Contains("ISignInUseCase.ExecuteAsync", node.Aliases);
        Assert.Contains("ExecuteAsync(command)", node.Aliases);
        Assert.NotNull(node.Owner);
        Assert.Equal("type:signinusecase", node.Owner!.Id);
        Assert.Equal("SignInUseCase", node.Owner.Title);
        Assert.Equal(NodeKind.UseCase, node.Owner.Kind);
        Assert.Equal("GraspANALYTICS.Core/Application/SignIn/UseCases/SignInUseCase.cs", node.Owner.Path);
        Assert.Equal("GraspANALYTICS.Core/Application/SignIn/UseCases/SignInUseCase.cs", node.Path);
        // Mentioned in section 2 (line 82) and defined in section 3 (line 96).
        Assert.Contains(node.Sources, s => s.Line == 82);
        Assert.Contains(node.Sources, s => s.Line == 96);
        Assert.Single(_g.Nodes, n => n.Id.EndsWith(".executeasync", StringComparison.Ordinal));
    }

    [Fact]
    public void Item08_CompleteAsync_drills_to_the_session_and_the_user_update()
    {
        var complete = Node(_g, "method:signincompletionservice.completeasync");
        Assert.Contains("ISignInCompletionService.CompleteAsync", complete.Aliases.Select(a => a.Split('(')[0]));
        HasChild(complete, "IRoleService.GetRoleByIdAsync", EdgeKind.Calls);

        var start = Edge(complete, "StartSessionAsync").To;
        Assert.Equal("method:signincompletionservice.startsessionasync", start.Id);

        var startAsync = Edge(start, "IAuthenticatedSessionService.StartAsync").To;
        var loginUser = Edge(startAsync, "LoginService.LoginUser").To;
        Assert.Equal("method:loginservice.loginuser", loginUser.Id);
        var initSession = Edge(loginUser, "SessionService.InitSession").To;
        Assert.Equal("method:sessionservice.initsession", initSession.Id);
        HasChild(initSession, "SimultaneousSignIn", EdgeKind.Returns);

        HasChild(start, "IUserRepository.UpdateUserAsync", EdgeKind.Calls);
        HasChild(start, "ReportsHomeDestination", EdgeKind.Returns);
        HasChild(start, "AdminHomeDestination", EdgeKind.Returns);

        // The implementation named in the continuation line.
        var iface = Node(_g, "type:authenticatedsessionservice");
        HasChild(iface, "HttpAuthenticatedSessionService", EdgeKind.Implements);
    }

    [Fact]
    public void Item09_staff_sign_in_resolves_to_section_6a_root()
    {
        var staff = Node(_g, "method:staffsigninservice.signinasync");
        Assert.Contains("IStaffSignInService.SignInAsync", staff.Aliases);
        Assert.Equal("StaffSignInService", staff.Owner?.Title);
        Assert.Equal(".../SignIn/Services/StaffSignInService.cs", staff.Path);

        var fromUseCase = Edge(Node(_g, "method:signinusecase.signinasync"), "IStaffSignInService.SignInAsync");
        Assert.Same(staff, fromUseCase.To);
        Assert.Contains("grasp", fromUseCase.Label);

        HasChild(staff, "ISettingsRepository.GetGlobalAsync", EdgeKind.Calls);
        HasChild(staff, "IDirectoryCredentialClient.Validate", EdgeKind.Calls);
        HasChild(staff, "ITwoFactorClient.SignRequest", EdgeKind.Calls);
        HasChild(staff, "TwoFactorDestination", EdgeKind.Returns);
        HasChild(Node(_g, "TwoFactorDestination"), "route:/duo", EdgeKind.Redirects);

        // Wrapped note lines stay notes.
        Assert.Null(TryNode(_g, "settings()"));
        Assert.Contains(Node(_g, "ISettingsRepository.GetGlobalAsync").Notes, n => n.Contains("LDAPServers"));
    }

    [Fact]
    public void Item10_components_view_shows_members_and_users()
    {
        var page = Node(_g, "type:loginpage");
        Assert.Equal(NodeKind.VueComponent, page.Kind);
        Assert.Equal("LoginPage.vue", page.Title);
        Assert.Equal("ClientApp/src/public/login/LoginPage.vue", page.Path);
        Assert.Contains(page, _g.Components);
        HasChild(page, "submit()", EdgeKind.Contains);

        var api = Node(_g, "type:login/api");
        Assert.Equal("api.ts", api.Title);
        Assert.Contains(api, _g.Components);
        HasChild(api, "loadLoginPage()", EdgeKind.Contains);
        HasChild(api, "signIn()", EdgeKind.Contains);
        Assert.Single(_g.Nodes, n => n.Title.Equals("api.ts", StringComparison.OrdinalIgnoreCase));

        var controller = Node(_g, "type:signincontroller");
        Assert.Equal(NodeKind.Controller, controller.Kind);
        Assert.Contains(controller, _g.Components);
        HasChild(controller, "SignInAsync", EdgeKind.Contains);
        HasChild(controller, "GetPageState", EdgeKind.Contains);
        Assert.Same(controller, Node(_g, "method:signincontroller.signinasync").Owner);
        HasChild(Node(_g, "endpoint:get /api/sign-in"), "SignInController", EdgeKind.Uses);
        HasChild(Node(_g, "endpoint:post /api/sign-in"), "SignInController", EdgeKind.Uses);
        Assert.Equal("Controllers/Api/SignInController.cs", Node(_g, "method:signincontroller.signinasync").Path);
    }

    [Fact]
    public void Item11_destinations_redirect_to_routes_and_sso_route_has_section_6b_children()
    {
        var reports = Node(_g, "destination:reportshomedestination");
        Assert.Equal(NodeKind.Destination, reports.Kind);
        HasChild(reports, "route:/home", EdgeKind.Redirects);

        var idp = Node(_g, "destination:identityproviderdestination");
        var start = Edge(idp, "route:/auth/start");
        Assert.Equal(EdgeKind.Redirects, start.Kind);
        Assert.Equal("/auth/start", start.To.Title);
        HasChild(start.To, "MapSsoEndpointExtensions.StartAuth", EdgeKind.Calls);
        Assert.Equal("MapSsoEndpointExtensions", Node(_g, "MapSsoEndpointExtensions.StartAuth").Owner?.Title);

        HasChild(Node(_g, "destination:passwordchangedestination"), "route:/changepassword", EdgeKind.Redirects);
        HasChild(Node(_g, "destination:twofactordestination"), "route:/duo", EdgeKind.Redirects);
        HasChild(Node(_g, "destination:adminhomedestination"), "route:/admin", EdgeKind.Redirects);
    }

    [Fact]
    public void Item12_outcomes_carry_their_http_status()
    {
        var invalid = Node(_g, "outcome:invalidcredentials");
        Assert.Equal(NodeKind.Outcome, invalid.Kind);
        Assert.Contains(invalid.Notes, n => n.Contains("401"));
        var simultaneous = Node(_g, "outcome:simultaneoussignin");
        Assert.Contains(simultaneous.Notes, n => n.Contains("409"));
        Assert.Contains(Node(_g, "outcome:invalidrole").Notes, n => n.Contains("403"));
        Assert.Contains(Node(_g, "outcome:humanverificationfailed").Notes, n => n.Contains("400"));
        Assert.Contains(Node(_g, "outcome:domainuserinvalid").Notes, n => n.Contains("401"));
        HasChild(Node(_g, "type:signincontroller"), "SimultaneousSignIn", EdgeKind.Returns);
    }

    [Fact]
    public void Item13_sections_title_and_summary()
    {
        Assert.Equal(13, _g.Sections.Count);
        Assert.Equal("Login plumbing", _g.Title);
        Assert.StartsWith("How sign-in works", _g.Summary);
        Assert.Equal("architecture", _g.Area);
        Assert.Equal(new[]
        {
            "The pieces at a glance", "1. Loading the page", "2. Submitting the form", "3. Inside SignInUseCase",
            "4. SignInCompletionService.CompleteAsync", "5. Back to the browser", "6. Where the follow-on addresses lead",
            "6a. Staff accounts (grasp\\...): LDAP, then Duo", "6b. SSO-only users: OAuth round trip", "6c. Forced password change",
            "7. When reCAPTCHA is required", "8. Every request after sign-in", "9. What gets logged",
        }, _g.Sections.Select(s => s.Title));
        Assert.All(_g.Sections, s => Assert.Equal(NodeKind.Section, s.Kind));
        Assert.NotNull(_g.Root);
        Assert.Equal("doc:login-plumbing.md", _g.Root!.Id);
        Assert.Equal(13, _g.Root.Outgoing.Count(e => e.Kind == EdgeKind.Contains && e.To.Kind == NodeKind.Section));
        var six = Node(_g, "section:6. where the follow-on addresses lead");
        Assert.Equal(3, six.Outgoing.Count(e => e.Kind == EdgeKind.Contains && e.To.Kind == NodeKind.Section));
        Assert.Equal(32, Node(_g, "section:1. loading the page").Sources[0].Line);
    }

    [Fact]
    public void Item14_entry_points_start_with_the_browser_and_exclude_called_roots()
    {
        Assert.NotEmpty(_g.EntryPoints);
        Assert.Equal("browser:get /login", _g.EntryPoints[0].Id);
        Assert.Equal("1. Loading the page", _g.EntryPoints[0].Subtitle);
        Assert.Contains(_g.EntryPoints, e => e.Id == "method:loginpage.submit");
        Assert.DoesNotContain(_g.EntryPoints, e => e.Id.EndsWith(".executeasync", StringComparison.Ordinal));
        Assert.DoesNotContain(_g.EntryPoints, e => e.Id.EndsWith(".completeasync", StringComparison.Ordinal));
        Assert.DoesNotContain(_g.EntryPoints, e => e.Id == "method:staffsigninservice.signinasync");
        Assert.DoesNotContain(_g.EntryPoints, e => e.Id == "type:login/main");
        Assert.All(_g.EntryPoints, e => Assert.False(e.Incoming.Any(i => GraphBuilderStrong(i.Kind)), $"{e.Title} is called by {e.Incoming.First().From.Title}"));
        Assert.Equal(_g.EntryPoints.Count, _g.EntryPoints.Distinct().Count());
    }

    private static bool GraphBuilderStrong(EdgeKind k)
        => k is EdgeKind.Calls or EdgeKind.Then or EdgeKind.Uses or EdgeKind.Mounts or EdgeKind.Redirects or EdgeKind.Returns;

    [Fact]
    public void Item15_components_cover_every_pieces_row_and_implementations()
    {
        Assert.True(_g.Components.Count >= 17, $"only {_g.Components.Count} components");
        var titles = _g.Components.Select(c => c.Title).ToList();
        foreach (var expected in new[]
                 {
                     "index.html", "main.ts", "LoginPage.vue", "api.ts", "publicSettings.ts", "LoginController", "VitePageResolver",
                     "PublicSettingsController", "PublicAntiforgeryController", "SignInController", "SignInContractMapper", "SignInUseCase",
                     "StaffSignInService", "SignInCompletionService", "HttpAuthenticatedSessionService", "LoginService", "MemoryFailedSignInStore",
                     "SignInAuditService", "SessionValidationMiddleware",
                 })
            Assert.Contains(expected, titles);
        // Pieces rows come first and in document order.
        Assert.Equal("index.html", titles[0]);
        Assert.True(titles.IndexOf("SessionValidationMiddleware") < titles.IndexOf("MapSsoEndpointExtensions"));
        Assert.Equal(_g.Components.Count, _g.Components.Distinct().Count());

        var client = Node(_g, "type:directorycredentialclient");
        Assert.Equal(NodeKind.Client, client.Kind);
        Assert.Contains("IDirectoryCredentialClient", client.Aliases);
        var impl = Edge(client, "LdapDirectoryCredentialClient");
        Assert.Equal(EdgeKind.Implements, impl.Kind);
        Assert.Contains(impl.To, _g.Components);
        HasChild(Node(_g, "type:twofactorclient"), "DuoTwoFactorClient", EdgeKind.Implements);
        Assert.Equal(NodeKind.Middleware, Node(_g, "type:sessionvalidationmiddleware").Kind);
        Assert.Equal("Page bootstrap", Node(_g, "type:login/main").Subtitle);
        Assert.StartsWith("Sets the app base", Node(_g, "type:login/main").Description);
    }

    [Fact]
    public void Item16_robustness_of_degenerate_documents()
    {
        var parser = new MarkdownDocParser();
        var empty = parser.Parse("", "x.md");
        Assert.NotNull(empty.Root);
        Assert.Empty(empty.Sections);
        Assert.Equal("doc:x.md", empty.Root!.Id);

        var titleOnly = parser.Parse("# Only a title", "y.md");
        Assert.Equal("Only a title", titleOnly.Title);
        Assert.Empty(titleOnly.Sections);

        var tiny = parser.Parse("## a\n```text\nroot\n  └─ child\n```", "z.md");
        Assert.Single(tiny.Sections);
        var root = Node(tiny, "root");
        var child = Edge(root, "child");
        Assert.Equal(EdgeKind.Calls, child.Kind);
        Assert.Contains(root, tiny.EntryPoints);
        Assert.Equal(3, root.Sources[0].Line);
        Assert.Equal(4, child.To.Sources[0].Line);
    }
}
