# DocsViewer

A Windows desktop app (.NET 10, WPF) that turns architecture "plumbing" markdown docs into a clickable
drill-down of cards. Open a folder of docs, pick one, and start at an entry point such as
**Browser GET /Login**. Each click opens a new column with the files, functions, endpoints, use cases,
services and repositories the selected card uses, all the way down.

## Run

```bash
dotnet run --project src/DocsViewer
```

With no arguments the app looks for the last folder you opened, then for a `docs` folder above the exe
(so running from this repo opens `docs/`). You can also pass a folder or a single `.md` file:

```bash
dotnet run --project src/DocsViewer -- C:\path\to\docs
```

Print what the parser extracted from a doc, without opening a window:

```bash
dotnet run --project src/DocsViewer -- --dump docs/Login-Plumbing.md dump.txt
```

Build and test:

```bash
dotnet build DocsViewer.slnx
```

```bash
dotnet test tests/DocsViewer.Core.Tests
```

`nuget.config` pins the package source to nuget.org because the user-level config includes a private feed
that needs interactive sign-in.

## How to read the UI

- **Flows** – the first column lists entry points: things nobody else calls, such as `Browser GET /Login (or /)`
  or `LoginPage.submit()`. Click one to open the next column.
- **Components** – the first column lists every file/type from the doc's "pieces" table (Vue pages, TypeScript
  modules, controllers, use cases, services, repositories, …). Click `LoginPage.vue` to see its functions and the
  endpoints they call.
- **Sections** – the doc's headings; click one to see the flow items it describes.
- Columns are grouped by relationship: **Calls**, **Then** (the next step), **Uses** (served by / depends on),
  **Mounts**, **Redirects**, **Returns**, **Contains**, **Implements**.
- The details pane shows the path, description, notes, who calls the selected item, and every place in the markdown
  it appears (click a line to see the excerpt). The search box matches titles, paths, descriptions and notes.
- **Open file**: when the docs folder lives inside a repository (a folder with `.git` or a solution file above it),
  the toolbar shows that repository as the source root and cards with a path get **Open file** and **Reveal in
  Explorer** buttons. Paths relative to a project folder are found by file name. Use **Source root…** to point at a
  different folder; the choice is remembered per docs folder.
- **Document hierarchy**: `Home-Export-Plumbing.md` is listed under `Home-Plumbing.md` (by name, or by a
  `parent:` key in the front matter). Chips under the breadcrumb link to the parent, children and any document the
  current one refers to, and "see Other-Doc.md §3" notes on a card open that document at that section.

## Writing docs the viewer understands

The parser reads ordinary markdown with a few conventions that AI-generated plumbing docs already follow.
When you generate a new doc, ask for this shape:

1. **Front matter** with `summary:` and `area:`, then a single `# Title`.
2. A **pieces table** with the header `Piece | Where | What it does`. One row per file/type, with the source path in
   *Where* (several paths may be joined with ` + ` or ` → `), and the public functions or endpoints it exposes as
   back-ticked code in *What it does* (`loadLoginPage()`, `GET /api/sign-in`).
3. **Numbered sections** (`## 1. Loading the page`). Name the subject in the heading when a section explains one
   method: `## 3. Inside \`SignInUseCase\`` or `## 4. \`SignInCompletionService.CompleteAsync\``.
4. **Call trees** in ```` ```text ```` fences using box-drawing connectors:

   ```text
   SignInController.SignInAsync            [ValidateAntiForgeryToken]
     └─ request.ToCommand(clientAddress)   SignInContractMapper → SignInCommand(...)
     └─ ISignInUseCase.ExecuteAsync
     └─ result.ToResponse(Url)
   ```

   Rules: `Type.Method(...)` lines are calls; `GET /path` lines are endpoints; `/path` lines are routes;
   `condition → Outcome` lines are branches with their result; `(section 6a)` at the end of a line links to that
   section; a single type name after three or more spaces names the implementation of an interface.
5. **Call tables** with the header `Call | Server | Returns` for a fan-out of HTTP calls, where *Server* is
   `Controller → dependency, dependency`.
6. **Step lists**: a paragraph ending in a colon that names one function, followed by a list, e.g.
   `` `LoginPage.submit()`: `` then `1. …` `2. Otherwise calls `signIn(...)` …`. A nested table or fence inside a list
   item belongs to that step.
7. **Destination / status tables** (`Destination | nextUrl`, `Error code | Status`) for where the browser goes next.
8. Prose bullets that start with a back-ticked name (`` - `VitePageResolver` reads the file … ``) become that card's
   description.

Everything else stays attached as notes, so nothing from the doc is lost; it just may not become its own card.

See `SPEC.md` for the full design, including the canonical id rules that merge `ISignInUseCase.ExecuteAsync`,
`SignInUseCase.ExecuteAsync` and `ExecuteAsync(command)` into one card.
