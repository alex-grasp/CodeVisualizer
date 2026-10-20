---
summary: How the Home page Export Report feature works end to end - the Vue export button and type picker, the state it reads, POST /api/UserReport/ExportReport, the access check and data-scope filter, the Power BI ExportTo start-poll-download flow, and how the file reaches the browser; read before debugging or changing report export.
area: architecture
---

# Home export plumbing

This doc follows a click on **Export Report** in the Home page header to the PDF or PowerPoint file landing in the browser's downloads, and names what connects to what and what calls what on the way. For the Home page as a whole see `Home-Plumbing.md`; the shared report state comes from the report viewer (see `Home-ReportViewer-Plumbing.md` §3c) and the export filters come from the Filters panel (see `Home-Filters-Plumbing.md`).

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Host | `GraspANALYTICS/ClientApp/src/home/shell/HomeShell.vue` | Places `<ExportReport :header-text-color>` in the header. |
| Button, picker, overlay | `GraspANALYTICS/ClientApp/src/home/export-report/ExportReport.vue` | `openPicker()`, `exportAs()`: the header button, the PDF/PowerPoint dialog, the loading overlay, the error dialog. |
| State adapter | `GraspANALYTICS/ClientApp/src/home/export-report/reportPage.ts` | `currentReport()`, `exportFilterExpression()`, `useExportAvailability()`. |
| API client | `GraspANALYTICS/ClientApp/src/home/export-report/api.ts` | `exportReport()`: the POST, the file check, the file name. |
| Types | `GraspANALYTICS/ClientApp/src/home/export-report/types.ts` | `ExportType` (`'pdf' \| 'ppt'`), `ReportIdentity`, `ExportRequest`, `ExportedFile`. |
| Shared report state | `GraspANALYTICS/ClientApp/src/home/report-viewer/reportState.ts` | `activeReport` (Power BI report id, workspace id, `rendered`); re-exports `toODataFilter()`. |
| Saved filters | `GraspANALYTICS/ClientApp/src/home/report-filters/savedFilters.ts` | `savedExportFilters()`. |
| Export filter builder | `GraspANALYTICS/ClientApp/src/home/report-filters/powerBIFilters.ts` | `exportFilters()`: date, company, and account filters. |
| OData conversion | `GraspANALYTICS/ClientApp/src/home/report-viewer/powerBIFilters.ts` | `toODataFilter()`. |
| File save | `GraspANALYTICS/ClientApp/src/shared/fileDownload.ts` | `fileNameFromContentDisposition()`, `saveBlob()`. |
| Dialogs | `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `showOk()`, `escapeHtml()`, `handleNullSession()`. |
| Antiforgery | `GraspANALYTICS/Views/Home/Index.cshtml` + `GraspANALYTICS/ClientApp/src/shared/antiforgery.ts` | `@Html.AntiForgeryToken()` hidden input; `antiforgeryToken()` reads it into the header. |
| Endpoint | `GraspANALYTICS/Controllers/Api/UserReportController.cs` | `POST /api/UserReport/ExportReport`. |
| Request body | `GraspANALYTICS/Classes/ExportRequest.cs` | `ReportId`, `ExportType`, `Filters`. |
| User context | `GraspANALYTICS/Helpers/RequireUserContextAttribute.cs` → `GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs` | `OnActionExecutionAsync` → `GetAsync`: user, role, agency, company limit, and workspace from the session. |
| Use case | `GraspANALYTICS.Core/Application/UseCases/ExportReportUseCase.cs` | `ExportReportAsync`: access check, data scope, filter composition, start, poll, download. |
| Report assignment | `GraspANALYTICS.Core/Application/Services/UserReports/Repos/UserReportRepository.cs` | `IsAssignedAsync`. |
| Data scope | `GraspANALYTICS.Core/Application/Authorization/UserDataScopeResolver.cs` + `GraspANALYTICS.Core/Application/Services/DataSourceService.cs` | `ResolveAsync` → `GetUserConsolidatedIDsAsync`: the agency's data source ids. |
| Table names | `GraspANALYTICS.Core/Infrastructure/PowerBI/DatasetMetadataService.cs` + `GraspANALYTICS.Core/Application/UseCases/DefaultTableResolver.cs` | `GetAsync`, `Resolve`: finds the `ReportData*` and `Corps*` tables in the dataset. |
| Power BI REST | `GraspANALYTICS.Core/Infrastructure/PowerBI/PowerBIReportService.cs` | `GetPowerBIReportAsync`, `StartReportExportAsync`, `PollExportRequestAsync`, `GetExportedFileAsync`. |
| Entra token | `GraspANALYTICS.Core/Infrastructure/EntraIdService/EntraIdService.cs` | `GetAccessTokenAsync`. |
| HTTP client | `GraspANALYTICS/CompositionRoot/GraspHttpClientsExtensions.cs` | Registers the named `HttpClient` `"powerbi"` with a 30-second timeout. |

## 1. The button and the type picker

```text
Browser click "Export Report" (ExportReport.vue)
  ├─ disabled while reportPage.useExportAvailability() is false
  └─ ExportReport.openPicker()
       ├─ export running → nothing
       └─ opens the "Export Report" dialog: "Select Export Type", PDF, PowerPoint, Cancel
```

- `reportPage.useExportAvailability` is true only when `activeReport.rendered` is true. The viewer sets `rendered` from the embed's `rendered` event (`useReportEmbed.watchReportEvents` → `reportState.markActiveReportRendered`), and clears the whole state when a report is torn down, so the button is disabled while no report is on screen.
- The dialog states that exports reflect the date, company, and account filters, and that a very large list of agency ids may be truncated.
- Cancel closes the dialog.

```text
Browser click "PDF" or "PowerPoint" (ExportReport.vue)
  └─ ExportReport.exportAs(exportType)
       ├─ closes the dialog
       ├─ reportPage.currentReport()
       │     no active report → dialogs.showOk('Error', html) with the generic message, nothing sent
       ├─ reportPage.exportFilterExpression()
       │    ├─ savedFilters.savedExportFilters()
       │    │    └─ powerBIFilters.exportFilters(savedFilters)        (report-filters)
       │    └─ powerBIFilters.toODataFilter(filter)                   (report-viewer, through reportState)
       ├─ api.exportReport(request)            → POST /api/UserReport/ExportReport          (section 2)
       ├─ fileDownload.saveBlob(file.blob, file.fileName)                                   (section 5)
       └─ throws                                                                           (section 6)
            ├─ dialogs.handleNullSession(error)
            └─ dialogs.showOk('Error', html)
```

`ExportReport.exportAs` sends:

| Field | Source |
|---|---|
| `reportId` | `reportPage.currentReport()` → `activeReport.powerBIReportId` |
| `filters` | `reportPage.exportFilterExpression()`: each filter from `savedFilters.savedExportFilters()` through `powerBIFilters.toODataFilter()`, joined with ` and `, prefixed with `$filter=`; empty string when there are no filters |
| `exportType` | `'pdf'` or `'ppt'` |

`powerBIFilters.exportFilters` (report-filters) builds, from the saved filters the Filters panel loaded:

- always an `IssuedDate` filter on the report table: `>= startDate` and `< the day after endDate` (UTC midnight)
- `CompanyID in (...)` on the corporation table when company ids are saved
- `AccountID in (...)` on the corporation table when account ids are saved

Custom filters are **not** part of the export. If the saved filters were never loaded, the list is empty and `filters` is `''`.

Nothing else is sent: not the workspace id, not the current page, not a bookmark or the viewer's live filter state. The export always starts from the report's saved default view with these report-level filters.

## 2. The request

`api.exportReport(request)` calls `fetch` through `appUrl()`, so a virtual directory works:

```http
POST /api/UserReport/ExportReport
Content-Type: application/json
RequestVerificationToken: <value of input[name="__RequestVerificationToken"]>

{ "reportId": "<guid>", "filters": "$filter=ReportData/IssuedDate ge datetime'...' and ...", "exportType": "pdf" }
```

- The token comes from the hidden input `@Html.AntiForgeryToken()` writes into `Views/Home/Index.cshtml`, read by `antiforgery.antiforgeryToken()`.
- `fetch` has no timeout of its own; the browser waits as long as the server takes.

## 3. On the server

### 3a. The controller

```text
UserReportController.ExportReportAsync              [ApiController], [RequireUserContext], [ValidateAntiForgeryToken]
  ├─ antiforgery check fails → 400                  (an authorization filter, so it runs first)
  ├─ RequireUserContextAttribute.OnActionExecutionAsync(context, next)
  │    └─ UserContextResolver.GetAsync()
  │          no app session, or user not found → 401 with an empty body
  ├─ ReportId null → 400 { status: "error", message: "Empty ReportId or Workspace Id" }
  ├─ ExportType trimmed and lower-cased: "pdf" → Pdf, "ppt" → Pptx
  │     anything else → 400 { status: "error", message: "Error exporting report. Only PDF or PPT are allowed." }
  ├─ ExportReportUseCase.ExportReportAsync(reportId, format, filters, userContext, cancellationToken)    (section 3b)
  ├─ ReportNotAvailable → 404 { status: "error", message: "The report is not available to the current user." }
  ├─ any other failure → 400 { status: "error", message: <use-case message> }
  └─ success → returns 200 with the file (range processing on)
```

- `UserContextResolver.GetAsync` builds `UserContext` with `UserId`, `RoleId`, `AgencyId`, `CorporationId`, `DataSecurityCorporationId` (from the `ganUser` row), and `WorkspaceId` (the session's `PowerBIWorkspaceID`, `Guid.Empty` when it is not a GUID).
- The workspace always comes from the session, never from the browser.

### 3b. The use case

```text
ExportReportUseCase.ExportReportAsync(reportId, format, filters, userContext, cancellationToken)
  ├─ request cancelled → rethrows
  ├─ any other exception → Fail "Error exporting report ID {id}."                         (8008)
  └─ ExportReportUseCase.ExportAsync(reportId, format, filters, userContext, cancellationToken)
       ├─ ExportReportUseCase.GetRefusalAsync(reportId, userContext, cancellationToken)
       │    ├─ UserReportRepository.IsAssignedAsync(userId, reportId)
       │    │     no ganUserReport row, or the ganReport row is IsDeleted → ReportNotAvailable    (8010)
       │    └─ WorkspaceId is Guid.Empty → Fail                                                (8011)
       ├─ UserDataScopeResolver.ResolveAsync(userContext)
       │    ├─ DataSourceService.GetUserConsolidatedIDsAsync(agencyId)
       │    │     ConsolidatedDataSources children of the agency, else [agencyId]
       │    └─ IAccountsService.GetAccountIdsByUserIdAsync(userId)     only for non-admin roles; not used by export
       │    no data source ids → Fail                                                          (8002)
       ├─ PowerBIReportService.GetPowerBIReportAsync(workspaceId, reportId)                    (section 4)
       │     null → Fail                                                                       (5001)
       ├─ DatasetMetadataService.GetAsync(datasetId, workspaceId)        cached per workspace + dataset
       ├─ DefaultTableResolver.Resolve(tables)           "ReportData", else the first "ReportData*" table
       │     none → Fail                                                                       (8003)
       ├─ DataSecurityCorporationId > 0 → DefaultTableResolver.Resolve(tables, "Corps")
       │     none → Fail                                                                       (8009)
       ├─ composes the filter                                                                  (section 3c)
       │     longer than 1500 characters → Fail "Your selection is too large..."               (8004)
       ├─ PowerBIReportService.StartReportExportAsync(workspaceId, reportId, format, composedFilter)
       │     null or no export id → Fail                                                       (5005)
       ├─ ExportReportUseCase.GetPollingTimeout()
       ├─ PowerBIReportService.PollExportRequestAsync(workspaceId, reportId, exportId, timeout, CancellationToken.None)
       │     null → Fail "PollExportRequest error."                                            (8006, 5006)
       │     state not Succeeded → Fail "PollExportRequest error."                             (8005)
       ├─ PowerBIReportService.GetExportedFileAsync(workspaceId, reportId, export)
       │     no stream → Fail "Failed to retrieve exported file for report ID {id}."           (8007, 5007)
       └─ returns Ok with the stream, the content type, and "<ReportName><ResourceFileExtension>"
```

- `ExportReportUseCase.GetPollingTimeout` reads the `ganSetting` row `ExportPollingTimeoutSeconds`, or uses 600 seconds when it is missing or not a number.

### 3c. The filter sent to Power BI

`ExportReportUseCase.ExportAsync` composes the `reportLevelFilters` text in four steps:

1. The request's `filters` with every `$filter=` removed.
2. The base filter `<ReportData table>/DataSourceID in (<data source ids>)`.
3. When `DataSecurityCorporationId > 0`, ` and <Corps table>/CompanyID in (<DataSecurityCorporationId>)` is appended to the base filter. No `Corps` or `Corps*` table in the dataset fails the export (8009).
4. The result is the base filter alone, or `<base filter> and <request filters>` when the request sent filters.

- The agency scope (`DataSourceID`) and a user's company limit (`DataSecurityCorporationId`, read from the user row in `UserContextResolver`) are always added on the server.
- The browser's date, company, and account filters are appended as text after the server scope. A `// REFACTOR (#6457)` note in the use case records that this concatenation is not yet hardened against OData injection.
- `UserDataScopeResolver` also resolves the account ids a non-admin user may see, but the export does not use them; account narrowing comes only from the browser's filter.
- The 1500-character limit applies to the whole composed filter, which is why the dialog warns about long agency id lists.

## 4. Power BI ExportTo: start, poll, download

All four `PowerBIReportService` calls use the named `HttpClient` `"powerbi"` (30-second timeout per request) and an access token from `EntraIdService.GetAccessTokenAsync` for `https://analysis.windows.net/powerbi/api/.default`.

### `PowerBIReportService.GetPowerBIReportAsync` — report lookup

```text
PowerBIReportService.GetPowerBIReportAsync(workspaceId, reportId, cancellationToken)
  ├─ EntraIdService.GetAccessTokenAsync(scope)
  └─ PowerBIReportService.FetchPowerBIReportAsync()      → GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}
        any failure → null                                                    (5001)
```

### `PowerBIReportService.StartReportExportAsync` — start

```text
PowerBIReportService.StartReportExportAsync(workspaceId, reportId, format, filters, cancellationToken)
  ├─ EntraIdService.GetAccessTokenAsync(scope)
  └─ HttpClient.SendAsync()        → POST https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/ExportTo
        not 2xx → null, logs the status and the response body                 (5005)
        empty body → null
        otherwise → the export id and state
```

The body:

```json
{
  "format": "PDF",
  "powerBIReportConfiguration": { "reportLevelFilters": [ { "filter": "<composed filter>" } ] }
}
```

`format` is `"PDF"` or `"PPTX"`. No `pages`, `bookmark`, or `defaultBookmark` is sent in `powerBIReportConfiguration`.

### `PowerBIReportService.PollExportRequestAsync` — poll

```text
PowerBIReportService.PollExportRequestAsync(workspaceId, reportId, exportId, timeOutInSeconds, cancellationToken)
  ├─ EntraIdService.GetAccessTokenAsync(scope)            once, before the first status call
  └─ HttpClient.SendAsync()        → GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/exports/{exportId}
        not 2xx → null                                                        (5006)
        empty body → null
        Succeeded or Failed → returns the export
        otherwise → Task.Delay() for 5 seconds, then the next status call
```

- Before each status call it checks the clock: more than `timeOutInSeconds` since the start, or a cancelled token, returns null.
- The use case passes `CancellationToken.None`, so closing the tab does not stop the server from waiting on Power BI. The start and download calls do take the request's cancellation token.

### `PowerBIReportService.GetExportedFileAsync` — download

```text
PowerBIReportService.GetExportedFileAsync(workspaceId, reportId, export, cancellationToken)
  ├─ state not Succeeded → null
  ├─ EntraIdService.GetAccessTokenAsync(scope)
  └─ HttpClient.SendAsync()        → GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/exports/{exportId}/file
        not 2xx → null                                                        (5007)
        otherwise → copies the whole body into a MemoryStream and returns it with ReportName and ResourceFileExtension
```

### While it waits: the loading indicator

`exporting` is `true` from the click until the file is saved or the error is shown:

- the header button shows three blinking dots instead of the external-link icon, gets the `is-exporting` class and `aria-disabled`, and the title "Report is exporting..."; `ExportReport.openPicker` ignores clicks
- a full-screen overlay, teleported to `<body>` as an `<output aria-live="polite">`, shows a spinner, "Exporting report...", and "This can take a moment for large reports. Please keep this window open."

There is no client-side polling; the one POST stays open for the whole server flow.

## 5. Downloading the file

```text
Browser receives the export response
  ├─ api.exportReport(request)
  │    ├─ not 2xx, or Content-Type contains application/json or text/html → throws ApiError    (section 6)
  │    ├─ reads the body as a Blob
  │    └─ fileDownload.fileNameFromContentDisposition(header, "exported_file")
  │          filename* (decoded) first, then filename, then the fallback
  └─ ExportReport.exportAs(exportType)
       └─ fileDownload.saveBlob(blob, fileName)
             creates an object URL, clicks a temporary <a download>, removes it, revokes the URL
```

| Format | Content type | File name |
|---|---|---|
| PDF | `application/pdf` | Power BI's `reportName` + `resourceFileExtension` (for example `Sales.pdf`) |
| PowerPoint | `application/vnd.openxmlformats-officedocument.presentationml.presentation` | same, with `.pptx` |

The controller returns the file with a download name, so ASP.NET Core sends `Content-Disposition: attachment` with both `filename` and `filename*`.

## 6. Failures

### Statuses

| Case | Status | Body |
|---|---|---|
| File ready | 200 | the file |
| No session / user (`RequireUserContext`) | 401 | empty |
| Missing or wrong antiforgery token | 400 | ASP.NET default |
| `reportId` not a GUID (model binding) | 400 | validation problem |
| `reportId` missing | 400 | `{ status, message: "Empty ReportId or Workspace Id" }` |
| `exportType` not `pdf`/`ppt` | 400 | `{ status, message: "Error exporting report. Only PDF or PPT are allowed." }` |
| Report not assigned to the user, or deleted | 404 | `{ status, message: "The report is not available to the current user." }` |
| Any other use-case failure | 400 | `{ status, message: <use-case message> }` |
| Unexpected exception in the use case | 400 | `{ status, message: "Error exporting report ID {id}." }` (8008) |

Cancellation of the request is rethrown, not turned into a failure.

Use-case messages the browser can receive:

- `Error exporting report ID {id}.` (workspace missing, no data sources, report or table not found, start failed, unexpected error)
- `Your selection is too large to export. Please narrow your filters or select fewer items, then try again.`
- `PollExportRequest error.` (timed out, status call failed, or Power BI reported `Failed`)
- `Failed to retrieve exported file for report ID {id}.`

### In the browser

`api.exportReport` throws `ApiError(status, body)` when the status is not 2xx **or** the response is JSON/HTML. `ExportReport.exportAs` then calls:

1. `dialogs.handleNullSession(error)`: with no `HandleNullSession` global on the page, a 401 sends the browser to `/Error?statusCode=401`.
2. `dialogs.showOk('Error', html)`: "An error occurred while exporting the report." plus the server's `errorMessage` or `message` (HTML-escaped with `dialogs.escapeHtml`) when the body has one. The Home page loads no `ShowOkDialog` global, so this shows as a browser `alert` with the HTML stripped.

No active report when an option is clicked → the same generic error, nothing sent. A filter that `powerBIFilters.toODataFilter` cannot convert throws inside the `try` and also shows the generic error.

## 7. Logging

- `ExportReportUseCase` (8000-8999): 8002 no data sources, 8003 report table not found, 8004 selection too large, 8005 Power BI reported failure (state, code, message), 8006 polling did not succeed, 8007 file retrieval failed, 8008 unexpected error, 8009 company table not found for a company-limited user, 8010 report not assigned, 8011 workspace id empty. 8001 is retired.
- `PowerBIReportService` (5000-5999): 5001 report retrieval failed, 5005 export submission failed (status and response body), 5006 status request failed, 5007 file request failed.
- `UserContextResolver` logs warnings (no EventId) for an invalid workspace id or a user missing from the analytics database.

See `Logging-EventId-Registry.md`.

## 8. Endpoints

For the Power BI rows, *Handler* names the method that makes the call.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/UserReport/ExportReport` | `UserReportController.ExportReportAsync → ExportReportUseCase.ExportReportAsync` | `reportId`, `filters`, `exportType`; header `RequestVerificationToken` | the file | 200; 400; 401; 404 (section 6) |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}` | `PowerBIReportService.GetPowerBIReportAsync → Power BI` | bearer token | the report, including `datasetId` | 2xx; anything else → null, export 400 |
| `POST https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/ExportTo` | `PowerBIReportService.StartReportExportAsync → Power BI` | `format`, `powerBIReportConfiguration.reportLevelFilters` | export `id` and `status` | 2xx; anything else → null (5005), export 400 |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/exports/{exportId}` | `PowerBIReportService.PollExportRequestAsync → Power BI` | bearer token | `status`, `reportName`, `resourceFileExtension`, `error` | 2xx; anything else → null (5006), export 400 |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}/exports/{exportId}/file` | `PowerBIReportService.GetExportedFileAsync → Power BI` | bearer token | the file bytes | 2xx; anything else → null (5007), export 400 |
