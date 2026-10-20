---
summary: How the Home page AI Query panel works from the browser to the API - when the toggle shows, the usage check, the streamed question request and its SSE events, answer markers, DAX diagnostics, the CSV offer and download, and what the panel remembers; read before debugging or changing the AI Query client or its endpoints.
area: architecture
---

# Home AI Query plumbing

This doc follows a user from the **AI Query** button in the Home header to a streamed answer and a CSV download, and names what connects to what and what calls what on the way. It owns the client side and the API surface. For the Home page as a whole, see `Home-Plumbing.md`. For what happens inside the server after the controller hands off (the agent loop, tool calls, DAX, Power BI), see `AIPanel-Plumbing.md`; for tenant isolation, see `AIPanel-DataSourceID-Security.md`.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Home page host | `GraspANALYTICS/Views/Home/Index.cshtml` | Calls `IFoundryFeatureResolver.IsEnabled()` and `IsDaxQueriesEnabledAsync()`; writes `data-ai-query-enabled` and `data-dax-queries-enabled` on `#home-app`, plus the `@Html.AntiForgeryToken()` hidden input. |
| Page settings | `GraspANALYTICS/ClientApp/src/home/shell/homePage.ts` | `readHomePage()`: reads the two flags into `HomePageSettings.aiQueryEnabled` and `.daxQueriesEnabled`. |
| Shell | `GraspANALYTICS/ClientApp/src/home/shell/HomeShell.vue` | Mounts `AiQueryToggle` in the header and `AiQueryPanel` after it, both only when `aiQueryEnabled`. |
| Toggle | `GraspANALYTICS/ClientApp/src/home/ai-query/AiQueryToggle.vue` | The **AI Query** header button (`#ai-query-toggle`); calls `panelState.togglePanel()`. |
| Open/closed state | `GraspANALYTICS/ClientApp/src/home/ai-query/panelState.ts` | `panelOpen` (read-only ref), `togglePanel()`, `closePanel()`. |
| Panel | `GraspANALYTICS/ClientApp/src/home/ai-query/AiQueryPanel.vue` | `submitQuestion()`, `runQuery()`, `applyStreamEvent()`, `finishQuery()`, `completeAnswer()`, `applyRefusal()`, `applyUsage()`, `refreshUsage()`, `seedDatesFromReport()`, `handleReportChanged()`, `acceptCsvOffer()`, `clearChat()`. |
| API client | `GraspANALYTICS/ClientApp/src/home/ai-query/api.ts` | `getUsageStatus()`, `streamQuery()`, `getQueryResultCsv()`. |
| SSE reader | `GraspANALYTICS/ClientApp/src/home/ai-query/eventStream.ts` | `readEventStream()`: turns the response body into `{ event, data }` frames. |
| Answer markers | `GraspANALYTICS/ClientApp/src/home/ai-query/answerMarkers.ts` | `parseAnswer()`, `displayableAnswer()`. |
| Markdown | `GraspANALYTICS/ClientApp/src/home/ai-query/markdown.ts` | `renderMarkdown()`: `marked` with `breaks: true`, then `DOMPurify.sanitize`. |
| CSV offer | `GraspANALYTICS/ClientApp/src/home/ai-query/csvOffer.ts` | `offersCsvExport()`, `isCsvAcceptIntent()`. |
| CSV button | `GraspANALYTICS/ClientApp/src/home/ai-query/CsvDownload.vue` | `download()`: the **Download CSV** button for one `exportId`. |
| DAX diagnostics | `GraspANALYTICS/ClientApp/src/home/ai-query/QueryDiagnostics.vue` | `copyDax()`: collapsible list of the DAX queries for the last question. |
| Report hook | `GraspANALYTICS/ClientApp/src/home/ai-query/reportPage.ts` | `onReportChanged()`, `reportFilterDateRange()`. |
| Preferences | `GraspANALYTICS/ClientApp/src/home/ai-query/panelPreferences.ts` | `PANEL_WIDTH_KEY`, `storedUseGlobalDataset()`, `storeUseGlobalDataset()`. |
| Types | `GraspANALYTICS/ClientApp/src/home/ai-query/types.ts` | `QueryRequest`, `QueryStreamEvent`, `QueryToolCall`, `QueryRefusal`, `QueryNotice`, `QueryCompletion`, `ChatEntry`. |
| Shared report state | `GraspANALYTICS/ClientApp/src/home/report-viewer/reportState.ts` | `activeReport` (read only by the panel), `setActiveReportMetadata()`. |
| Saved filters | `GraspANALYTICS/ClientApp/src/home/report-filters/savedFilters.ts` | `savedFilterDateRange()`. |
| Docked panel | `GraspANALYTICS/ClientApp/src/shared/useDockedPanel.ts` | `useDockedPanel()`: width, drag resize, body padding. |
| Side panels | `GraspANALYTICS/ClientApp/src/shared/sidePanels.ts` | `useExclusiveSidePanel()`, `announceSidePanelOpened()`. |
| Session errors | `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `handleNullSession()`. |
| Antiforgery | `GraspANALYTICS/ClientApp/src/shared/antiforgery.ts` | `antiforgeryToken()`. |
| Storage | `GraspANALYTICS/ClientApp/src/shared/browserStorage.ts` | `readStoredValue()`, `storeValue()`, `removeStoredValue()`. |
| File save | `GraspANALYTICS/ClientApp/src/shared/fileDownload.ts` | `saveBlob()`. |
| Query API | `GraspANALYTICS/Controllers/Api/FoundryAiController.cs` | `POST /api/FoundryAi/query` (`Query`), streams `text/event-stream`. |
| Event serializer | `GraspANALYTICS/Api/Foundry/FoundryQueryEventSerializer.cs` | `Serialize()`: each `IFoundryQueryEvent` to its JSON payload; drops `dax` when DAX queries are off. |
| CSV API | `GraspANALYTICS/Controllers/Api/FoundryAiExportController.cs` | `GET /api/FoundryAi/export/{exportId}/csv` (`ExportCsv`). |
| Usage API | `GraspANALYTICS/Controllers/Api/AiAccessController.cs` | `GET /api/AiAccess` (`GetAsync`): the user's token usage and access status. |
| Usage contract | `GraspANALYTICS/Api/Ai/Contracts/AiUsageStatusResponse.cs` + `AiContractMapper.cs` | `AiUsageStatusResponse`, `ToResponse()`. |
| Saved prompts API | `GraspANALYTICS/Controllers/Api/AiUserPromptsController.cs` | `GET /api/AiUserPrompts` (`GetAsync`). Not called by the panel (section 7). |
| Feature check | `GraspANALYTICS.Core/Application/Services/FoundryFeatureResolver.cs` | `IsEnabled()`, `IsDaxQueriesEnabledAsync()`. |
| Session flag | `GraspANALYTICS/Services/SessionService.cs` | `PopulateFoundrySession()`: sets `AppSession.FoundryEnabled`. |
| Usage service | `GraspANALYTICS.Core/Application/AiUsage/AiUsageService.cs` | `GetAsync()`, `RegisterUsageAsync()` (see AIPanel-Plumbing.md §11). |
| CSV use case | `GraspANALYTICS.Core/Application/UseCases/FoundryCsvExportUseCase.cs` | `Execute()` (see AIPanel-Plumbing.md §12). |

## 1. When the toggle and panel exist

```text
Browser GET /Home
  ├─ Index.cshtml renders #home-app
  │    ├─ IFoundryFeatureResolver.IsEnabled() → aiQueryEnabled   FoundryFeatureResolver
  │    │    ├─ IFeatureFlagService.IsEnabled(Feature_AI_Query)       FeatureFlagOptions, appsettings*.json
  │    │    └─ ISessionService.GetAppSession(false) → AppSession.FoundryEnabled
  │    ├─ aiQueryEnabled false → daxQueriesEnabled false
  │    └─ IFoundryFeatureResolver.IsDaxQueriesEnabledAsync() → daxQueriesEnabled
  │         └─ IAiUsageLimitRepository.GetAsync(userId, agencyId) → DaxQueriesEnabled
  └─ homePage.readHomePage(dataset) → aiQueryEnabled, daxQueriesEnabled
       ├─ aiQueryEnabled false → neither AiQueryToggle nor AiQueryPanel is mounted
       └─ HomeShell mounts AiQueryToggle and AiQueryPanel
```

- `SessionService.PopulateFoundrySession` sets `AppSession.FoundryEnabled` to true when the user's agency has a `ganFoundryAgencySettings` row with `FoundryEnabled` and the user's AI usage limit row has `AccessEnabled`. It runs from `SessionService.InitSession` at sign-in and from `SessionService.ReloadSession`, which the admin branding, admin settings, agency settings, and global settings saves call.
- Both flags render as `"true"`/`"false"` data attributes; `homePage.readHomePage` treats anything but `"true"` as off.
- There is no role check on the client or in these controllers. Access is the feature flag, the agency setting, and the user's AI usage limit row (set by Super Admin in the user editor).
- `AppSession.FoundryEnabled` is a snapshot taken when the session starts or is reloaded. If access is turned off later, the toggle stays until then, but the panel locks from the live usage check (section 2).

## 2. Opening the panel

The panel is always mounted while `aiQueryEnabled`; `v-show` hides it.

```text
Browser click "AI Query" (AiQueryToggle.vue)
  └─ panelState.togglePanel()
       ├─ useDockedPanel reserves body padding-right equal to the panel width while open
       ├─ sidePanels.announceSidePanelOpened('ai-query')        from useExclusiveSidePanel
       │     side-panel:opened on document → the Report Filters panel closes, and vice versa
       └─ AiQueryPanel watcher: panelOpen turns true
            ├─ dates never seeded and both empty → AiQueryPanel.seedDatesFromReport()
            │    └─ reportPage.reportFilterDateRange()
            │         └─ savedFilters.savedFilterDateRange()
            └─ AiQueryPanel.refreshUsage()
                 ├─ api.getUsageStatus()  → GET /api/AiAccess
                 ├─ AiQueryPanel.applyUsage(status)
                 └─ failure → dialogs.handleNullSession(error), the meter stays as it was
```

- `useDockedPanel` is set up with `widthStorageKey: 'ai-panel-width'` and `defaultWidth: 420`.

On the server:

```text
AiAccessController.GetAsync                 [Authorize], [CustomAuthorize], [RequireUserContext]
  ├─ CustomAuthorize: no app session → 401; duplicate sign-in while LastLoginWins is on → 460
  ├─ RequireUserContext: no user context → 401
  ├─ IAiUsageService.GetAsync(userContext)          AiUsageService     see AIPanel-Plumbing.md §11
  │    ├─ IAiUsageLimitRepository.GetAsync(userId, agencyId)
  │    │     no row → accessEnabled false and the AiAccessNotEnabledMessage setting
  │    └─ new month → IAiUsageLimitRepository.UpsertAsync(row)   TokensUsed reset to 0
  └─ AiContractMapper.ToResponse(status)  → 200 AiUsageStatusResponse
```

### Usage indicator and lock

`AiUsageStatusResponse`:

| Property | Meaning |
|---|---|
| `consumed` | Tokens used this month |
| `tokenLimit` | The user's monthly limit |
| `remaining` | `tokenLimit - consumed` (never below 0 when not allowed) |
| `allowed` | False when there is no row, access is off, or the limit is reached |
| `accessEnabled` | The user's `AccessEnabled` |
| `message` | Empty when allowed; otherwise the `AiAccessNotEnabledMessage` setting, or the `AiUsageDepletedMessage` setting followed by the `AiFairUseStatement` setting |

`AiQueryPanel.applyUsage(status)`:

- The meter (`#ai-usage-indicator`, a PrimeVue `ProgressBar`) shows only when `tokenLimit` is non-zero. Percent is `consumed / tokenLimit`, rounded and capped at 100. The tooltip reads "N of M tokens used". It turns depleted when `allowed` is false or `remaining <= 0`.
- `allowed && accessEnabled` → unlocked. Otherwise the lock message is `message`, or "You have reached your AI usage limit. Please try again later." The banner `#ai-depletion-banner` shows it, **Ask AI** is disabled, and suggestion chips hide.

### Date range

`AiQueryPanel.seedDatesFromReport()` copies the saved report filter dates (`reportPage.reportFilterDateRange()` → `savedFilters.savedFilterDateRange()`) into the **From** and **To** date inputs. An empty saved date leaves its input as it is. **Clear** (`#ai-date-clear`) empties both. The dates are a default for the question; the answer can move them (section 4).

### Report and dataset

```text
Browser GET /api/UserReport/GetReportMetaData (useReportEmbed.ts)
  └─ reportState.setActiveReportMetadata(powerBIReportId, metadata)
       └─ reportPage.onReportChanged listener                watches activeReport.value?.metadata
            └─ AiQueryPanel.handleReportChanged(datasetId)
                 ├─ first metadata → AiQueryPanel.clearChat()
                 │                   panelState.closePanel()
                 │                   AiQueryPanel.seedDatesFromReport()
                 ├─ different dataset → AiQueryPanel.resetQueryState()
                 │                      AiQueryPanel.appendNotice(REPORT_CHANGED)
                 │                      AiQueryPanel.seedDatesFromReport()
                 └─ same dataset → nothing
```

| Event | Panel does |
|---|---|
| First metadata | Stores `datasetId`, clears the chat, **closes the panel**, seeds the dates. |
| Metadata with a different dataset | Clears suggestions, DAX list, error, and CSV offer; adds the notice "Report changed — previous answers were based on a different dataset and may no longer apply."; reseeds the dates. The chat entries and the conversation history are kept. |
| Same dataset again | Nothing. |

Every metadata change also re-enables the global-dataset switch.

### Suggested questions and saved prompts

- The panel has no starting suggestions. Chips come only from a `SUGGESTED_QUESTIONS:` line in an answer (section 4). Whether the agent writes one depends on the agency's AI context text, not on code.
- `AiQueryPanel.askSuggestion` puts the chip's text in the question box and calls `AiQueryPanel.submitQuestion`.
- `GET /api/AiUserPrompts` exists but nothing in `GraspANALYTICS/ClientApp` calls it (section 7).

### Global dataset switch

**Use Global Dataset** (`ToggleSwitch`, `#ai-use-global-dataset`) sends `useGlobalDataset` with each question. The server then uses the agency's `DefaultDatasetId` instead of the report's dataset (`FoundryQueryContextBuilder.ResolveDatasetId`, see AIPanel-Plumbing.md §3). When no default is set, the server uses the report's dataset and sends a `global-dataset-unavailable` notice; `AiQueryPanel.applyNotice` shows its message, turns the switch off, and disables it until the report metadata changes.

## 3. Asking a question

```text
Browser click "Ask AI" (AiQueryPanel.vue)                 or Ctrl+Enter in the question box
  └─ AiQueryPanel.submitQuestion()
       ├─ a question is running → ignored
       ├─ csvOffer.isCsvAcceptIntent(text)                 only while a CSV offer is open
       ├─ locked and not a CSV acceptance → ignored
       ├─ blank → "Please enter a question."
       ├─ no datasetId → "No report is loaded yet. Please wait for the report to finish loading."
       ├─ CSV acceptance → AiQueryPanel.acceptCsvOffer(text, exportId)     (section 5)
       └─ AiQueryPanel.runQuery(text)
            ├─ AiQueryPanel.resetQueryState()
            ├─ AiQueryPanel.appendAssistant('', true)       pending "Analyzing" entry
            ├─ AiQueryPanel.buildRequest(text)
            ├─ api.streamQuery(request)  → POST /api/FoundryAi/query
            │    ├─ antiforgery.antiforgeryToken()
            │    ├─ non-OK status or no body → ApiError(status, body text)
            │    └─ eventStream.readEventStream(response.body)
            ├─ AiQueryPanel.applyStreamEvent(event, reply, streamed)     per event
            └─ AiQueryPanel.finishQuery(reply, text, streamed)          (section 6)
```

- Past the checks, `AiQueryPanel.submitQuestion` clears the error, empties the question box, and adds the user entry before it takes the CSV path or calls `AiQueryPanel.runQuery`.
- `AiQueryPanel.resetQueryState` clears the error, suggestions, DAX list, latest export id, and CSV offer.

The request:

```http
POST /api/FoundryAi/query
Content-Type: application/json
RequestVerificationToken: <value of input[name="__RequestVerificationToken"]>

{
  "query": "...",
  "datasetId": "<report dataset id>",
  "dateFrom": "yyyy-MM-dd" | null,
  "dateTo": "yyyy-MM-dd" | null,
  "useGlobalDataset": false,
  "conversationHistory": [ { "role": "user" | "assistant", "content": "..." } ]
}
```

The body binds to `FoundryQueryRequest` (`GraspANALYTICS.Domain/Models/Foundry/FoundryQueryRequest.cs`). The client never sends `accountIds`, `fieldFilter`, `reportContext`, or `maxRows`, so those stay null.

On the server:

```text
FoundryAiController.Query                   [Authorize], [RequireUserContext]
  ├─ blank query     → 400 "Query is required."
  ├─ blank datasetId → 400 "DatasetId is required."
  ├─ IFoundryQueryUseCase.ExecuteAsync(request, userContext)     see AIPanel-Plumbing.md §3
  │     FoundryDisabledException           → 404 "AI Agent is not enabled."
  │     PowerBiTableNotFoundException      → 400 "Unknown DataTable: <name>"
  │     UnknownFoundryFieldFilterException → 400 "Unknown fields in FieldFilter: ..."
  ├─ sets Content-Type text/event-stream, Cache-Control no-cache, X-Accel-Buffering no
  ├─ IFoundryFeatureResolver.IsDaxQueriesEnabledAsync() → daxQueriesEnabled
  └─ FoundryAiController.WriteEventAsync(event, daxQueriesEnabled)           per event
       ├─ IFoundryQueryEventSerializer.Serialize(event, daxQueriesEnabled)   FoundryQueryEventSerializer
       └─ writes the frame and flushes the response body
```

- The controller reads the events with `WithCancellation(cancellationToken)`; each frame is `event: <EventName>`, `data: <json>`, and a blank line.
- `FoundryQueryEventSerializer.Serialize` writes camelCase JSON and omits null properties.
- The action has no `[ValidateAntiForgeryToken]` and the app registers no global antiforgery filter (`Program.cs` only sets the header name), so the header the client sends is not checked here.

A frame on the wire:

```http
event: delta
data: {"text":"Total bookings for "}

```

### The SSE events

| `event:` | Domain event | `data` properties | Panel does |
|---|---|---|---|
| `meta` | `FoundryQueryMetaEvent` | `rowsAnalyzed`, `rowsTruncated`, `workspace`, `dataset` | Ignored (`api.toQueryStreamEvent` returns null). |
| `usage` | `FoundryQueryUsageEvent` | the `AiUsageStatusResponse` properties (section 2) | `AiQueryPanel.applyUsage`. Sent at the start and again after the answer with the updated count. |
| `notice` | `FoundryQueryNoticeEvent` | `reason`, `message` | Only `global-dataset-unavailable` is handled (section 2). |
| `delta` | `FoundryQueryDeltaEvent` | `text` | Appended to the raw answer; the entry re-renders. |
| `tool` | `FoundryQueryToolEvent` | `callId`, `name`, `dax` (only when DAX queries are on and the call has DAX), `rowsReturned`, `truncated`, `durationMs`, `exportId` (only when the call returned rows) | Added to the DAX list; a non-empty `exportId` becomes the latest export. |
| `refusal` | `FoundryQueryRefusalEvent` | `reason` (`budget`, `oversized`, `error`), `message`, `usage` | Held until the stream ends (section 6). |
| `done` | `FoundryQueryDoneEvent` | `answer`, `rowsAnalyzed`, `inputTokens`, `cachedTokens`, `outputTokens`, `reasoningTokens`, `totalTokens` | Held until the stream ends. The client reads `answer` and `totalTokens`. |

What the use case yields, and in what order: see AIPanel-Plumbing.md §3.

### `eventStream.readEventStream` — reading the body

`eventStream.readEventStream`:

1. Reads chunks from the body reader and decodes them with a `TextDecoder` in streaming mode into a buffer.
2. Splits the buffer on a blank line (`\n\n` or `\r\n\r\n`) and keeps the unfinished tail.
3. Parses each frame: every line is `field: value` (one leading space dropped); a line without `:` is a field with no value. `event:` sets the event name (default `message`); `data:` lines are collected and joined with `\n`. A frame with no data line is skipped.
4. Releases the reader lock when the stream ends. Any unfinished tail is discarded; the server always ends each frame with a blank line.

`api.toQueryStreamEvent` then parses `data` as JSON by event name and drops unknown names.

## 4. How an answer renders

While streaming, each `delta` makes `AiQueryPanel.applyStreamEvent` re-render the whole raw text through `answerMarkers.displayableAnswer` and `markdown.renderMarkdown`, so the marker lines never flash on screen.

When the stream ends with `done`, `AiQueryPanel.finishQuery` calls `AiQueryPanel.completeAnswer` with `completion.answer` (or the streamed text if `answer` is empty).

### `AiQueryPanel.completeAnswer` — the finished answer

`AiQueryPanel.completeAnswer`:

1. `answerMarkers.parseAnswer(text)` (below).
2. `markdown.renderMarkdown(parsed.answer)` into the entry, shown with `v-html`; the entry shows "N tokens" from `totalTokens`.
3. `AiQueryPanel.applyDateMarkers(parsed)`: a `DATE_RANGE_USED:` range sets **From** and **To** and adds the notice "Date filters updated to <from> → <to> to match this answer."; otherwise the no-date-filter note adds "Note: I am not using the date filters for this answer." as a notice.
4. Adds `{ role: 'user', content: question }` and `{ role: 'assistant', content: cleaned answer }` to the conversation history. The history lives only in the component and is sent in full with the next question.
5. `csvOffer.offersCsvExport(parsed.answer)`: sets the CSV offer when the answer offers one and a tool call in this question returned an `exportId` (section 5).
6. Updates the suggestion chips, only when the panel is not locked.

### `answerMarkers.parseAnswer` — the markers

`answerMarkers.parseAnswer`:

1. `SUGGESTED_QUESTIONS: a | b | c` anywhere, case-insensitive, to the end of its line → removed; split on `|` → chips.
2. `DATE_RANGE_USED: yyyy-MM-dd..yyyy-MM-dd` as a whole line → removed; returned as `dateRangeUsed`.
3. `Note: I am not using the date filters for this answer.` as a whole line → removed; returned as `ignoresDateFilters`.
4. Three or more line breaks collapse to one blank line; the result is trimmed.

The instructions that make the agent write `DATE_RANGE_USED:` and the date note come from the agency's AI context setting with its `{DateFrom}`/`{DateTo}` placeholders (`FoundryAiService.RenderAiContext`, see AIPanel-Plumbing.md §4), not from code.

### DAX diagnostics

- `QueryDiagnostics` renders inside `AiQueryPanel` only when the page rendered `daxQueriesEnabled = true`.
- It is a collapsed `<details id="ai-dax-info">`; the summary reads "No queries yet", "1 DAX query", or "N DAX queries".
- Each tool call shows "#n · rows · ms[ · truncated]" and the DAX in a `<pre>`. `QueryDiagnostics.copyDax` copies it to the clipboard and the button reads "Copied" or "Failed" for 1.2 s.
- A call without `dax` shows "(no DAX captured — call was denied or failed before execution)" and no Copy button.

Two checks keep DAX from users without the right: the page flag decides whether the list renders, and the server re-checks `IFoundryFeatureResolver.IsDaxQueriesEnabledAsync` on every query and leaves `dax` out of `tool` events when it is off. The list shows only the current question's calls; it resets on the next question.

## 5. The CSV offer and download

How an offer opens:

- `AiQueryPanel.recordToolCall` keeps the `exportId` of each `tool` event that has one; the latest call with rows wins.
- `csvOffer.offersCsvExport(answer)` is true when the answer contains the word "csv" and a sentence with "csv" is a question or uses an offer phrase ("would you", "want me", "export", ...). The offer then holds that latest export id.
- `csvOffer.isCsvAcceptIntent(text)` is true for a short yes ("yes", "ok", "sure", "go ahead", ...) or for an export noun (csv, download, export, spreadsheet, file) together with a verb.

```text
Browser click "Ask AI" (AiQueryPanel.vue)                 with a reply such as "yes" while a CSV offer is open
  └─ AiQueryPanel.submitQuestion()
       ├─ csvOffer.isCsvAcceptIntent(text)
       └─ AiQueryPanel.acceptCsvOffer(text, exportId)                no server call
            ├─ AiQueryPanel.appendAssistant(CSV_READY, false)
            │     "Here you go — click the Download CSV button below to save the results of your last query."
            ├─ adds the reply and that sentence to the conversation history
            └─ adds a CsvDownload entry for the export id
```

```text
Browser click "Download CSV" (CsvDownload.vue)
  └─ CsvDownload.download()
       ├─ api.getQueryResultCsv(exportId)  → GET /api/FoundryAi/export/{exportId}/csv
       ├─ fileDownload.saveBlob(blob, 'ai-query-results.csv')
       └─ failure → dialogs.handleNullSession(error)
                    "Sorry — the download could not be prepared. The data may have expired; please re-run your query and try again."
```

On the server:

```text
FoundryAiExportController.ExportCsv         [Authorize], [RequireUserContext]
  ├─ IFoundryCsvExportUseCase.Execute(exportId, userContext)     FoundryCsvExportUseCase     see AIPanel-Plumbing.md §12
  │    ├─ IFoundryCsvExportStore.Get(exportId)                    FoundryCsvExportStore
  │    │     missing, or another user or agency → 404 "The requested CSV export is no longer available."
  │    └─ CsvContentBuilder.Build(data)                            UTF-8 with BOM
  └─ returns 200 text/csv, file name "ai-query-results.csv"
```

- The export is created on the server when a tool call returns rows (`FoundryToolCallService.SaveCsvExport`, a `Guid` "N" id). `FoundryCsvExportStore` keeps it in `IMemoryCache` with a 30-minute sliding expiration, so it is lost on an app restart.
- The CSV holds the rows the tool call returned to the agent, not the agent's answer.
- The agent's instruction to offer a CSV is `FoundryAiService.CsvExportGuidance`.
- An offer lasts until the next question, a dataset change, or **Clear chat**. Each accepted offer adds its own button; old buttons keep their export id.

## 6. Errors, the usage limit, and cancellation

### `AiQueryPanel.finishQuery` — stream outcomes

| Outcome | Panel shows |
|---|---|
| `done`, no refusal | The answer (section 4). |
| `refusal` with `reason` `oversized` or `error` | Pending entry removed; `#ai-error` with the refusal `message`, or "An error occurred. Please contact Support." |
| `refusal` without `usage` | Locks with the message. The server puts `usage` on every refusal it sends, so this row covers only a payload without it. |
| `refusal` with `usage` (`budget`) | `AiQueryPanel.applyUsage(usage)`. If that locks the panel, the lock banner shows the usage message; if not (the estimate was over the remaining tokens, but the limit is not reached), `#ai-error` shows the refusal message. |
| Stream ended with neither | Pending entry removed; "An error occurred. Please contact Support." |
| Non-OK status or a read error | Pending entry removed; `dialogs.handleNullSession(error)`; "An error occurred. Please contact Support." |

The refusal reasons and their setting-driven messages (`AiFoundryServiceErrorMessage`, `AiFoundryOversizedRequestMessage`, `AiFoundryBudgetExceededMessage`, `AiFoundryOversizedResultsMessage`) come from `FoundryQueryUseCase` (see AIPanel-Plumbing.md §3). An exception the use case does not turn into a refusal, after the headers are sent, ends the stream early, and the client lands in the "neither" row.

`dialogs.handleNullSession` hands the status to a global `HandleNullSession` when the page has one; otherwise a 401 sends the browser to `/Error?statusCode=401`.

### Usage limit reached

- A user over the limit is refused before the agent is called: `FoundryQueryUseCase.ExecuteAsync` returns a single `budget` refusal with `allowed: false` usage, and the panel locks.
- The lock stays until a later usage check says `allowed` again (each panel open runs one). While locked, only a CSV acceptance still works.
- Tokens are registered only when an answer completes (`IAiUsageService.RegisterUsageAsync` with input plus output tokens); the second `usage` event carries the new count.

### Cancellation

- The client has no stop button and does not pass an `AbortSignal`. Closing the panel or clearing the chat (disabled while running) does not stop a running question; the answer still lands in the hidden panel.
- Leaving or reloading the page aborts the fetch. The server sees `HttpContext.RequestAborted`; the controller's `WithCancellation` loop and the use case stop. A cancelled question writes no `done`, registers no usage, and saves no prompt.

## 7. Endpoints

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/AiAccess` | `AiAccessController.GetAsync → IAiUsageService.GetAsync` | none | `consumed`, `tokenLimit`, `remaining`, `allowed`, `accessEnabled`, `message` | 200; 401 (no user context, or `[CustomAuthorize]` finds no app session); 460 (`[CustomAuthorize]` duplicate sign-in when `LastLoginWins` is on) |
| `POST /api/FoundryAi/query` | `FoundryAiController.Query → IFoundryQueryUseCase.ExecuteAsync` | `query`, `datasetId`, `dateFrom`, `dateTo`, `useGlobalDataset`, `conversationHistory[{role, content}]`; header `RequestVerificationToken` (sent, not validated) | `text/event-stream`: `meta`, `usage`, `notice`, `delta`, `tool`, `refusal`, `done` | 200 (refusals arrive inside the stream); 400 plain text (blank query or dataset, unknown table, unknown field filter) or a model-binding validation problem; 401; 404 "AI Agent is not enabled." |
| `GET /api/FoundryAi/export/{exportId}/csv` | `FoundryAiExportController.ExportCsv → IFoundryCsvExportUseCase.Execute` | `exportId` from a `tool` event | `text/csv` file `ai-query-results.csv`, UTF-8 with BOM | 200; 401; 404 "The requested CSV export is no longer available." |
| `GET /api/AiUserPrompts` | `AiUserPromptsController.GetAsync → IAiUserPromptService.GetByAgencyAsync` | none (the agency id comes from the user context) | array of `Id`, `UserId`, `PromptText`, `PromptResponse`, `ReportId`, `Dataset`, `DaxQueriesUsed`, `TokensSent`, `TokensReceived`, `DateCreated` (PascalCase: `[CaseInsensitiveJson]` swaps in a formatter with no naming policy) | 200; 401 (no user context). No `[Authorize]`. Not called by the panel. |

- The rows behind `GET /api/AiUserPrompts` are written by `FoundryQueryUseCase.SaveQueryAsync` when an answer completes; `ReportId` comes from `reportContext`, which the panel never sends.
- What the query use case does behind `POST /api/FoundryAi/query`: see AIPanel-Plumbing.md §3.
- Tenant scoping for the query (the `DataSourceID` filter forced on every tool call) is in `AIPanel-DataSourceID-Security.md`. The CSV export is scoped by the user and agency stored with it.

## 8. What the panel remembers

| What | Where | Lifetime |
|---|---|---|
| Open or closed | `panelState.ts` module ref | Page load. Starts closed, and the first report metadata closes it again. |
| Panel width | `localStorage` `ai-panel-width` | Saved after a drag on the left edge; double-click resets to 420 and removes the key. Clamped to 320 px .. 90% of the window, and re-clamped on window resize. |
| Use Global Dataset | `localStorage` `ai-use-global-dataset` (`"true"`/`"false"`) | Saved on every change, including the server turning it off. |
| Chat entries, conversation history, dates, suggestions, DAX list, CSV offer | Component state in `AiQueryPanel.vue` | Page load. **Clear chat** (`#ai-clear-chat`) empties entries, history, suggestions, DAX list, and the offer; dates stay. |
| Usage status | Component state | Refreshed on each panel open and from `usage` events. |

`localStorage` access goes through `browserStorage.readStoredValue`, `browserStorage.storeValue`, and `browserStorage.removeStoredValue`, which swallow storage errors; without storage the panel uses the defaults.
