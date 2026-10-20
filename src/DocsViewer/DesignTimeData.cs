using System.IO;
using DocsViewer.Core.Model;
using DocsViewer.ViewModels;

namespace DocsViewer;

/// <summary>
/// A hand-built <see cref="DocGraph"/> that mimics docs/Login-Plumbing.md, used by the <c>--sample</c> switch so the UI
/// can be exercised before the markdown parser lands. Line numbers refer to the real reference document.
/// </summary>
public static class DesignTimeData
{
    public const string DeepNodeId = "endpoint:get /api/sign-in";

    public static DocGraph CreateLoginGraph()
    {
        var path = FindReferenceDoc() ?? Path.Combine(AppContext.BaseDirectory, "Login-Plumbing.md");
        var raw = File.Exists(path) ? SafeRead(path) : FallbackMarkdown;

        var g = new DocGraph(path, raw)
        {
            Title = "Login plumbing",
            Summary = "How sign-in works end to end - the standalone Vue login page, the public and sign-in APIs, the Core sign-in use case, the session and cookie it creates, and the Duo, SSO, and change-password paths; read before debugging or changing anything on the sign-in path.",
            Area = "architecture",
        };

        DocNode N(string id, string title, NodeKind kind, string? subtitle = null, string? path = null, string? description = null,
            DocNode? owner = null, DocNode? section = null, params string[] notes)
        {
            var n = g.GetOrAdd(id, title, kind);
            if (subtitle != null) n.Subtitle = subtitle;
            if (path != null) n.Path = path;
            if (description != null) n.Description = description;
            if (owner != null) n.Owner = owner;
            if (section != null) n.Section = section;
            foreach (var note in notes) n.Notes.Add(note);
            return n;
        }

        void Src(DocNode n, DocNode section, int line, string text) => n.Sources.Add(new SourceRef(section.Title, line, text));
        void L(DocNode from, DocNode to, EdgeKind kind, string? label = null) => g.Link(from, to, kind, label);
        void Alias(DocNode n, params string[] aliases) { foreach (var a in aliases) n.Aliases.Add(a); }

        // ---- document and sections ---------------------------------------------------------------------------
        var doc = g.GetOrAdd("doc:login-plumbing.md", "Login plumbing", NodeKind.Document);
        doc.Description = g.Summary;
        g.Root = doc;

        var sPieces = N("section:the pieces at a glance", "The pieces at a glance", NodeKind.Section, "## section", null,
            "Every file or type that takes part in sign-in, with where it lives and what it does.");
        var s1 = N("section:1. loading the page", "1. Loading the page", NodeKind.Section, "## section", null,
            "From the browser asking for /Login to the Vue page being mounted.");
        var s2 = N("section:2. submitting the form", "2. Submitting the form", NodeKind.Section, "## section", null,
            "What LoginPage.submit() sends and how SignInController handles it.");
        var s3 = N("section:3. inside signinusecase", "3. Inside SignInUseCase", NodeKind.Section, "## section", null,
            "The sign-in rules: reCAPTCHA gate, staff vs. regular accounts, rejections and completion.");
        var s4 = N("section:4. signincompletionservice.completeasync", "4. SignInCompletionService.CompleteAsync", NodeKind.Section, "## section", null,
            "Role check, forced password change, session start, capacity resume, audit and landing page.");
        var s5 = N("section:5. back to the browser", "5. Back to the browser", NodeKind.Section, "## section", null,
            "How destinations become nextUrl and error codes become HTTP statuses.");
        foreach (var (s, line) in new[] { (sPieces, 10), (s1, 32), (s2, 64), (s3, 91), (s4, 117), (s5, 148) })
        {
            g.Sections.Add(s);
            L(doc, s, EdgeKind.Contains);
            Src(s, s, line, g.Lines.Count > line - 1 ? g.Lines[line - 1] : s.Title);
        }

        // ---- components (pieces table) -----------------------------------------------------------------------
        var indexHtml = N("type:login/index", "index.html", NodeKind.Html, "Login page HTML", "ClientApp/src/public/login/index.html",
            "Vite HTML entry. Built to wwwroot/vue/src/public/login/index.html with fallback text inside #login-app.", null, sPieces);
        var mainTs = N("type:login/main", "main.ts", NodeKind.TypeScript, "Page bootstrap", "ClientApp/src/public/login/main.ts",
            "Sets the app base, loads the page data, starts Google Analytics, mounts LoginPage.vue.", null, sPieces);
        var loginPage = N("type:loginpage", "LoginPage.vue", NodeKind.VueComponent, "Page component", "ClientApp/src/public/login/LoginPage.vue",
            "The form. Checks for blank fields, shows reCAPTCHA when required, calls signIn, follows nextUrl or shows the error inline.", null, sPieces);
        var apiTs = N("type:login/api", "api.ts", NodeKind.TypeScript, "Page API client", "ClientApp/src/public/login/api.ts",
            "loadLoginPage() and signIn().", null, sPieces);
        var publicSettingsTs = N("type:shared/publicsettings", "publicSettings.ts", NodeKind.TypeScript, "Shared public calls", "ClientApp/src/public/shared/publicSettings.ts",
            "GET /api/public/settings and GET /api/public/antiforgery-token.", null, sPieces);
        var loginController = N("type:logincontroller", "LoginController", NodeKind.Controller, "Page delivery", "Controllers/LoginController.cs",
            "Serves the built HTML at / and /Login.", null, sPieces);
        var viteResolver = N("type:vitepageresolver", "VitePageResolver", NodeKind.Mapper, "Page delivery", "Infrastructure/Vite/VitePageResolver.cs",
            "Reads the built page from the web root and throws if the client build is missing.", null, sPieces);
        var publicSettingsController = N("type:publicsettingscontroller", "PublicSettingsController", NodeKind.Controller, "Public settings API", "Controllers/Api/PublicSettingsController.cs",
            "Title, version, Google Analytics id, reCAPTCHA site key and script URL.", null, sPieces);
        var antiforgeryController = N("type:publicantiforgerycontroller", "PublicAntiforgeryController", NodeKind.Controller, "Anonymous antiforgery API", "Controllers/Api/PublicAntiforgeryController.cs",
            "Sets the antiforgery cookie and returns the request token.", null, sPieces);
        var signInController = N("type:signincontroller", "SignInController", NodeKind.Controller, "Sign-in API", "Controllers/Api/SignInController.cs",
            "GET /api/sign-in (page state) and POST /api/sign-in (sign in).", null, sPieces);
        var contractMapper = N("type:signincontractmapper", "SignInContractMapper", NodeKind.Mapper, "Contract mapping", "Api/SignIn/Contracts/SignInContractMapper.cs",
            "Request to SignInCommand; SignInOutcome to response; destination to URL.", null, sPieces);
        var signInUseCase = N("type:signinusecase", "SignInUseCase", NodeKind.UseCase, "Sign-in use case", "GraspANALYTICS.Core/Application/SignIn/UseCases/SignInUseCase.cs",
            "The sign-in rules.", null, sPieces);
        Alias(signInUseCase, "ISignInUseCase");
        var staffService = N("type:staffsigninservice", "StaffSignInService", NodeKind.Service, "Staff sign-in", ".../SignIn/Services/StaffSignInService.cs",
            "grasp\\ accounts: LDAP check, then Duo.", null, sPieces);
        Alias(staffService, "IStaffSignInService");
        var completionService = N("type:signincompletionservice", "SignInCompletionService", NodeKind.Service, "Completion", ".../SignIn/Services/SignInCompletionService.cs",
            "Role, forced password change, session start, capacity resume, audit, landing page. Shared with SSO.", null, sPieces);
        Alias(completionService, "ISignInCompletionService");
        var httpSession = N("type:httpauthenticatedsessionservice", "HttpAuthenticatedSessionService", NodeKind.Service, "Session start", "Core/Infrastructure/SignIn/Services/HttpAuthenticatedSessionService.cs",
            "In-memory app session, ASP.NET session key, auth cookie.", null, sPieces);
        var loginService = N("type:loginservice", "LoginService", NodeKind.Service, "Session start", "Services/LoginService.cs", null, null, sPieces);
        var sessionService = N("type:sessionservice", "SessionService", NodeKind.Service, "Session start", null, null, null, sPieces);
        var failedStore = N("type:memoryfailedsigninstore", "MemoryFailedSignInStore", NodeKind.Store, "Failed sign-in markers", "Core/Infrastructure/SignIn/Services/MemoryFailedSignInStore.cs",
            "Decides when reCAPTCHA is required.", null, sPieces, "singleton, in memory", "keeps markers for 15 minutes");
        Alias(failedStore, "IFailedSignInStore");
        var auditService = N("type:signinauditservice", "SignInAuditService", NodeKind.Service, "Audit", ".../SignIn/Services/SignInAuditService.cs",
            "Writes an AuditLog row for every outcome.", null, sPieces);
        var sessionMiddleware = N("type:sessionvalidationmiddleware", "SessionValidationMiddleware", NodeKind.Middleware, "Request check after sign-in", "Classes/SessionValidationMiddleware.cs",
            "Matches the cookie's session claim to the server session on every request.", null, sPieces);
        var userRepo = N("type:userrepository", "IUserRepository", NodeKind.Repository, "Implicit type (from the trees)", null, null, null, s3);
        Alias(userRepo, "UserRepository");
        var roleService = N("type:roleservice", "IRoleService", NodeKind.Service, "Implicit type (from the trees)", null, null, null, s4);
        var authSession = N("type:authenticatedsessionservice", "IAuthenticatedSessionService", NodeKind.Service, "Implicit type (from the trees)", null, null, null, s4);
        var directoryClient = N("type:directorycredentialclient", "IDirectoryCredentialClient", NodeKind.Client, "Implicit type (from the trees)", null, null, null, s3);
        var ldapClient = N("type:ldapdirectorycredentialclient", "LdapDirectoryCredentialClient", NodeKind.Client, "Implementation", null, null, null, s3);
        var twoFactorClient = N("type:twofactorclient", "ITwoFactorClient", NodeKind.Client, "Implicit type (from the trees)", null, null, null, s3);
        var noticeStore = N("type:signinnoticestore", "ISignInNoticeStore", NodeKind.Store, "Implicit type (from the trees)", null, null, null, s1);

        foreach (var c in new[]
                 {
                     indexHtml, mainTs, loginPage, apiTs, publicSettingsTs, loginController, viteResolver, publicSettingsController,
                     antiforgeryController, signInController, contractMapper, signInUseCase, staffService, completionService, httpSession,
                     loginService, sessionService, failedStore, auditService, sessionMiddleware, userRepo, roleService, authSession,
                     directoryClient, ldapClient, twoFactorClient, noticeStore,
                 })
        {
            g.Components.Add(c);
            L(sPieces, c, EdgeKind.Contains);
        }
        var pieceLines = new (DocNode Node, int Line)[]
        {
            (indexHtml, 14), (mainTs, 15), (loginPage, 16), (apiTs, 17), (publicSettingsTs, 18), (loginController, 19), (viteResolver, 19),
            (publicSettingsController, 20), (antiforgeryController, 21), (signInController, 22), (contractMapper, 23), (signInUseCase, 24),
            (staffService, 25), (completionService, 26), (httpSession, 27), (loginService, 27), (sessionService, 27), (failedStore, 28),
            (auditService, 29), (sessionMiddleware, 30),
        };
        foreach (var (node, line) in pieceLines) Src(node, sPieces, line, LineOrTitle(g, line, node.Title));

        L(loginController, viteResolver, EdgeKind.Uses);
        L(httpSession, loginService, EdgeKind.Calls);
        L(directoryClient, ldapClient, EdgeKind.Implements);

        // ---- section 1: loading the page -----------------------------------------------------------------------
        var browser = N("browser:get /login", "Browser GET /Login (or /)", NodeKind.Browser, "1. Loading the page", null,
            "The visitor opens /Login (or the site root). The server answers with the built login HTML.", null, s1,
            "/Login answers with status 401 for an anonymous visitor even though the page renders.",
            "UseGraspContentSecurityPolicy pre-sets 401 on any anonymous request outside / and paths containing login/.");
        Src(browser, s1, 35, "Browser GET /Login (or /)");
        var routeLogin = N("route:/login", "/Login", NodeKind.Route, "Login page route", null, "The standalone login page.", null, s1);
        Src(routeLogin, s1, 35, "Browser GET /Login (or /)");
        var routeRoot = N("route:/", "/", NodeKind.Route, "Site root", null, null, null, s1);
        L(browser, routeLogin, EdgeKind.Uses);
        L(browser, routeRoot, EdgeKind.Uses, "or");

        var index = N("method:logincontroller.index", "LoginController.Index", NodeKind.Method, "Serves the built HTML", null,
            "Redirects deeper /Login/* paths back to /Login, sends the virtual-directory root to the app root, otherwise returns the built page with Cache-Control: no-store.",
            loginController, s1, "/Login/Index or deeper → 302 to /Login (query kept)", "empty path (virtual dir) → 302 to the app root", "otherwise: Cache-Control: no-store");
        Src(index, s1, 36, "  └─ LoginController.Index");
        L(browser, index, EdgeKind.Calls);
        L(loginController, index, EdgeKind.Contains);
        L(index, routeLogin, EdgeKind.Redirects, "302 (query kept)");

        var getPagePath = N("method:vitepageresolver.getpagepath", "VitePageResolver.GetPagePath(...)", NodeKind.Method, "GetPagePath(\"src/public/login/index.html\")", null,
            "Resolves the built page under wwwroot/vue and throws if the client build is missing.", viteResolver, s1,
            "→ PhysicalFile(wwwroot/vue/src/public/login/index.html)");
        Alias(getPagePath, "VitePageResolver.GetPagePath(\"src/public/login/index.html\")");
        Src(getPagePath, s1, 40, "            VitePageResolver.GetPagePath(\"src/public/login/index.html\")");
        L(index, getPagePath, EdgeKind.Calls, "otherwise");
        L(viteResolver, getPagePath, EdgeKind.Contains);
        L(getPagePath, indexHtml, EdgeKind.Returns, "PhysicalFile");

        // Then the browser runs main.ts
        Src(mainTs, s1, 48, "Then the browser runs `main.ts`:");
        L(browser, mainTs, EdgeKind.Then);

        var setAppBase = N("method:setappbasefrombundleurl", "setAppBaseFromBundleUrl(...)", NodeKind.Method, "setAppBaseFromBundleUrl(import.meta.url)", null,
            "Works out the application base from the bundle's own address, so every appUrl(...) and requestJson(...) call works under a virtual directory.", null, s1);
        Src(setAppBase, s1, 50, "1. `setAppBaseFromBundleUrl(import.meta.url)` works out the application base ...");
        L(mainTs, setAppBase, EdgeKind.Calls);

        var loadLoginPage = N("method:login/api.loadloginpage", "loadLoginPage()", NodeKind.Method, "api.ts", null,
            "Makes three calls in parallel. If any call fails, the page still mounts with \"The login page could not be loaded. Refresh the page to try again.\"",
            apiTs, s1);
        Src(loadLoginPage, sPieces, 17, "| Page API client | `ClientApp/src/public/login/api.ts` | `loadLoginPage()` and `signIn()`. |");
        Src(loadLoginPage, s1, 51, "2. `loadLoginPage()` makes three calls in parallel:");
        L(mainTs, loadLoginPage, EdgeKind.Calls);
        L(apiTs, loadLoginPage, EdgeKind.Contains);

        var epSettings = N("endpoint:get /api/public/settings", "GET /api/public/settings", NodeKind.Endpoint, "Public settings", null,
            "Anonymous. Returns the page title, version, Google Analytics id and reCAPTCHA settings.", null, s1,
            "Returns: applicationTitle, version, googleAnalyticsId, humanVerificationSiteKey, humanVerificationScriptUrl");
        Src(epSettings, s1, 55, "| `GET /api/public/settings` | `PublicSettingsController` → ... |");
        var epAntiforgery = N("endpoint:get /api/public/antiforgery-token", "GET /api/public/antiforgery-token", NodeKind.Endpoint, "Antiforgery token", null,
            "Anonymous. Sets the antiforgery cookie and returns the request token.", null, s1, "Returns: requestToken, plus the antiforgery cookie");
        Src(epAntiforgery, s1, 56, "| `GET /api/public/antiforgery-token` | `PublicAntiforgeryController` → `IAntiforgery.GetAndStoreTokens` | ... |");
        var epSignInGet = N("endpoint:get /api/sign-in", "GET /api/sign-in", NodeKind.Endpoint, "Page state", null,
            "Anonymous. Hands the page a one-time notice (for example from a failed SSO return) and whether reCAPTCHA is required.", null, s1,
            "Returns: errorMessage (a one-time notice), humanVerificationRequired", "None of these endpoints needs a session.");
        Src(epSignInGet, s1, 57, "| `GET /api/sign-in` | `SignInController.GetPageState` → ... |");
        Src(epSignInGet, sPieces, 22, "| Sign-in API | `Controllers/Api/SignInController.cs` | `GET /api/sign-in` (page state) and `POST /api/sign-in` (sign in). |");
        L(loadLoginPage, epSettings, EdgeKind.Calls);
        L(loadLoginPage, epAntiforgery, EdgeKind.Calls);
        L(loadLoginPage, epSignInGet, EdgeKind.Calls);
        L(publicSettingsTs, epSettings, EdgeKind.Contains);
        L(publicSettingsTs, epAntiforgery, EdgeKind.Contains);

        L(epSettings, publicSettingsController, EdgeKind.Uses);
        var generalSettings = N("setting:generalsettings", "GeneralSettings", NodeKind.Setting, "Application settings", null, null, null, s1);
        var appVersion = N("type:appversionservice", "IAppVersionService", NodeKind.Service, "Implicit type (from the tables)", null, null, null, s1);
        var gaResolver = N("type:googleanalyticsidresolver", "IGoogleAnalyticsIdResolver", NodeKind.Mapper, "Implicit type (from the tables)", null, null, null, s1);
        var widgetResolver = N("type:humanverificationwidgetresolver", "IHumanVerificationWidgetResolver", NodeKind.Mapper, "Implicit type (from the tables)", null, null, null, s1);
        foreach (var dep in new[] { generalSettings, appVersion, gaResolver, widgetResolver })
        {
            L(publicSettingsController, dep, EdgeKind.Uses);
            Src(dep, s1, 55, "| `GET /api/public/settings` | `PublicSettingsController` → `GeneralSettings`, `IAppVersionService`, ... |");
        }
        foreach (var dep in new[] { appVersion, gaResolver, widgetResolver }) g.Components.Add(dep);

        L(epAntiforgery, antiforgeryController, EdgeKind.Uses);
        var getAndStore = N("method:antiforgery.getandstoretokens", "IAntiforgery.GetAndStoreTokens", NodeKind.Method, "ASP.NET Core antiforgery", null, null, null, s1);
        L(antiforgeryController, getAndStore, EdgeKind.Uses);

        var getPageState = N("method:signincontroller.getpagestate", "SignInController.GetPageState", NodeKind.Method, "GET /api/sign-in", null,
            "Builds the page state: takes the saved one-time notice and asks the failed-sign-in store whether reCAPTCHA is required for this client address.",
            signInController, s1);
        Src(getPageState, s1, 57, "| `GET /api/sign-in` | `SignInController.GetPageState` → `ISignInNoticeStore.Take()`, `IFailedSignInStore.IsHumanVerificationRequired(clientAddress)` | ... |");
        L(epSignInGet, getPageState, EdgeKind.Uses);
        L(epSignInGet, signInController, EdgeKind.Uses);
        L(signInController, getPageState, EdgeKind.Contains);
        var noticeTake = N("method:signinnoticestore.take", "ISignInNoticeStore.Take()", NodeKind.Method, "One-time notice", null,
            "Returns and removes the notice saved by a failed SSO return.", noticeStore, s1);
        var isHvRequired = N("method:failedsigninstore.ishumanverificationrequired", "IFailedSignInStore.IsHumanVerificationRequired(...)", NodeKind.Method, "MemoryFailedSignInStore", null,
            "True when a failed-sign-in marker exists for the client address (or the account).", failedStore, s1);
        Alias(isHvRequired, "IFailedSignInStore.IsHumanVerificationRequired(clientAddress)", "IFailedSignInStore.IsHumanVerificationRequired(account, address)");
        Src(isHvRequired, s1, 57, "... `IFailedSignInStore.IsHumanVerificationRequired(clientAddress)` ...");
        Src(isHvRequired, s3, 97, "  ├─ reCAPTCHA gate: IFailedSignInStore.IsHumanVerificationRequired(account, address)");
        L(getPageState, noticeTake, EdgeKind.Calls);
        L(getPageState, isHvRequired, EdgeKind.Calls);
        L(noticeStore, noticeTake, EdgeKind.Contains);
        L(failedStore, isHvRequired, EdgeKind.Contains);

        L(mainTs, loginPage, EdgeKind.Mounts);
        mainTs.Notes.Add("Sets document.title and starts Google Analytics with the id before mounting.");
        Src(loginPage, s1, 60, "3. It sets `document.title`, starts Google Analytics with the id, and mounts `LoginPage.vue` with the combined context.");

        // ---- section 2: submitting the form --------------------------------------------------------------------
        var submit = N("method:loginpage.submit", "LoginPage.submit()", NodeKind.Method, "2. Submitting the form", null,
            "Validates the form, then posts the credentials through signIn(...).", loginPage, s2,
            "Blank username or password → shows \"Invalid Username or Password\" and sends nothing.");
        Src(submit, s2, 66, "`LoginPage.submit()`:");
        L(loginPage, submit, EdgeKind.Contains);

        var signIn = N("method:login/api.signin", "signIn(...)", NodeKind.Method, "api.ts", null,
            "Sends the credentials with the antiforgery token and maps the response to nextUrl or an inline error.", apiTs, s2,
            "200 with a same-origin nextUrl → LoginPage calls location.assign(nextUrl)",
            "An error body with errorMessage → that message and humanVerificationRequired",
            "A 400 validation problem → \"Invalid Username or Password\"",
            "Anything else → \"Could not login user\"");
        Alias(signIn, "signIn()", "signIn(antiforgeryToken, { username, password, humanVerificationToken })");
        Src(signIn, sPieces, 17, "| Page API client | `ClientApp/src/public/login/api.ts` | `loadLoginPage()` and `signIn()`. |");
        Src(signIn, s2, 69, "2. Otherwise calls `signIn(antiforgeryToken, { username, password, humanVerificationToken })`, which sends:");
        Src(signIn, s5, 171, "`signIn()` in `api.ts`:");
        L(submit, signIn, EdgeKind.Calls, "otherwise");
        L(apiTs, signIn, EdgeKind.Contains);

        var epSignInPost = N("endpoint:post /api/sign-in", "POST /api/sign-in", NodeKind.Endpoint, "Sign in", null,
            "The sign-in request. Needs the antiforgery header; the body carries username, password and the reCAPTCHA token.", null, s2,
            "RequestVerificationToken: <token from step 1>", "{ \"username\": \"...\", \"password\": \"...\", \"humanVerificationToken\": \"...\" }");
        Src(epSignInPost, s2, 72, "   POST /api/sign-in");
        L(signIn, epSignInPost, EdgeKind.Calls);
        L(epSignInPost, signInController, EdgeKind.Uses);

        var signInAsync = N("method:signincontroller.signinasync", "SignInController.SignInAsync", NodeKind.Method, "POST /api/sign-in", null,
            "Maps the request to a command, runs the use case and maps the outcome back to a response and status code.", signInController, s2,
            "[ValidateAntiForgeryToken], [ApiController] model validation",
            "A missing or wrong antiforgery token is refused before the use case runs.",
            "A blank username or password fails SignInRequest validation ([Required]) with a 400 validation problem.",
            "The client address is HttpContext.Connection.RemoteIpAddress.");
        Src(signInAsync, s2, 80, "SignInController.SignInAsync            [ValidateAntiForgeryToken], [ApiController] model validation");
        L(epSignInPost, signInAsync, EdgeKind.Then, "handled by");
        L(epSignInPost, getPageState, EdgeKind.Mentions);
        L(signInController, signInAsync, EdgeKind.Contains);

        var toCommand = N("method:signincontractmapper.tocommand", "request.ToCommand(clientAddress)", NodeKind.Method, "SignInContractMapper", null,
            "SignInContractMapper → SignInCommand(Login, Password, HumanVerificationToken, ClientAddress)", contractMapper, s2);
        Src(toCommand, s2, 81, "  └─ request.ToCommand(clientAddress)   SignInContractMapper → SignInCommand(...)");
        var executeAsync = N("method:signinusecase.executeasync", "ISignInUseCase.ExecuteAsync", NodeKind.Method, "3. Inside SignInUseCase", null,
            "The sign-in rules. The account name used for the failed-sign-in markers is the login trimmed, without a trailing \\admin.", signInUseCase, s3,
            "Any unexpected exception becomes SignInFailed (\"Could not login user\"), logged as EventId 21002.", "Cancellation is not swallowed.");
        Alias(executeAsync, "ExecuteAsync(command)", "SignInUseCase.ExecuteAsync");
        Src(executeAsync, s2, 82, "  └─ ISignInUseCase.ExecuteAsync");
        Src(executeAsync, s3, 96, "ExecuteAsync(command)");
        var toResponse = N("method:signincontractmapper.toresponse", "result.ToResponse(Url)", NodeKind.Method, "SignInContractMapper", null,
            "SignInOutcome → SignInResponse { nextUrl | errorMessage, humanVerificationRequired }", contractMapper, s2);
        Src(toResponse, s2, 83, "  └─ result.ToResponse(Url)             SignInOutcome → SignInResponse { ... }");
        L(signInAsync, toCommand, EdgeKind.Calls);
        L(signInAsync, executeAsync, EdgeKind.Calls);
        L(signInAsync, toResponse, EdgeKind.Calls);
        signInAsync.Notes.Add("200, or the error status for the code (table in section 5)");
        L(contractMapper, toCommand, EdgeKind.Contains);
        L(contractMapper, toResponse, EdgeKind.Contains);
        L(signInUseCase, executeAsync, EdgeKind.Contains);
        L(s2, submit, EdgeKind.Contains);
        L(s2, epSignInPost, EdgeKind.Contains);
        L(s2, signInAsync, EdgeKind.Contains);
        L(submit, epSignInPost, EdgeKind.Then);

        // ---- section 3: inside SignInUseCase -------------------------------------------------------------------
        L(s3, executeAsync, EdgeKind.Contains);
        var hvFailed = N("outcome:humanverificationfailed", "HumanVerificationFailed", NodeKind.Outcome, "reCAPTCHA failed", null,
            "The reCAPTCHA gate failed; a marker is recorded for the address.", null, s3, "HTTP 400");
        var hvVerify = N("method:humanverificationclient.verifyasync", "IHumanVerificationClient.VerifyAsync(token)", NodeKind.Method, "ReCaptchaHumanVerificationClient", null,
            "Verifies the reCAPTCHA token server-side so the browser cannot skip it.", null, s3);
        L(executeAsync, isHvRequired, EdgeKind.Calls, "reCAPTCHA gate");
        L(executeAsync, hvVerify, EdgeKind.Calls, "reCAPTCHA gate");
        L(hvVerify, hvFailed, EdgeKind.Returns, "fails");
        Src(hvFailed, s3, 99, "  │     → HumanVerificationFailed (marker recorded for the address)");

        var ucSignInAsync = N("method:signinusecase.signinasync", "SignInAsync", NodeKind.Method, "SignInUseCase", null,
            "Chooses between the staff path (grasp\\ accounts) and the regular password path.", signInUseCase, s3);
        Src(ucSignInAsync, s3, 100, "  ├─ SignInAsync");
        L(executeAsync, ucSignInAsync, EdgeKind.Calls);
        L(signInUseCase, ucSignInAsync, EdgeKind.Contains);
        var noDestination = N("branch:no destination", "no destination", NodeKind.Branch, "ExecuteAsync", null,
            "Record markers for account and address, return with humanVerificationRequired.", null, s3);
        var verified = N("branch:destination with verified credentials", "destination with verified credentials", NodeKind.Branch, "ExecuteAsync", null,
            "Clear the markers.", null, s3);
        L(executeAsync, noDestination, EdgeKind.Calls);
        L(executeAsync, verified, EdgeKind.Calls);
        var recordMarkers = N("method:failedsigninstore.record", "IFailedSignInStore.Record(...)", NodeKind.Method, "MemoryFailedSignInStore", null, null, failedStore, s3);
        var clearMarkers = N("method:failedsigninstore.clear", "IFailedSignInStore.Clear(...)", NodeKind.Method, "MemoryFailedSignInStore", null, null, failedStore, s3);
        L(noDestination, recordMarkers, EdgeKind.Calls);
        L(verified, clearMarkers, EdgeKind.Calls);
        L(failedStore, recordMarkers, EdgeKind.Contains);
        L(failedStore, clearMarkers, EdgeKind.Contains);

        var staffSignIn = N("method:staffsigninservice.signinasync", "IStaffSignInService.SignInAsync", NodeKind.Method, "6a. Staff accounts", null,
            "grasp\\ accounts: reads the Duo and directory settings, checks LDAP, then signs a Duo request.", staffService, s3,
            "Each failure writes an audit row and returns its own error code (EventIds 21031-21036).");
        Alias(staffSignIn, "StaffSignInService.SignInAsync");
        Src(staffSignIn, s3, 101, "  │   ├─ login starts with \"grasp\\\"  → IStaffSignInService.SignInAsync        (section 6a)");
        L(ucSignInAsync, staffSignIn, EdgeKind.Calls, "login starts with \"grasp\\\"");
        L(staffService, staffSignIn, EdgeKind.Contains);
        var getGlobal = N("method:settingsrepository.getglobalasync", "ISettingsRepository.GetGlobalAsync", NodeKind.Method, "Duo keys and directory settings", null, null, null, s3,
            "Duo keys (DUO_TWOFACT_IKEY, SKEY, AKEY, SIGNATURE_KEY) and directory settings (ActiveDirectoryGroups, AuthenticationDomain, LDAPServers)");
        var decrypt = N("method:encryptionservice.decrypt", "IEncryptionService.Decrypt", NodeKind.Method, "each Duo key", null, null, null, s3);
        var validate = N("method:directorycredentialclient.validate", "IDirectoryCredentialClient.Validate(ldapServers, login, password)", NodeKind.Method, "LdapDirectoryCredentialClient", null, null, directoryClient, s3, "LdapDirectoryCredentialClient");
        var signRequest = N("method:twofactorclient.signrequest", "ITwoFactorClient.SignRequest(keys, user)", NodeKind.Method, "DuoTwoFactorClient", null, null, twoFactorClient, s3, "DuoTwoFactorClient");
        var twoFactorDest = N("destination:twofactordestination", "TwoFactorDestination", NodeKind.Destination, "browser goes to /Duo", null, null, null, s3);
        var routeDuo = N("route:/duo", "/Duo", NodeKind.Route, "Duo frame (DuoController.Index)", null, null, null, s3);
        L(staffSignIn, getGlobal, EdgeKind.Calls);
        L(staffSignIn, decrypt, EdgeKind.Calls);
        L(staffSignIn, validate, EdgeKind.Calls);
        L(staffSignIn, signRequest, EdgeKind.Calls);
        L(staffSignIn, twoFactorDest, EdgeKind.Returns);
        L(twoFactorDest, routeDuo, EdgeKind.Redirects, "?requestSig=...&user=...");
        L(directoryClient, validate, EdgeKind.Contains);
        L(twoFactorClient, signRequest, EdgeKind.Contains);

        var otherwise = N("branch:otherwise", "otherwise", NodeKind.Branch, "regular accounts", null,
            "Password sign-in for non-staff accounts.", null, s3, "strip a trailing \"\\admin\" → preferAdminHome = true");
        Src(otherwise, s3, 102, "  │   └─ otherwise");
        L(ucSignInAsync, otherwise, EdgeKind.Calls);

        var getActiveUser = N("method:userrepository.getactiveuserbyloginasync", "IUserRepository.GetActiveUserByLoginAsync", NodeKind.Method, "GetActiveUserByLoginAsync(login)", null,
            "Looks the account up by login; unknown logins are rejected.", userRepo, s3);
        Alias(getActiveUser, "IUserRepository.GetActiveUserByLoginAsync(login)");
        Src(getActiveUser, s3, 104, "  │       ├─ IUserRepository.GetActiveUserByLoginAsync(login)");
        L(otherwise, getActiveUser, EdgeKind.Calls);
        L(userRepo, getActiveUser, EdgeKind.Contains);
        var rejectUnknown = N("method:signinusecase.rejectunknownloginasync", "RejectUnknownLoginAsync", NodeKind.Method, "SignInUseCase", null, null, signInUseCase, s3);
        var invalidCredentials = N("outcome:invalidcredentials", "InvalidCredentials", NodeKind.Outcome, "Rejected", null,
            "Unknown login, no password set, or the password did not match.", null, s3, "HTTP 401");
        Src(invalidCredentials, s3, 105, "  │       │     none            → RejectUnknownLoginAsync → InvalidCredentials");
        Src(invalidCredentials, s5, 166, "| `InvalidCredentials`, `DomainUserInvalid` | 401 |");
        L(getActiveUser, rejectUnknown, EdgeKind.Calls, "none");
        L(rejectUnknown, invalidCredentials, EdgeKind.Returns);
        L(signInUseCase, rejectUnknown, EdgeKind.Contains);

        var idpDest = N("destination:identityproviderdestination", "IdentityProviderDestination", NodeKind.Destination, "SSO-only users", null,
            "The user must sign in through the identity provider.", null, s3);
        Alias(idpDest, "IdentityProviderDestination(tenantHint)");
        Src(idpDest, s3, 106, "  │       ├─ user.IsSsoOnly     → IdentityProviderDestination(tenantHint)     (section 6b)");
        L(otherwise, idpDest, EdgeKind.Returns, "user.IsSsoOnly");
        var rejectCredentials = N("method:signinusecase.rejectcredentialsasync", "RejectCredentialsAsync", NodeKind.Method, "SignInUseCase", null, null, signInUseCase, s3);
        L(otherwise, rejectCredentials, EdgeKind.Calls, "no password set");
        L(rejectCredentials, invalidCredentials, EdgeKind.Returns);
        L(signInUseCase, rejectCredentials, EdgeKind.Contains);
        var hashEquals = N("method:hashingprovider.equals", "IHashingProvider.Equals(password, hash)", NodeKind.Method, "password check", null, null, null, s3);
        L(otherwise, hashEquals, EdgeKind.Calls);
        L(hashEquals, rejectCredentials, EdgeKind.Calls, "fails");

        var completeAsync = N("method:signincompletionservice.completeasync", "ISignInCompletionService.CompleteAsync", NodeKind.Method, "4. SignInCompletionService.CompleteAsync", null,
            "Shared by the password sign-in and the SSO return: role check, forced password change, session start, capacity resume, audit and landing page.",
            completionService, s4,
            "If anything after the session start fails, IAuthenticatedSessionService.EndAsync ends the session again, so a failed sign-in never leaves a live session behind.");
        Alias(completeAsync, "CompleteAsync(user, enteredLogin, attempt, options)", "SignInCompletionService.CompleteAsync", "ISignInCompletionService.CompleteAsync(user, PasswordSignIn(preferAdminHome))");
        Src(completeAsync, s3, 110, "  │       └─ ISignInCompletionService.CompleteAsync(user, PasswordSignIn(preferAdminHome))");
        Src(completeAsync, s4, 122, "CompleteAsync(user, enteredLogin, attempt, options)");
        L(otherwise, completeAsync, EdgeKind.Calls);
        L(completionService, completeAsync, EdgeKind.Contains);
        L(s4, completeAsync, EdgeKind.Contains);

        // ---- section 4: completion -----------------------------------------------------------------------------
        var getRole = N("method:roleservice.getrolebyidasync", "IRoleService.GetRoleByIdAsync", NodeKind.Method, "GetRoleByIdAsync(user.RoleID)", null, null, roleService, s4);
        var invalidRole = N("outcome:invalidrole", "InvalidRole", NodeKind.Outcome, "Rejected", null, null, null, s4, "HTTP 403");
        L(completeAsync, getRole, EdgeKind.Calls);
        L(getRole, invalidRole, EdgeKind.Returns, "none → audit RoleNotFound");
        L(roleService, getRole, EdgeKind.Contains);
        var pwdChangeDest = N("destination:passwordchangedestination", "PasswordChangeDestination", NodeKind.Destination, "Forced password change", null, null, null, s4);
        L(completeAsync, pwdChangeDest, EdgeKind.Returns, "options.EnforcePasswordChange && user.ForcePasswordReset");
        var routeChangePwd = N("route:/changepassword", "/ChangePassword", NodeKind.Route, "ChangePasswordController", null, null, null, s5);
        L(pwdChangeDest, routeChangePwd, EdgeKind.Redirects, "?u=<login>&t=<password token>");

        var startSession = N("method:signincompletionservice.startsessionasync", "StartSessionAsync", NodeKind.Method, "SignInCompletionService", null,
            "Starts the authenticated session, resumes the capacity when allowed, audits and picks the landing page.", completionService, s4);
        Src(startSession, s4, 127, "  └─ StartSessionAsync");
        L(completeAsync, startSession, EdgeKind.Calls);
        L(completionService, startSession, EdgeKind.Contains);
        var startAsync = N("method:authenticatedsessionservice.startasync", "IAuthenticatedSessionService.StartAsync", NodeKind.Method, "StartAsync(user, role)", null,
            "HttpAuthenticatedSessionService builds the principal and signs the cookie in.", authSession, s4, "HttpAuthenticatedSessionService");
        L(startSession, startAsync, EdgeKind.Calls);
        L(authSession, startAsync, EdgeKind.Contains);
        L(authSession, httpSession, EdgeKind.Implements);
        var loginUser = N("method:loginservice.loginuser", "LoginService.LoginUser(...)", NodeKind.Method, "LoginService", null,
            "Builds a ClaimsPrincipal (Name, Role, a new session-id claim), initialises the app session, stores the session key and signs the cookie in.",
            loginService, s4, "HttpContext.Session[SessionIdentity.SessionKey] = the session id", "HttpContext.SignInAsync(cookie scheme, principal) → auth cookie");
        L(startAsync, loginUser, EdgeKind.Calls);
        L(loginService, loginUser, EdgeKind.Contains);
        var initSession = N("method:sessionservice.initsession", "SessionService.InitSession(...)", NodeKind.Method, "SessionService", null,
            "Builds the AppSession (branding, filters, agency, admin, SMTP, Foundry settings) and caches it by login.", sessionService, s4);
        var simultaneous = N("outcome:simultaneoussignin", "SimultaneousSignIn", NodeKind.Outcome, "Rejected", null,
            "A cached session for this login already exists.", null, s4, "HTTP 409");
        L(loginUser, initSession, EdgeKind.Calls);
        L(initSession, simultaneous, EdgeKind.Returns, "a cached session for this login already exists");
        L(sessionService, initSession, EdgeKind.Contains);
        var resume = N("method:capacitycontrolservice.resumeifallowedasync", "ICapacityControlService.ResumeIfAllowedAsync", NodeKind.Method, "options.ResumeCapacity", null, null, null, s4,
            "setting AppRegAllowControlCapacity is true → IPowerBICapacityClient.Resume", "failure → audit → CapacityControlFailed");
        L(startSession, resume, EdgeKind.Calls, "password sign-in only");
        var updateUser = N("method:userrepository.updateuserasync", "IUserRepository.UpdateUserAsync", NodeKind.Method, "audit SignedIn, set user.LastLoginDate", null, null, userRepo, s4);
        L(startSession, updateUser, EdgeKind.Calls);
        L(userRepo, updateUser, EdgeKind.Contains);
        var adminHome = N("destination:adminhomedestination", "AdminHomeDestination", NodeKind.Destination, "\"\\admin\" typed and role is Agency Admin", null, null, null, s4);
        var reportsHome = N("destination:reportshomedestination", "ReportsHomeDestination", NodeKind.Destination, "otherwise", null, null, null, s4);
        L(startSession, adminHome, EdgeKind.Returns, "\"\\admin\" typed and Agency Admin");
        L(startSession, reportsHome, EdgeKind.Returns, "otherwise");

        // ---- section 5: back to the browser --------------------------------------------------------------------
        var toUrl = N("method:signincontractmapper.tourl", "SignInContractMapper.ToUrl", NodeKind.Method, "destination → nextUrl", null,
            "Turns the destination into nextUrl.", contractMapper, s5);
        Src(toUrl, s5, 150, "`SignInContractMapper.ToUrl` turns the destination into `nextUrl`:");
        L(contractMapper, toUrl, EdgeKind.Contains);
        L(s5, toUrl, EdgeKind.Contains);
        var routeHome = N("route:/home", "/Home", NodeKind.Route, "Reports home", null, null, null, s5);
        var routeAdmin = N("route:/admin", "/Admin", NodeKind.Route, "Admin home", null, null, null, s5);
        var routeAuthStart = N("route:/auth/start", "/auth/start", NodeKind.Route, "SSO round trip (section 6b)", null, null, null, s5);
        L(reportsHome, routeHome, EdgeKind.Redirects);
        L(adminHome, routeAdmin, EdgeKind.Redirects);
        L(idpDest, routeAuthStart, EdgeKind.Redirects, "plus ?tenant= when the user has a tenant hint");
        foreach (var d in new[] { reportsHome, adminHome, idpDest, pwdChangeDest, twoFactorDest })
        {
            L(toUrl, d, EdgeKind.Uses);
            Src(d, s5, 152 + Array.IndexOf(new[] { reportsHome, adminHome, idpDest, pwdChangeDest, twoFactorDest }, d) + 2, $"| `{d.Title}` | ... |");
        }
        var startAuth = N("method:mapssoendpointextensions.startauth", "MapSsoEndpointExtensions.StartAuth", NodeKind.Method, "/auth/start", null, null, null, s5);
        L(routeAuthStart, startAuth, EdgeKind.Calls);
        var oauthUseCase = N("type:oauthauthorizationusecase", "IOAuthAuthorizationUseCase", NodeKind.UseCase, "SSO", null, null, null, s5);
        L(startAuth, oauthUseCase, EdgeKind.Uses);
        oauthUseCase.Notes.Add("→ 302 to the identity provider");
        var domainInvalid = N("outcome:domainuserinvalid", "DomainUserInvalid", NodeKind.Outcome, "Rejected", null, null, null, s5, "HTTP 401");
        Src(domainInvalid, s5, 166, "| `InvalidCredentials`, `DomainUserInvalid` | 401 |");
        Src(simultaneous, s5, 168, "| `SimultaneousSignIn` | 409 |");
        Src(invalidRole, s5, 167, "| `InvalidRole` | 403 |");
        Src(hvFailed, s5, 165, "| `HumanVerificationFailed` | 400 |");
        L(signInController, invalidCredentials, EdgeKind.Mentions);
        L(signInController, simultaneous, EdgeKind.Mentions);

        // ---- section membership, entry points --------------------------------------------------------------------
        L(s1, browser, EdgeKind.Contains);
        L(s1, mainTs, EdgeKind.Contains);
        L(s1, loadLoginPage, EdgeKind.Contains);

        g.EntryPoints.Add(browser);
        g.EntryPoints.Add(submit);
        g.EntryPoints.Add(toUrl);
        browser.Subtitle = s1.Title;
        submit.Subtitle = s2.Title;
        toUrl.Subtitle = s5.Title;

        return g;
    }

    private static string LineOrTitle(DocGraph g, int line, string fallback)
        => line >= 1 && line <= g.Lines.Count && !string.IsNullOrWhiteSpace(g.Lines[line - 1]) ? g.Lines[line - 1] : fallback;

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return FallbackMarkdown; }
    }

    /// <summary>Finds docs/Login-Plumbing.md by walking up from the exe directory.</summary>
    private static string? FindReferenceDoc()
    {
        var docs = MainViewModel.FindDocsFolderAbove(AppContext.BaseDirectory);
        if (docs == null) return null;
        var candidate = Path.Combine(docs, "Login-Plumbing.md");
        return File.Exists(candidate) ? candidate : Directory.EnumerateFiles(docs, "*.md").FirstOrDefault();
    }

    private const string FallbackMarkdown = """
        ---
        summary: Sample document (the reference markdown was not found next to the application).
        area: architecture
        ---

        # Login plumbing

        ## The pieces at a glance

        | Piece | Where | What it does |
        |---|---|---|
        | Page bootstrap | `ClientApp/src/public/login/main.ts` | Sets the app base, loads the page data, mounts `LoginPage.vue`. |

        ## 1. Loading the page

        ```text
        Browser GET /Login (or /)
          └─ LoginController.Index
               └─ VitePageResolver.GetPagePath("src/public/login/index.html")
        ```
        """;
}
