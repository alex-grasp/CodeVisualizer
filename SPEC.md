# DocsViewer – design spec

A .NET 10 WPF desktop app that reads AI-generated "plumbing" markdown docs (see `docs/Login-Plumbing.md`)
and shows them as a drill-down card interface: an entry card such as **Browser GET /Login**, then cards for the
files it uses, then the functions those run, the endpoints they call, the use cases / services behind each endpoint,
down to the repository level. Clicking a component (e.g. `LoginPage.vue`) shows its functions and the endpoints they call.

Solution layout (already scaffolded, builds):

```
DocsViewer.slnx
nuget.config                      nuget.org only (the user-level config has a private feed that needs auth)
src/DocsViewer.Core/              class library, net10.0, references Markdig 1.4.0
  Model/                          NodeKind, EdgeKind, DocNode, DocEdge, SourceRef, DocGraph   ← the shared contract (do not change shape without need)
  Parsing/IDocParser.cs           interface
  Parsing/MarkdownDocParser.cs    THE PARSER (stub today; to be implemented)
  DocLibrary.cs                   loads a folder of *.md into DocGraphs
  GraphDump.cs                    text rendering of a graph for debugging / --dump
src/DocsViewer/                   WPF app, net10.0-windows, references Core
tests/DocsViewer.Core.Tests/      xunit, references Core
docs/Login-Plumbing.md            the reference document everything is validated against
```

Build: `dotnet build DocsViewer.slnx`. Test: `dotnet test tests/DocsViewer.Core.Tests`. Run: `dotnet run --project src/DocsViewer`.

---

## 1. Graph model (contract)

- `DocNode` – one card. `Id` is a canonical lower-case key; nodes are **deduplicated by Id** so the same thing mentioned
  in several places becomes one node whose children are merged. `Title` is the display spelling, `Subtitle` a secondary
  line (path / signature / section), `Path` a source path when known, `Description` one paragraph, `Notes` extra lines,
  `Aliases` other spellings, `Sources` where in the markdown it appears, `Owner` the owning type/file for members,
  `Section` the section where first defined, `Outgoing`/`Incoming` edges.
- `DocEdge` – `From`, `To`, `Kind`, `Order` (document order), optional `Label`, optional `Source`.
- `EdgeKind` (display priority order): `Calls, Then, Uses, Mounts, Redirects, Returns, Contains, Implements, Mentions`.
  `Mentions` is weak and hidden from drill-down by default.
- `DocGraph` – `Title/Summary/Area`, `Root`, `EntryPoints` (Flow view roots), `Components` (Components view roots),
  `Sections`, `Nodes`, `Edges`, `Find`, `GetOrAdd`, `Link` (dedups same from/to/kind, ignores self edges), `Search`.
- `DocNode.ChildrenInOrder()` – the edges a column shows, grouped by kind priority then document order.

### Canonical ids

| Thing | Id | Example |
|---|---|---|
| Document | `doc:<file name>` | `doc:login-plumbing.md` |
| Section | `section:<heading text lower>` | `section:1. loading the page` |
| Type / file / component | `type:<simple name lower>` | `type:signincontroller`, `type:loginpage`, `type:main` (see below) |
| Member (method / function) | `type:<owner>.<method lower>` → `method:<owner>.<method>` | `method:signincontroller.signinasync`, `method:loginpage.submit`, `method:api.signin` |
| Function with unknown owner | `method:<name>` | `method:loadloginpage` (later re-homed to its owner when found) |
| Endpoint | `endpoint:<VERB> <path lower>` | `endpoint:get /api/sign-in` |
| Route | `route:<path lower, no query>` | `route:/login`, `route:/auth/start` |
| Browser entry | `browser:<verb> <path lower>` | `browser:get /login` |
| Destination / outcome / setting / concept | `<kind>:<name lower>` | `destination:reportshomedestination`, `outcome:invalidcredentials` |

Owner normalisation rules:

- Strip a leading `I` when the second character is upper-case (`ISignInUseCase` → `SignInUseCase`); keep the original
  spelling in `Aliases` and mark the interface relationship with `Implements` only if both spellings appear.
- A file path maps to its file name without extension: `Controllers/Api/SignInController.cs` → `signincontroller`,
  `ClientApp/src/public/login/LoginPage.vue` → `loginpage`. For generic TS/JS file names (`main`, `index`, `api`,
  `shared`, `utils`, `types`) use `<parent folder>/<name>` → `type:login/main`, `type:login/api`, `type:login/index`.
  A bare mention such as `main.ts` or `api.ts` resolves to the unique component in the doc whose path ends with it.
- A `Type.Method(args)` reference strips the argument list for the id and keeps the full text as alias/subtitle.
- A tree root with no owner (`ExecuteAsync(command)`, `CompleteAsync(user, …)`) takes its owner from the nearest
  enclosing heading's back-ticked identifier: `## 3. Inside \`SignInUseCase\`` → owner `SignInUseCase`;
  `## 4. \`SignInCompletionService.CompleteAsync\`` → owner `SignInCompletionService` (and the heading itself names the method).
- A function `name()` with no owner, mentioned in a component's description (`loadLoginPage()` and `signIn()` in the
  `api.ts` row) is a member of that component. Later mentions of `loadLoginPage()` resolve to the same node.

### NodeKind inference (by name, then by context)

| Pattern | Kind |
|---|---|
| `*.vue` | VueComponent |
| `*.ts`, `*.js` | TypeScript |
| `*.html`, `*.cshtml` | Html |
| `*Controller` | Controller |
| `*Middleware` | Middleware |
| `*UseCase` | UseCase |
| `*Service` | Service |
| `*Repository` | Repository |
| `*Client` | Client |
| `*Store` | Store |
| `*Mapper`, `*Resolver`, `*Extensions`, `*Contracts`, `*Provider` | Mapper |
| `*Destination` | Destination |
| `GET|POST|PUT|PATCH|DELETE /…` | Endpoint |
| `Browser …` | Browser |
| `/Path…` (leading slash, no spaces before `?`) | Route |
| `Owner.Method(...)`, `Owner.Method`, `name()` | Method |
| PascalCase word used as an outcome on the right of `→` (`InvalidCredentials`, `SimultaneousSignIn`, `HumanVerificationFailed`) | Outcome |
| `ALL_CAPS_WITH_UNDERSCORES`, `*Setting*` | Setting |
| a tree line that is not identifier-like ("no password set", "otherwise", "login starts with …") | Branch |
| anything else back-ticked | Concept |

Interfaces (`IFoo…`) take the kind of their stripped name (`IUserRepository` → Repository).

---

## 2. What the parser reads (Markdig AST, not regex over the whole text)

Use Markdig with `UsePipeTables()`, `UseYamlFrontMatter()`, `UseAutoIdentifiers()` off is fine. Every block carries
`Line` (0-based) → `SourceRef.Line = Line + 1`. Walk the document in order, keeping the **current section**.

### 2.1 Front matter and title
`summary:` → `DocGraph.Summary`, `area:` → `Area`. The first `# H1` → `Title`. Create the Document root node
(`doc:`), `Contains` every `##`/`###` section in order; sections `Contains` their items; `###` sections are also
`Contains` children of their parent `##`.

### 2.2 Headings `##` / `###`
Section node, kind Section, title = heading text without back-ticks. If the heading has a back-ticked identifier it
names the **subject** of the section (owner context, and if it is `Type.Method` it IS the method whose tree follows).

### 2.3 The pieces table (`Piece | Where | What it does`, header match is case-insensitive and tolerant)
Each row → a **component**, appended to `graph.Components` in order:
- `Where` can hold several paths/identifiers joined by ` + ` or ` → ` (`Controllers/LoginController.cs` +
  `Infrastructure/Vite/VitePageResolver.cs`; `HttpAuthenticatedSessionService.cs → Services/LoginService.cs →
  SessionService.InitSession`). Create one node per path/identifier. The first is the primary: `Title` = `Piece`
  cell text? No – `Title` = the simple type/file name (e.g. `LoginController`, `main.ts`, `LoginPage.vue`), `Subtitle`
  = the Piece cell ("Page bootstrap"), `Path` = the path. Extra entries get `Uses` (for `+`) or `Calls` (for `→`)
  edges from the previous one, in order, and also appear in `Components`.
- `.../SignIn/Services/X.cs` – strip the leading `.../` when deriving the name; keep the path as given.
- `What it does` → `Description` of the primary. Back-ticked members in it (`loadLoginPage()`, `signIn()`,
  `GET /api/public/settings`) become `Contains` children (functions → Method nodes with `Owner` = component; endpoints
  → Endpoint nodes linked `Uses` component→endpoint? No: component `Contains` endpoint for a TS client module, and for
  a controller row `GET /api/sign-in (page state)` → endpoint `Uses` the controller).
- Keep **every row**, including infrastructure like `SessionValidationMiddleware`.

### 2.4 ```text call trees
A fenced block whose lines use `├─`, `└─`, `│` (or whose first line is a root followed by indented lines) is a tree.
- Depth = column of the connector (`├─`/`└─`) ; lines without a connector but indented deeper than the previous node
  are **continuations** of that node: if a continuation starts with an identifier call (`VitePageResolver.GetPagePath(...)`)
  it becomes a `Calls` child of the previous node; a continuation starting with `→` is a result (`Returns`/`Redirects`
  edge if the target is identifier/route-like, otherwise a note). Other continuations become notes.
- The root line (column 0) is the tree's **root flow item**. Right-hand annotations separated by 3+ spaces
  (`SignInController.SignInAsync            [ValidateAntiForgeryToken], [ApiController] model validation`,
  `IDirectoryCredentialClient.Validate(ldapServers, login, password)   LdapDirectoryCredentialClient`) are notes; when the
  annotation is a single PascalCase type name it is the **implementation** → `Implements` edge from the interface's
  type node to that type, and the type is added to `Components` if new.
- A line of the form `X → Y`: X is the node (Branch if not identifier-like); Y becomes a `Returns` child when it is a
  PascalCase outcome/destination (`InvalidCredentials`, `IdentityProviderDestination(tenantHint)`), a `Redirects` child
  when it is `302 to /Login`, `/Duo`, a route, or a `*Destination`; otherwise a note on X. `(section 6a)` style suffixes
  become the edge `Label` and additionally link X to that section's first flow item with `Calls` if X's id does not
  already resolve to it.
- A line that is identifier-like (`Type.Method(...)`, `IType.Method`, `name(...)`) is a Method node → `Calls` edge from
  its parent. A `GET|POST /…` line is an Endpoint. A `/path` line is a Route. `Browser GET /Login (or /)` is a Browser
  node that also `Redirects`→? No: Browser node `Uses` the Route node `/Login`.
- A Branch line (`login starts with "grasp\"`, `otherwise`, `no password set`, `none`) becomes a Branch node **only
  when it has children**; a leaf branch with `→ Outcome` collapses into an edge from the parent to the outcome with the
  branch text as `Label` (so `none → RejectUnknownLoginAsync → InvalidCredentials` yields parent `-Calls (none)->
  RejectUnknownLoginAsync -Returns-> InvalidCredentials`).
- Keep the full line text in `Notes`/`Subtitle` so nothing is lost.
- Pipeline-style trees without connectors (section 6b: `/auth/start?tenant=...   MapSsoEndpointExtensions.StartAuth →
  IOAuthAuthorizationUseCase → 302 to the identity provider`) : first token is the node (Route), the rest split on `→`
  become a `Calls`/`Uses` chain ending in `Redirects`/notes. Lines indented under it continue it.
- Section 8's tree (`UseAuthentication`, `UseSession`, `SessionValidationMiddleware` at column 0 with children)
  has several roots: each column-0 line is a flow item, chained with `Then`.

### 2.5 Call tables (`Call | Server | Returns`)
Row → Endpoint node from `Call`; `Server` cell `A → B, C, D` → endpoint `Uses` A (Method or type), A `Uses` B, C, D;
`Returns` cell → a note `Returns: …` on the endpoint (fields are not nodes). If the table sits inside a list item
(section 1, step 2) the endpoints are `Calls` children of that step's node (`loadLoginPage()`).

### 2.6 Other tables
- `Destination | nextUrl` → Destination nodes `Redirects` Route nodes (strip query, keep it in the label).
- `Error code | Status` → Outcome nodes with note `HTTP <status>`; a cell with several codes creates several nodes.
- Generic table: the first cell is a node when identifier-like; the other cells are notes.

### 2.7 Step lists
A paragraph that ends with `:` and contains exactly one back-ticked call/identifier (`\`LoginPage.submit()\`:`,
`Then the browser runs \`main.ts\`:`, `\`signIn()\` in \`api.ts\`:` – two identifiers: the first is the subject, `in X`
gives the owner) followed by an ordered or bullet list → the subject is a **flow item** of the section; each list item:
- first call-like back-ticked identifier → `Calls` child (its item text → `Description`/note of that child);
  further identifiers in the item → `Uses` children of that child; a nested table → handled per 2.5 with the child as parent;
  a nested ```text block → its root is a `Calls`/`Then` child.
- "mounts `LoginPage.vue`" → `Mounts` edge. "starts Google Analytics" etc. → note.
- item without identifiers → note on the subject.

`On the server:` followed by a tree → the tree root is a `Then` child of the previous flow item in the section
(the `POST /api/sign-in` endpoint) with label `handled by`.

### 2.8 Prose bullets
Bullets right after a tree/table/list are notes on the section's current flow item, except a bullet that starts with a
back-ticked identifier already in the graph → `Description` (first time) / `Notes` of that node
(`- \`VitePageResolver\` reads the file…`, `- \`/Login\` answers with status **401**…`).
Inline code in prose that resolves to an existing node adds a `Mentions` edge from the current flow item and a `SourceRef`.

### 2.9 Flow items, `Then` chaining and entry points
Within a section, flow items (tree roots, step-list subjects, table-of-calls parents) are `Contains` children of the
section and are chained in order with `Then` edges (`Browser GET /Login` → then → `main.ts`). After the whole document
is parsed, `EntryPoints` = flow items with **no incoming** `Calls/Then/Uses/Mounts/Redirects/Returns` edge, in document
order, each with `Subtitle` = its section title. For the reference doc that must include `Browser GET /Login (or /)`
and `LoginPage.submit()` and must **not** include `ExecuteAsync`, `CompleteAsync`, or `StaffSignInService.SignInAsync`
(they are reached from the sign-in trees).

### 2.10 Components view extras
- A Method node whose owner is a component is a `Contains` child of it (so `LoginPage.vue` → `submit()` → `signIn(...)`
  → `POST /api/sign-in`; `SignInController` → `SignInAsync`, `GetPageState`).
- Implicit types met only in trees (`MapSsoEndpointExtensions`, `DuoController`, `LoginService`) are created as type
  nodes and appended to `Components` after the pieces-table rows, grouped last. Methods' `Path`/`Subtitle` fall back
  to the owner's path.
- Endpoints also get `Uses` edges to the controller method/type that serves them when the doc says so.

### 2.11 Robustness
Never throw. Empty text, no front matter, no tables, headings only, trees with tabs, CRLF, and bullets with weird
nesting must yield a graph (possibly only Document + Sections). Line numbers must be right (tests check a few).

---

## 3. Required drill paths (acceptance tests against docs/Login-Plumbing.md)

Each is a chain of **titles** (match by node Id resolution, not exact title text) along edges whose kind is not `Mentions`:

1. Entry `Browser GET /Login (or /)` → `LoginController.Index` → `VitePageResolver.GetPagePath(...)`.
2. `Browser GET /Login (or /)` → (Then) `main.ts` → `setAppBaseFromBundleUrl(...)`, `loadLoginPage()`, (Mounts) `LoginPage.vue`.
3. `loadLoginPage()` → `GET /api/public/settings`, `GET /api/public/antiforgery-token`, `GET /api/sign-in`.
4. `GET /api/public/settings` → `PublicSettingsController` → `GeneralSettings`, `IAppVersionService`, `IGoogleAnalyticsIdResolver`, `IHumanVerificationWidgetResolver`.
5. `GET /api/sign-in` → `SignInController.GetPageState` → `ISignInNoticeStore.Take()`, `IFailedSignInStore.IsHumanVerificationRequired(...)`.
6. Entry `LoginPage.submit()` → `signIn(...)` → `POST /api/sign-in` → `SignInController.SignInAsync` → `ISignInUseCase.ExecuteAsync` → `SignInAsync` → `IUserRepository.GetActiveUserByLoginAsync` (and `ISignInCompletionService.CompleteAsync`).
7. `ISignInUseCase.ExecuteAsync` and `ExecuteAsync(command)` (section 3 root) are the **same node** (`method:signinusecase.executeasync`), owner type `SignInUseCase` with path `GraspANALYTICS.Core/Application/SignIn/UseCases/SignInUseCase.cs`.
8. `ISignInCompletionService.CompleteAsync` → `IRoleService.GetRoleByIdAsync` and → `StartSessionAsync` → `IAuthenticatedSessionService.StartAsync` → `LoginService.LoginUser` → `SessionService.InitSession` ; and `StartSessionAsync` → … → `IUserRepository.UpdateUserAsync`.
9. `IStaffSignInService.SignInAsync` (section 3) resolves to section 6a's root and has children `ISettingsRepository.GetGlobalAsync`, `IDirectoryCredentialClient.Validate`, `ITwoFactorClient.SignRequest`, `TwoFactorDestination`.
10. Components view: `LoginPage.vue` (kind VueComponent, path `ClientApp/src/public/login/LoginPage.vue`) contains `submit()`; `api.ts` contains `loadLoginPage()` and `signIn()`; `SignInController` (Controller) contains `SignInAsync` and `GetPageState` and is used by `GET /api/sign-in` and `POST /api/sign-in`.
11. `ReportsHomeDestination` → Redirects → `/Home`; `IdentityProviderDestination` → `/auth/start`; `/auth/start` has children from section 6b (`MapSsoEndpointExtensions.StartAuth`).
12. Outcome `InvalidCredentials` has note containing `401`; `SimultaneousSignIn` → `409`.
13. `graph.Sections.Count == 13` (9 `##` + `###` 6a/6b/6c + "The pieces at a glance"), `Title == "Login plumbing"`, `Summary` starts with "How sign-in works".
14. `EntryPoints` contains `Browser GET /Login (or /)` first; does not contain `ExecuteAsync`/`CompleteAsync`.
15. `Components` has ≥ 17 entries (every pieces-table row) and `IDirectoryCredentialClient` → Implements → `LdapDirectoryCredentialClient`.
16. Robustness: `Parse("", "x.md")`, `Parse("# Only a title", …)`, `Parse("## a\n```text\nroot\n  └─ child\n```", …)`
    do not throw; the last yields `root -Calls-> child`.

---

## 4. WPF UI

Plain WPF, no third-party UI packages, MVVM with a small `ObservableObject`/`RelayCommand` of our own.
Light, modern look: window background `#F3F4F6`, cards white with 1px `#E5E7EB` border, radius 8, subtle shadow on
hover, accent `#2563EB`, text `#111827`, muted `#6B7280`. Font Segoe UI (fallback default). Every kind has a badge
colour (Browser slate, Route/Endpoint blue/indigo, Vue green, TypeScript amber, Controller purple, UseCase rose,
Service teal, Repository orange, Client/Store cyan, Mapper gray, Method dark gray, Destination/Outcome lime/red,
Branch light gray, Section neutral).

Layout:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ DocsViewer   [📁 folder path …] [Open folder]   [🔍 search box……………]        │
├──────────────┬───────────────────────────────────────────────────────────────┤
│ Documents    │ Breadcrumb:  Login plumbing › Browser GET /Login › main.ts    │
│  • Login     ├───────────────────────────────────────────┬───────────────────┤
│    plumbing  │  Column 1    │ Column 2     │ Column 3    │ Details           │
│              │  ┌────────┐  │ ┌────────┐   │ ┌────────┐  │ Title, kind badge │
│ [Flows]      │  │card    │  │ │card ▸  │   │ │card    │  │ Path  [copy]      │
│ [Components] │  └────────┘  │ └────────┘   │ └────────┘  │ Description       │
│ [Sections]   │  ┌────────┐  │  (grouped by │             │ Notes             │
│              │  │card ▸  │  │   edge kind) │             │ Called by (in)    │
│              │  └────────┘  │              │             │ Appears in (lines)│
│              │              │              │             │ Markdown excerpt  │
└──────────────┴───────────────────────────────────────────┴───────────────────┘
```

- **Left pane**: folder picker (OpenFolderDialog from WPF .NET 8+), list of documents (Title, file name, Summary as
  tooltip), and a mode switch Flows / Components / Sections that decides what the first column shows:
  `graph.EntryPoints`, `graph.Components` (grouped by kind with small headers), `graph.Sections`.
- **Columns**: Miller columns in a horizontal `ScrollViewer`. Column N+1 shows `selected.ChildrenInOrder()` (hide
  `Mentions`), grouped by `EdgeKind` with a small group header ("Calls", "Then", "Uses", "Mounts", "Redirects",
  "Returns", "Contains", "Implements"). Selecting a card truncates deeper columns, appends the next column, scrolls
  it into view, and updates the breadcrumb and detail pane. Clicking a breadcrumb item truncates to it. Cards show
  the badge, Title, Subtitle (ellipsis, tooltip full), an edge label chip when present, and `›` + child count when
  `HasChildren`. Selected card has an accent border. Keyboard: Up/Down move in column, Right/Enter drills, Left goes back.
- **Details pane**: Title, kind, Owner (clickable → navigates), Path with Copy button, Description, Notes, "Called by"
  = Incoming edges (clickable), "Appears in" = Sources (section + line; clicking shows that part of the markdown in an
  excerpt box with the line highlighted). A "Show in Components" link for Method nodes.
- **Search**: text box filters across the current document (`graph.Search`), results in a popup/list; choosing one
  navigates: finds a path from an entry point (BFS over incoming edges) and rebuilds the columns, or if none, shows the
  node as a single column.
- **Startup**: command line arg (folder or .md file) → else last folder from `%LocalAppData%\DocsViewer\settings.json`
  → else walk up from the exe directory looking for a `docs` folder (so `dotnet run` from the repo finds `docs/`) → else
  empty state with a hint. A `FileSystemWatcher` reloads a doc when its file changes. `--dump <file.md>` on the command
  line writes `GraphDump.Render` to stdout and exits (attach a console with `AttachConsole(-1)` or write to a file given as
  the next arg) – used for verification.
- Empty states and errors are shown in-window, never as unhandled exceptions. `DispatcherUnhandledException` logs to
  `%LocalAppData%\DocsViewer\errors.log` and shows a message.

Make `DocsViewer.csproj` `net10.0-windows`, `UseWPF`, `Nullable enable`, `ApplicationIcon` optional, `AssemblyName DocsViewer`.
