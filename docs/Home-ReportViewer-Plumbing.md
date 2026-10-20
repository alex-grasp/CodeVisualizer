---
summary: How the Home page report viewer works - the category and report list, the default report, the Power BI embed config and token, the powerbi-client embed, the shared report state, preferences, error states, and logging; read before debugging or changing the report viewer.
area: architecture
---

# Home report viewer plumbing

This doc follows the report viewer on the Home page from loading the report list to showing an embedded Power BI report, and names what connects to what and what calls what. The Home page as a whole (the Razor host, the shell, the header, filters, export, and the AI panel) is covered in `Home-Plumbing.md`; this doc covers only `GraspANALYTICS/ClientApp/src/home/report-viewer/` and the server code it calls.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Viewer root | `GraspANALYTICS/ClientApp/src/home/report-viewer/ReportViewer.vue` | Exposes `menuAvailable`, `togglePanel()`, `reloadReport()` to `HomeShell.vue`. Owns `loadCatalog()`, `openReport()`, `toggleViewMode()`, `confirmDefaultReport()`, the side panel, the divider, the empty and failed notices, and both dialogs. |
| Image view | `GraspANALYTICS/ClientApp/src/home/report-viewer/CategoryThumbnails.vue` | Category menu plus report cards with thumbnails; emits `open` and `setDefault`. |
| List view | `GraspANALYTICS/ClientApp/src/home/report-viewer/CategoryList.vue` | Collapsible categories with report rows; marks the selected report; emits `open` and `setDefault`. |
| Default star | `GraspANALYTICS/ClientApp/src/home/report-viewer/DefaultReportStar.vue` | "Default Report" badge on the default report. |
| Set-default dialog | `GraspANALYTICS/ClientApp/src/home/report-viewer/SetDefaultReportDialog.vue` | `confirm()` and `close()`; emits `saved` and `closed`. |
| Embed host | `GraspANALYTICS/ClientApp/src/home/report-viewer/ReportEmbed.vue` | The element Power BI embeds into and the load-failure notice; exposes `load()` and `reload()` from `useReportEmbed`. |
| Embed logic | `GraspANALYTICS/ClientApp/src/home/report-viewer/useReportEmbed.ts` | `useReportEmbed()`, which returns `load()`, `reload()`, and `loadFailure`. |
| SDK wrapper | `GraspANALYTICS/ClientApp/src/home/report-viewer/powerBIEmbed.ts` | `embedReport()`, `resetEmbed()`, `embedWorkspaceId()`, `basicFilterType()`. |
| Filters | `GraspANALYTICS/ClientApp/src/home/report-viewer/powerBIFilters.ts` | `embedFilters()`, `filterProblem()`, `toODataFilter()`. |
| Shared report state | `GraspANALYTICS/ClientApp/src/home/report-viewer/reportState.ts` | `activeReport`, `setActiveReport()`, `setActiveReportMetadata()`, `markActiveReportRendered()`, `clearActiveReport()`; re-exports `toODataFilter()`. |
| Panel resize | `GraspANALYTICS/ClientApp/src/home/report-viewer/usePanelResize.ts` | `usePanelResize()`, which returns `width`, `resizing`, `startResize()`, `resetWidth()`. |
| Message dialog | `GraspANALYTICS/ClientApp/src/home/report-viewer/ViewerMessageDialog.vue` + `GraspANALYTICS/ClientApp/src/home/report-viewer/viewerMessages.ts` | `currentViewerMessage`, `showViewerMessage()`, `dismissViewerMessage()`, `clearViewerMessages()`. |
| Notice | `GraspANALYTICS/ClientApp/src/home/report-viewer/ReportNotice.vue` | Inline notice block with an optional ErrorID line. |
| Logging | `GraspANALYTICS/ClientApp/src/home/report-viewer/viewerLog.ts` → `GraspANALYTICS/ClientApp/src/shared/clientLogger.ts` | `logViewerEvent()`, `logViewerFailure()` → `logToServer()`. |
| API client | `GraspANALYTICS/ClientApp/src/home/report-viewer/api.ts` + `GraspANALYTICS/ClientApp/src/home/report-viewer/types.ts` | `getCategoriesAndReports()`, `setDefaultReport()`, `getReportMetadata()`, `getReportConfig()`, `getStoredViewMode()`, `saveViewMode()`; maps PascalCase responses to camelCase types. |
| HTTP and dialogs | `GraspANALYTICS/ClientApp/src/shared/http.ts` + `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `requestJson()`, `sendJson()`, `errorMessage()`; `handleNullSession()`. |
| Home host | `GraspANALYTICS/Controllers/HomeController.cs` + `GraspANALYTICS/Views/Home/Index.cshtml` | `GET /Home`; writes `data-allow-toggle-menu`. |
| Report list API | `GraspANALYTICS/Controllers/Api/UserReportController.cs` | `POST /api/UserReport/GetCategoriesAndReports`, `GET /api/UserReport/GetReportMetaData`, `POST /api/UserReport/SetDefaultReport`. |
| Embed config API | `GraspANALYTICS/Controllers/Api/PowerBIReportConfigController.cs` | `GET /api/PowerBIReportConfig/{reportId}`. |
| User settings API | `GraspANALYTICS/Controllers/Api/UserSettingsController.cs` | `GET /api/UserSettings`, `POST /api/UserSettings`. |
| Client log API | `GraspANALYTICS/Controllers/Api/ClientLoggerController.cs` | `POST /api/ClientLogger`. |
| User context | `GraspANALYTICS/Helpers/RequireUserContextAttribute.cs` → `GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs` | `OnActionExecutionAsync` → `GetAsync`. |
| PascalCase output | `GraspANALYTICS/Helpers/CaseInsensitiveJsonAttribute.cs` | `OnActionExecuted`: replaces the output formatter with default `System.Text.Json` options. |
| Catalog service | `GraspANALYTICS.Core/Application/CategoriesAndReports/CategoriesAndReportsService.cs` | `GetForUserAsync`. |
| Category repositories | `GraspANALYTICS.Core/Application/CategoriesAndReports/Repos/ReportCategoryRepository.cs` + `CategoryRepository.cs` + `ReportRepository.cs` | `GetAsync` on each. |
| Report assignment | `GraspANALYTICS.Core/Application/Services/UserReports/UserReportService.cs` | `GetValidUserReportsAsync`, `GetReportMetaDataAsync`, `SetDefaultReportAsync`. |
| Report assignment data | `GraspANALYTICS.Core/Application/Services/UserReports/Repos/UserReportRepository.cs` | `GetAsync`, `GetDefaultAsync`, `GetValidIdListAsync`, `IsAssignedAsync`, `GetReportMetaDataAsync`, `SetDefaultAsync`. |
| Embed config use case | `GraspANALYTICS.Core/Application/UseCases/GetPowerBIReportConfigUseCase.cs` | `ExecuteAsync`. |
| Table names | `GraspANALYTICS.Core/Application/UseCases/DefaultTableResolver.cs` | `Resolve`. |
| Data sources | `GraspANALYTICS.Core/Application/Services/DataSourceService.cs` | `GetUserConsolidatedIDsAsync`. |
| Custom filter row | `GraspANALYTICS.Core/Application/SortFilters/SortFilterRepository.cs` | `GetSortFilterAsync`. |
| Power BI REST | `GraspANALYTICS.Core/Infrastructure/PowerBI/PowerBIReportService.cs` | `GetPowerBIReportConfigAsync`. |
| Dataset metadata | `GraspANALYTICS.Core/Infrastructure/PowerBI/DatasetMetadataService.cs` | `GetAsync`. |
| Entra token | `GraspANALYTICS.Core/Infrastructure/EntraIdService/EntraIdService.cs` | `GetAccessTokenAsync`. |
| User settings service | `GraspANALYTICS.Core/Application/UserSettings/UserSettingsService.cs` + `Repos/UserSettingsRepository.cs` | `GetAllAsync`, `CreateAsync`, `GetReportToggleAsync`. |
| Report model | `GraspANALYTICS.Data/Models/Report.cs` | The `ganReport` row returned in the report list. |

`PowerBiCacheController` (`POST /api/PowerBiCache/ResyncPowerBIReports`, `POST /api/PowerBiCache/RemoveDeletedPowerBIReports`, Super Admin only) is not called by the viewer.

## 1. Loading the category and report list

### The host page

```text
Browser GET /Home
  └─ HomeController.Index                                         [CustomAuthorize]
       ├─ no app session → redirect to /Login
       ├─ HomePresenter.InitializeViewData(appSession, ViewData)
       └─ UserSettingsService.GetReportToggleAsync() → ViewBag.AllowToggleMenu
            └─ SettingsRepository.GetGlobalBooleanSettingAsync("ReportThumbnailToggleMenu")
                  true when the global value is true, on, or 1
```

`Views/Home/Index.cshtml` writes the value into `data-allow-toggle-menu`, and `HomeShell.vue` renders `<ReportViewer :allow-toggle-menu="settings.allowToggleMenu" />`.

### The catalog request

```text
Browser GET /Home (ReportViewer.vue mounts)
  └─ ReportViewer.loadCatalog()
       ├─ api.getCategoriesAndReports()      → POST /api/UserReport/GetCategoriesAndReports
       ├─ ReportViewer.storedViewMode()
       │     allowToggleMenu false → List Mode, no request
       │     └─ api.getStoredViewMode()      → GET /api/UserSettings          (section 4)
       │           throws → viewerLog.logViewerFailure(), Image Mode
       ├─ savedFilters.loadSavedFilters()    → GET /api/DefaultFilters        see Home-Filters-Plumbing.md §1
       ├─ categories present → status 'ready'
       │     └─ ReportEmbed.load(defaultPowerBIReportId)                       (section 3)
       ├─ no categories → status 'empty'
       └─ any of the three throws → status 'failed'
            ├─ dialogs.handleNullSession(error)
            └─ viewerLog.logViewerFailure("The reports list or the saved filters failed to load", error)
```

- The three calls run in parallel (`Promise.all`).
- A failure of the saved-filters call also fails the catalog. A failure of the view-mode read does not: it is logged and the view falls back to Image Mode.

On the server:

```text
UserReportController.GetCategoriesAndReportsAsync          [RequireUserContext], [CaseInsensitiveJson]
  └─ CategoriesAndReportsService.GetForUserAsync(userContext)
       ├─ UserId or AgencyId is 0 → CategoriesAndReportsResponse.Empty
       ├─ UserReportService.GetValidUserReportsAsync(userId)
       │     any exception → empty list
       │     └─ UserReportRepository.GetValidIdListAsync(userId)
       │           ganUserReport rows for the user whose ganReport row (matched on PowerBIReportID)
       │           is not IsDeleted → ReportID list
       ├─ CategoriesAndReportsService.ResolveDefaultReportAsync(userId, validReportIds)
       │    ├─ UserReportRepository.GetDefaultAsync(userId)              ganUserReport.IsDefault
       │    │     not in the valid list → ignored
       │    └─ no default → UserReportRepository.GetAsync(userId, first valid ReportID)
       └─ CategoriesAndReportsService.BuildCategoryTreeAsync(validReportIds, defaultReportId, userContext)
            └─ CategoriesAndReportsService.GetCategoryAssignmentAsync(userContext, reportId)
                 ├─ ReportCategoryRepository.GetAsync(reportId, agencyId)
                 │     ganReportCategory with CategoryID > 0 and AgencyID = the user's agency or 0;
                 │     the agency row wins over the global row
                 ├─ CategoryRepository.GetAsync(categoryId)              ganCategory
                 ├─ ReportRepository.GetAsync(reportId)                  ganReport
                 └─ no category link, category, or report → the report is left out
```

- `BuildCategoryTreeAsync` calls `GetCategoryAssignmentAsync` once per valid ReportID, in list order, and groups the reports by category. The category of the default report becomes `DefaultCategoryID`.

What decides which reports a user sees:

- **Assignment**: a `ganUserReport` row for the user. This is the only per-user check; the role does not filter reports.
- **Not deleted**: the matching `ganReport` row is not `IsDeleted`. `IsActive` is not checked.
- **Agency**: the report must have a category link for the user's agency or a global one (`AgencyID = 0`). The agency also scopes the data inside the report (section 3a).
- **Company scope** is not applied to the list. It is applied inside the report through the embed filters.

`UserReportService.GetValidUserReportsAsync` catches every exception and returns an empty list, so a database failure here shows the "no reports" notice, not the "could not be loaded" notice.

`[CaseInsensitiveJson]` replaces the output formatter with default `System.Text.Json` options, so this response is **PascalCase**. Each report is the whole `ganReport` model (`GraspANALYTICS.Data/Models/Report.cs`); `api.ts` reads `ReportID`, `PowerBIReportID`, `ReportName` (the `Name`, or `PowerBIName` when `Name` is blank), and `Thumbnail` (the `byte[]` as base64).

## 2. Choosing a report and the default report

### Opening the side panel

The side panel starts closed.

```text
Browser click "Reports menu" (HeaderBrand.vue)
  └─ HomeShell.toggleReportMenu()
       └─ ReportViewer.togglePanel()
            ├─ status not 'ready' → nothing
            ├─ panel open → ReportViewer.closePanel()
            └─ panel closed → opens the panel
```

- `HeaderBrand.vue` shows the button only while `ReportViewer.menuAvailable` is true, which is while the status is `loading` or `ready`.

### Opening a report

| View | Open a report | Set the default | Notes |
|---|---|---|---|
| Image (`CategoryThumbnails`) | click a card | right-click a card | The category under the mouse or focus becomes active; starts on `defaultCategoryId`. Cards show `data:image/jpeg;base64,<Thumbnail>` or `/images/reports/GenericReport.jpg`. "No reports in this category" for an empty category. |
| List (`CategoryList`) | click a row | right-click a row | Every category starts expanded; the header toggles it. The selected report gets `is-selected` and `aria-current`. |

```text
Browser click a report card (CategoryThumbnails.vue) or row (CategoryList.vue)
  └─ emits open
       └─ ReportViewer.openReport(report)
            ├─ ReportViewer.closePanel()
            └─ ReportEmbed.load(report.powerBIReportId)                (section 3)
```

- `ReportViewer.openReport` also sets `selectedReportId`, which `CategoryList` marks.

### Setting the default report

The star (`DefaultReportStar`) is shown on the report whose `reportId` equals `defaultReportId`. Its tooltip is "Default Report - Right click on a report to set a different default".

```text
Browser right-click a report card (CategoryThumbnails.vue) or row (CategoryList.vue)
  └─ emits setDefault
       └─ ReportViewer.confirmDefaultReport(report)
            opens SetDefaultReportDialog: "Make this your default report. Are you sure?"
```

```text
Browser click "Yes" (SetDefaultReportDialog.vue)
  └─ SetDefaultReportDialog.confirm()
       ├─ api.setDefaultReport(reportId)        → POST /api/UserReport/SetDefaultReport, body: the bare number
       ├─ success
       │    ├─ emits saved → ReportViewer.defaultReportSaved(report)      the star moves
       │    └─ viewerMessages.showViewerMessage()   "Success" / "Default report set"
       ├─ failure
       │    ├─ dialogs.handleNullSession(error)
       │    ├─ viewerLog.logViewerFailure(...)
       │    └─ viewerMessages.showViewerMessage()   "Error" / "Unable to set default report" + ErrorID
       └─ always → emits closed → ReportViewer.closeDefaultReportDialog()
```

```text
Browser click "No", X, or Esc (SetDefaultReportDialog.vue)
  └─ SetDefaultReportDialog.close()
       saving → ignored
```

On the server:

```text
UserReportController.SetDefaultReportAsync                [RequireUserContext], [ValidateAntiForgeryToken]
  ├─ UserReportService.SetDefaultReportAsync(userContext, reportId)
  │    ├─ UserReportRepository.GetValidIdListAsync(userId)
  │    │     reportId not in the list → false
  │    └─ UserReportRepository.SetDefaultAsync(userId, reportId)
  │         ├─ no ganUserReport row for (user, report) → false
  │         ├─ already the default → true
  │         └─ IAsyncEntityConnector.BatchUpdateAsync(changedRows)
  │               clears IsDefault on the user's other rows and sets it on this one;
  │               true when every row was updated
  ├─ true → 200 { status: "success" }
  └─ false → 404 { status: "error", message: "The report is not available to the current user." }
```

The default is stored as `ganUserReport.IsDefault`. Setting it does not open the report.

## 3. Embedding a report

`ReportEmbed.vue` holds the host `<div>` and exposes `load` and `reload` from `useReportEmbed(host)`.

```text
Browser opens a report (the default on load, a clicked report, or a reload after Filters)
  └─ useReportEmbed.load(powerBIReportId)
       ├─ no id or no host element → nothing
       ├─ reportState.clearActiveReport()
       ├─ powerBIEmbed.resetEmbed(host)
       │    └─ powerbi.reset(host)
       ├─ api.getReportConfig(powerBIReportId)          → GET /api/PowerBIReportConfig/{id}     (section 3a)
       │     no value in the body → throws "The report configuration could not be loaded."
       │     throws → useReportEmbed.showLoadFailure(error, powerBIReportId)                   (section 5)
       └─ useReportEmbed.embed(host, powerBIReportId, config, loadId)
            ├─ savedFilters.savedReportFilters()        saved filters plus custom filters     see Home-Filters-Plumbing.md
            ├─ powerBIFilters.embedFilters(config, userFilters, powerBIEmbed.basicFilterType())
            │     config.filters and config.sortFilters as Basic filters, then the user filters
            ├─ powerBIFilters.filterProblem(filters)
            │     no filters at all → "An issue loading the report was found! Please try again later."
            │     a filter without table or column → "Filter <n> is missing a target or column property."
            │     a problem → viewerLog.logViewerEvent(), loadFailure with contactAdministrator, no embed
            ├─ powerBIEmbed.embedReport(host, config, filters)                                (section 3b)
            ├─ reportState.setActiveReport(identity, userFilters)                             (section 3c)
            │    └─ powerBIEmbed.embedWorkspaceId(config.embedUrl)      the embed URL's groupId
            ├─ useReportEmbed.watchReportEvents(report, powerBIReportId)
            │    ├─ error event → useReportEmbed.showEmbedError(detail)                       (section 3b)
            │    └─ rendered event → reportState.markActiveReportRendered(powerBIReportId)
            └─ useReportEmbed.loadMetadata(powerBIReportId, loadId)
                 ├─ api.getReportMetadata(powerBIReportId)   → GET /api/UserReport/GetReportMetaData?powerBIReportID=<id>
                 ├─ success → reportState.setActiveReportMetadata(powerBIReportId, metadata)
                 └─ failure
                      ├─ dialogs.handleNullSession(error)
                      ├─ viewerLog.logViewerFailure(...)
                      └─ viewerMessages.showViewerMessage()   "Error" / "Error getting report metadata."
```

- Each `load` increments a load generation and clears `loadFailure`. A config, embed, or metadata result from an older load is dropped once a newer load has started.

### 3a. How the server builds the embed config

```text
PowerBIReportConfigController.Get(reportId)                 [Authorize], [RequireUserContext]
  └─ GetPowerBIReportConfigUseCase.ExecuteAsync(reportId, userContext)
       ├─ UserReportRepository.IsAssignedAsync(userId, reportId)
       │     no ganUserReport row, or the ganReport row is IsDeleted → Fail → 404              (6005)
       ├─ WorkspaceId empty → PowerBiWorkspaceMissingException → 404                           (6004)
       ├─ PowerBIReportService.GetPowerBIReportConfigAsync(workspaceId, reportId)
       │    ├─ EntraIdService.GetAccessTokenAsync(scope)           MSAL client credentials (app id, secret, tenant)
       │    ├─ PowerBIReportService.FetchPowerBIReportAsync()      → GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}
       │    │     any failure → null (5001) → Fail "Unable to get report {id}" → 404            (6002)
       │    └─ PowerBIReportService.GetPowerBITokenAsync()         → POST https://api.powerbi.com/v1.0/myorg/GenerateToken
       │          not 2xx or empty body → PowerBiApiRequestFailedException → 502                (5004)
       ├─ DatasetMetadataService.GetAsync(datasetId, workspaceId)
       ├─ DefaultTableResolver.Resolve(tables)                     report table "ReportData"
       ├─ DefaultTableResolver.Resolve(tables, "Corps")            company table
       │     either missing → returns the config as is (no data scope or custom filters)
       ├─ GetPowerBIReportConfigUseCase.BuildDataScopeFiltersAsync(reportTable, corporationTable, userContext)
       │    ├─ DataSourceService.GetUserConsolidatedIDsAsync(agencyId)
       │    │     ConsolidatedDataSources children of the agency, else [agencyId]
       │    │     none → PowerBiDataSourceScopeMissingException → 403                           (6003)
       │    ├─ <ReportData>/DataSourceID In [ids]
       │    └─ DataSecurityCorporationId > 0 → <Corps>/CompanyID In [that id]
       ├─ GetPowerBIReportConfigUseCase.BuildCustomFiltersAsync(reportTable, userContext)
       │    ├─ CorporationId 0 or less → no custom filters
       │    ├─ SortFilterRepository.GetSortFilterAsync(userId, corporationId)    ganSortFilter
       │    └─ each non-blank Sort1..Sort10 → <ReportData>/SortN In [value]
       └─ GetPowerBIReportConfigUseCase.GetCultureCode(agencyId)   ganSetting "CultureCode" for the agency, or ""
```

- `PowerBIReportService.GetPowerBITokenAsync` sends `reports: [id]`, `datasets: [datasetId]`, `targetWorkspaces: [ws]`.
- `DatasetMetadataService.GetAsync` caches the metadata under `pbi-meta:{ws}:{ds}` for the agency's Foundry `MetadataCacheMinutes` (the settings model defaults it to 15; 30 when no Foundry settings are found). On a cache miss it reads `ganReport.DataTablesMetaData`; when that is empty it calls Power BI REST (workspace and dataset) and XMLA (tables, columns, measures), then saves the result to `ganReport.DataTablesMetaData`.
- `DefaultTableResolver.Resolve` takes the table with the exact name, else the first table whose name starts with it.

Where the scope comes from (`GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs`, run by `[RequireUserContext]`): `UserId`, `RoleId`, `CorporationId`, and `AgencyId` from the app session's current user; `DataSecurityCorporationId` from `ganUser.DataSecurityCorporationID`; `WorkspaceId` from `AppSession.PowerBI.PowerBIWorkspaceID` (read by `PowerBISettings` from the global `ganSetting` row), or `Guid.Empty` when it is not a GUID.

The embed token carries no effective identity, so the data scope (agency data sources and the company) is applied only as embed filters, not as row-level security.

### 3b. What the client does with the SDK

`powerBIEmbed.ts` uses the global `powerbi` service that `powerbi-client` (2.23.10, bundled by Vite) creates on import. `powerBIEmbed.embedReport` calls `powerbi.embed(host, config)` with:

| Setting | Value |
|---|---|
| `type` | `'report'` |
| `tokenType` | `models.TokenType.Embed` |
| `accessToken` | `config.powerBIToken.token` |
| `embedUrl` | `config.embedUrl` |
| `filters` | the list from `powerBIFilters.embedFilters` |
| `settings` | `filterPaneEnabled: false`, `persistentFiltersEnabled: true`, `hideErrors: true`; `localeSettings: { language, formatLocale }` only when `cultureCode` is not blank |

- Only the `error` and `rendered` events are handled. There is no `loaded` handler and no bookmark code.
- `hideErrors: true` turns off Power BI's own error overlay. `useReportEmbed.showEmbedError` logs the event and shows "Error Loading PowerBI Embedded Report" with "Error getting PowerBI report. <detailedMessage>" and an ErrorID.
- On unmount the embed is reset and the active report cleared.

Filters are set once, at embed time. Applying filters from the header reloads the report:

```text
Browser click "Save" in the Filters panel (FilterPanel.vue)                    see Home-Filters-Plumbing.md
  └─ ReportFilters.save(selection)
       └─ saved → emits applied
            └─ HomeShell.reloadReport()
                 └─ ReportViewer.reloadReport()
                      └─ ReportEmbed.reload()
                           ├─ no report requested yet → viewerMessages.showViewerMessage() "Error" / "Cannot get current report ID"
                           └─ useReportEmbed.load(last requested id)      new config and new token (section 3)
```

### 3c. The report state it publishes

`reportState.ts` holds one frozen `activeReport` (or `null` while loading):

| Field | Set by | Read by |
|---|---|---|
| `powerBIReportId` | `reportState.setActiveReport`, right after `powerBIEmbed.embedReport` | `export-report/reportPage.ts` (`currentReport()`) |
| `workspaceId` | `reportState.setActiveReport` (the embed URL's `groupId`) | not read outside the viewer |
| `userFilters` | `reportState.setActiveReport` | not read outside the viewer (export uses `savedFilters.savedExportFilters()`) |
| `metadata` (`datasetId`, `fixedDate`) | `reportState.setActiveReportMetadata` | `ReportFilters.vue` (`fixedDate`), `ai-query/reportPage.ts` (`datasetId`) |
| `rendered` | `reportState.markActiveReportRendered` on the first `rendered` event | `export-report/reportPage.ts` (`useExportAvailability()`) |

Updates for a report id that is no longer current are ignored. `reportState.ts` also re-exports `toODataFilter` for export.

## 4. View mode, panel, and preferences

| Preference | Stored where | Read | Saved |
|---|---|---|---|
| View mode (`Image Mode` / `List Mode`) | `ganUserSetting`, key `ReportMode`, per `UserID` + `AgencyID` | `GET /api/UserSettings` on load; no row → Image Mode; any value other than `Image Mode` → List Mode | `POST /api/UserSettings { key: "ReportMode", value }` on each toggle |
| Toggle allowed | global `ganSetting` `ReportThumbnailToggleMenu` | `HomeController.Index` → `data-allow-toggle-menu` | not changed by the viewer |
| Panel width | component state only | - | not persisted |

- When `allowToggleMenu` is false, the view is always List Mode, the settings read is skipped, and the panel toolbar (mode toggle and close button) is not shown.
- The toolbar button shows `/images/icons/imageIcon.png` or `/images/icons/listIcon.png`.

```text
Browser click the view-mode button (ReportViewer.vue)
  └─ ReportViewer.toggleViewMode()
       ├─ switches between Image Mode and List Mode
       ├─ usePanelResize.resetWidth()
       └─ ReportViewer.persistViewMode(mode)
            └─ api.saveViewMode(mode)       → POST /api/UserSettings
                  throws → dialogs.handleNullSession(), viewerLog.logViewerFailure()
```

On the server:

```text
UserSettingsController.GetUserSettingsAsync               [RequireUserContext], [CaseInsensitiveJson]
  └─ UserSettingsService.GetAllAsync(userContext)
       any exception → []
       └─ UserSettingsRepository.GetAllAsync(userContext)      ganUserSetting rows for UserID + AgencyID
```

```text
UserSettingsController.SaveUserSettingAsync               [RequireUserContext]
  ├─ UserSettingsService.CreateAsync(userContext, request)
  │     blank key or null value → nothing saved
  │     any exception → logged and swallowed
  │     └─ UserSettingsRepository.CreateAsync(userContext, key, value)    updates the existing row or creates one
  └─ returns 200 { status: "success" }
```

`UserSettingsService` swallows exceptions, so `GET` returns `[]` and `POST` returns 200 even when the database call fails.

The divider (`category-panel-gutter`) between the panel and the report:

```text
Browser mousedown on the divider (ReportViewer.vue)
  └─ ReportViewer.startDividerDrag(event)
       ├─ report hidden → nothing
       └─ usePanelResize.startResize(event)
            ├─ mousemove → usePanelResize.drag(event)
            └─ mouseup → usePanelResize.endResize()
```

- `usePanelResize.drag` sets the width to the start width plus the mouse movement, kept between 350 px and the parent width minus 312 px (300 px for the report plus the 12 px gutter).
- While dragging, a full-screen overlay blocks the report iframe.

```text
Browser double-click on the divider (ReportViewer.vue)
  └─ usePanelResize.resetWidth()          back to the CSS width
```

```text
Browser click "Hide the report list" (ReportViewer.vue)
  └─ ReportViewer.closePanel()
```

```text
Browser click "Hide the report" (ReportViewer.vue)
  └─ ReportViewer.hideReport()            the panel fills the page
```

```text
Browser click "Show the report" (ReportViewer.vue)
  └─ ReportViewer.showReport()
```

## 5. Empty, error, and notice states

| State | Where it shows | Text |
|---|---|---|
| No categories | `ReportViewer`, `ReportNotice.no-reports` | "You currently have no reports." / "Contact an administrator to have reports assigned to your account." |
| Catalog or saved filters failed | `ReportViewer`, `ReportNotice.reports-unavailable`, `role="alert"` | "Your reports could not be loaded." / "Refresh the page to try again. If the problem continues, contact an administrator." + ErrorID |
| Config refused (status below 500) | `ReportEmbed`, `ReportNotice.report-load-error` | the body's `errorMessage` or `message` + "Contact an administrator to have this report fixed." + ErrorID |
| Config failed (500 and above, network, empty body) | same | "We're having trouble loading this report right now. Our team has been notified and is working on it. Please try again later." + ErrorID |
| Filter check failed | same | the `powerBIFilters.filterProblem` text + "Contact an administrator..." + ErrorID |
| Power BI `error` event | message dialog | section 3b |
| Metadata failed | message dialog | "Error getting report metadata." |
| Default report saved / failed | message dialog | "Default report set" / "Unable to set default report" |

`useReportEmbed.showLoadFailure` picks between the two config rows: an `ApiError` with a status below 500 is a refusal; anything else gets the generic text.

The message dialog (`ViewerMessageDialog.vue`) is a PrimeVue modal bound to `viewerMessages.currentViewerMessage`, the head of the queue. `viewerMessages.showViewerMessage` appends; OK, X, or Esc calls `viewerMessages.dismissViewerMessage`, which shows the next one. `viewerMessages.clearViewerMessages` empties the queue when the dialog unmounts.

Every failed call also passes the error to `dialogs.handleNullSession` (`GraspANALYTICS/ClientApp/src/shared/dialogs.ts`): it calls a global `HandleNullSession(status)` when one exists, otherwise a 401 sends the browser to `/Error?statusCode=401`.

## 6. Logging

```text
Browser call fails in the viewer (sections 1 to 5)
  └─ viewerLog.logViewerFailure(summary, error)
       ├─ viewerLog.logViewerEvent("<summary>: <message> (status <n>)")
       │    └─ clientLogger.logToServer("ReportViewer", message)
       │         ├─ crypto.randomUUID() → errorId         returned at once and shown as "ErrorID: ..."
       │         └─ http.sendJson('POST', '/api/ClientLogger', body)    → POST /api/ClientLogger, fire and forget
       │               fails → console.error()
       └─ console.error() with the summary and the ErrorID
```

On the server:

```text
ClientLoggerController.LogClientErrorAsync                [RequireUserContext]
  ├─ IsDebug (never sent by the viewer) → IAnalyticsLogger.LogDebug("Client[{ModuleName}] {Message}")
  ├─ otherwise → IAnalyticsLogger.LogError("Client[{ModuleName}] Exception {Message}. ErrorID={ErrorId}")
  └─ returns 200 { status: "success" }
```

Server-side log lines on the embed path: `PowerBIReportService` 5001 (report retrieval) and 5004 (GenerateToken); `GetPowerBIReportConfigUseCase` 6002-6005. See `Logging-EventId-Registry.md`.

## 7. Endpoints

All app calls go through `GraspANALYTICS/ClientApp/src/shared/http.ts` (`appUrl` for the virtual directory). `http.sendJson` adds the `RequestVerificationToken` header from the page's antiforgery field; of the viewer's calls only `SetDefaultReport` validates it. For the Power BI rows, *Handler* names the method that makes the call.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/UserReport/GetCategoriesAndReports` | `UserReportController.GetCategoriesAndReportsAsync → CategoriesAndReportsService.GetForUserAsync` | no body | PascalCase: `CategoriesAndReports[]` (`CategoryID`, `Name`, `DefaultReportID`, `Reports[]` of `ganReport` fields incl. `ReportID`, `PowerBIReportID`, `ReportName`, `Thumbnail`), `DefaultReportID`, `DefaultPowerBIReportID`, `DefaultCategoryID` | 200; 401 no user context |
| `GET /api/UserReport/GetReportMetaData?powerBIReportID=` | `UserReportController.GetReportMetaDataAsync → UserReportService.GetReportMetaDataAsync` | query `powerBIReportID` | PascalCase: `TmcOnly`, `FixedDate`, `DatasetId` | 200; 401; 404 `{ status, message }` (blank id, not assigned, deleted, missing, or any error) |
| `POST /api/UserReport/SetDefaultReport` | `UserReportController.SetDefaultReportAsync → UserReportService.SetDefaultReportAsync` | JSON number | `{ status: "success" }` | 200; 400 antiforgery; 401; 404 `{ status, message }` |
| `GET /api/PowerBIReportConfig/{reportId}` | `PowerBIReportConfigController.Get → GetPowerBIReportConfigUseCase.ExecuteAsync` | route `Guid` | `{ status, message, value }`, `value` = `embedUrl`, `filters[]`, `sortFilters[]` (each `target { table, column }`, `logic`, `schema`, `filterType`, `operator`, `values`, `requireSingleSelection`), `powerBIToken { token, tokenId, expiration }`, `cultureCode` | 200; 401; 403; 404; 500; 502; 503 (table below) |
| `GET /api/UserSettings` | `UserSettingsController.GetUserSettingsAsync → UserSettingsService.GetAllAsync` | none | PascalCase `ganUserSetting` rows (`Key`, `Value`, ...) | 200 (`[]` on a database failure); 401 |
| `POST /api/UserSettings` | `UserSettingsController.SaveUserSettingAsync → UserSettingsService.CreateAsync` | `{ key, value }` (`SaveUserSettingRequest`) | `{ status: "success" }` | 200 (also on a database failure); 401 |
| `POST /api/ClientLogger` | `ClientLoggerController.LogClientErrorAsync → IAnalyticsLogger.LogError` | `{ moduleName, message, errorId }` (`ClientErrorModel`) | `{ status: "success" }` | 200; 401 |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/reports/{id}` | `PowerBIReportService.GetPowerBIReportConfigAsync → Power BI` | bearer token | the report, incl. `datasetId` and `embedUrl` | 2xx; anything else → config 404 "Unable to get report {id}" |
| `POST https://api.powerbi.com/v1.0/myorg/GenerateToken` | `PowerBIReportService.GetPowerBIReportConfigAsync → Power BI` | `reports`, `datasets`, `targetWorkspaces` | `token`, `tokenId`, `expiration` | 2xx; anything else → `PowerBiApiRequestFailedException`, config 502 |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}` | `DatasetMetadataService.GetAsync → Power BI` | bearer token; only on a metadata cache and database miss | the workspace name | 2xx; anything else → `PowerBiApiRequestFailedException`, config 502 |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{ws}/datasets/{ds}` | `DatasetMetadataService.GetAsync → Power BI` | bearer token; only on a metadata cache and database miss | the dataset name | 2xx; anything else → `PowerBiApiRequestFailedException`, config 502 |

`GET /api/PowerBIReportConfig/{reportId}` statuses (the body is `{ status: "error", message }`):

| Cause | Status | `message` |
|---|---|---|
| Report not assigned or deleted | 404 | "The report is not available to the current user." |
| Power BI report lookup failed | 404 | "Unable to get report {reportId}" |
| No workspace id (`PowerBiWorkspaceMissingException`) | 404 | "The Power BI workspace is not configured for this agency." |
| `PowerBiDataSourceScopeMissingException` | 403 | "No data source scope is available for the current user." |
| `PowerBiTableNotFoundException` | 500 | "The Power BI dataset is missing a required table." |
| `PowerBiApiRequestFailedException` (for example GenerateToken) | 502 | "The Power BI service could not be reached. Please try again." |
| `XmlaConnectionFailedException` | 503 | "The Power BI dataset could not be queried." |
| any other `DomainException` (for example Entra token failure) | 500 | "An unexpected error occurred while preparing the report." |
| No user context | 401 | - |

The client shows the server `message` only for statuses below 500 (the 404s and the 403); 500, 502, and 503 show the generic "We're having trouble..." text.
