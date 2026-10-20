# Writing plumbing docs for DocsViewer

Paste this into the prompt of the AI that generates the `*-Plumbing.md` files. The viewer turns each doc into
clickable cards; these rules decide whether a line becomes a card you can drill into or just a note.

## Keep doing this (it already works)

- YAML front matter with `summary:` and `area:`, then one `# Title`. Optional `parent: Home-Plumbing.md` makes the
  doc a child of another one in the sidebar; without it, `Home-Export-Plumbing.md` is nested under `Home-Plumbing.md`
  by name.
- Keep the docs inside the repository they describe (for example `<repo>/docs/`). The viewer finds the repository root
  from there and the paths on the cards open the real source files. Write paths as they are in the repo, relative to
  the repo root or to a project folder (`Controllers/Api/SignInController.cs` is found even when it lives under
  `GraspANALYTICS.Web/`).
- **The pieces at a glance** table right after the intro: `| Piece | Where | What it does |`, one row per file or type,
  repo-relative path in back-ticks in *Where*. Join several paths with ` + ` or ` → `.
- Numbered `##` sections, `###` subsections, in the order a request flows.
- Call trees in ```` ```text ```` fences with `├─`, `└─`, `│` connectors, one call per line, deeper indentation = called by the
  line above. `condition → Outcome` for branches, `(8010)` EventId suffixes, `(section 6a)` cross references,
  `see Other-Doc.md §3` for other documents, a single implementation type after three spaces
  (`IDirectoryCredentialClient.Validate(...)   LdapDirectoryCredentialClient`).
- `On the server:` followed by a tree whose root is the controller action, right after the request fence.
- Prose bullets that start with a back-ticked name become that card's description.

## Change these habits

1. **Start every flow with the trigger as the root line of the tree.** The first line decides the entry card.

   ```text
   Browser click "Export Report" (ExportReport.vue)
     └─ ExportReport.openPicker()
          └─ ExportReport.exportAs('pdf')
   ```

   Use `Browser GET /Home`, `Browser click "…" (Component.vue)`, `Browser submits the form`, or `Timer every 5 s`.
   Not `HomeShell header` or a bare function.

2. **Qualify every method and function with its owner, every time.** `ExportReport.exportAs()`,
   `AiQueryPanel.submitQuestion()`, `eventStream.readEventStream()`, `ExportReportUseCase.ExportAsync`,
   `IUserRepository.GetActiveUserByLoginAsync`. Never a bare `exportAs()`, `runQuery`, `done`, `ExecuteAsync`.
   For TypeScript modules the owner is the file name without extension (`api.signIn()`, `savedFilters.savedExportFilters()`).

3. **Only real code gets parentheses.** Inside a tree, `Name(...)` is read as a call. Write actions as plain words:
   `returns 200 with the file` rather than `Ok(stream, …)` or `File(stream, …)`; `copies the body into a MemoryStream`
   rather than `blob = response.blob()`. Keep `loop:`, `wait 5 seconds`, and pseudo-code out of call trees; put them in a
   bullet list under the tree.

4. **Write the call, not the assignment.** `IFoundryFeatureResolver.IsEnabled() → aiQueryEnabled`, not
   `aiQueryEnabled = IFoundryFeatureResolver.IsEnabled()`.

5. **Tag fences by purpose.** ```` ```text ```` only for call trees. ```` ```json ```` or ```` ```http ```` for request and
   response bodies. ```` ```diagram ```` for box-and-arrow pictures with `▼`, `◄──►`, or free layout. The viewer parses
   only `text` fences as trees.

6. **One format for the endpoints table**, in every doc:

   ```markdown
   | Endpoint | Handler | Request | Response | Statuses |
   |---|---|---|---|---|
   | `POST /api/UserReport/ExportReport` | `UserReportController.ExportReportAsync → ExportReportUseCase.ExportReportAsync` | `reportId`, `exportType`, `filters` | the file | 200; 400; 401; 404 |
   ```

   *Handler* is `Controller.Method → UseCase.Method`, back-ticked. Calls to third parties (Power BI REST) go in the
   same table with the host in the path.

7. **Paths in back-ticks, not links.** `` `GraspANALYTICS.Core/Application/UseCases/FoundryQueryUseCase.cs` ``, not
   `[FoundryQueryUseCase](../GraspANALYTICS.Core/...)`. Links lose the path in the viewer.

8. **Every doc gets the pieces table**, including server-only docs like AIPanel-Plumbing.md. In *What it does*, list the
   public functions or endpoints of that file in back-ticks (`loadLoginPage()` and `signIn()`); that is how the viewer
   learns which file owns a function.

9. **Headings that explain one method name it fully:** `### \`FoundryQueryUseCase.ExecuteAsync\` — setup`, followed by a
   tree or by a paragraph `` `FoundryQueryUseCase.ExecuteAsync`: `` and a numbered list of the calls it makes.

10. **Tables of fields, events, settings and statuses are fine as they are.** They become notes on the nearest card.
    Do not turn field lists into trees.

## A good tree

```text
Browser click "Ask AI" (AiQueryPanel.vue)
  └─ AiQueryPanel.submitQuestion()
       ├─ blank question → "Please enter a question."
       └─ AiQueryPanel.runQuery()
            └─ api.streamQuery()  → POST /api/FoundryAi/query
```

On the server:

```text
FoundryAiController.Query                 [Authorize], [RequireUserContext]
  ├─ blank query → 400 "Query is required."
  ├─ IFoundryQueryUseCase.ExecuteAsync(request, userContext)     see AIPanel-Plumbing.md §3
  │     FoundryDisabledException → 404 "AI Agent is not enabled."
  └─ IFoundryQueryEventSerializer.Serialize(event, daxQueriesEnabled)
```
