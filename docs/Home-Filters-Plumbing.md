---
summary: How the Home page Filters panel works - the Vue panel and its option lists, the filter endpoints and the company scope they apply, the saved filters in ganUserFilter, and how a saved selection becomes Power BI filters on the embedded report; read before debugging or changing anything in the Filters panel.
area: architecture
---

# Home Filters plumbing

This doc follows a user from clicking **Filters** in the Home header to the report showing the filtered data, and names what connects to what and what calls what on the way. It is one part of the Home page; for the overview see Home-Plumbing.md §1, and for the report embed itself see Home-ReportViewer-Plumbing.md §3.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Header button + save flow | `GraspANALYTICS/ClientApp/src/home/report-filters/ReportFilters.vue` | The **Filters** button: `togglePanel()`, `openPanel()`, `save()`, `persist()`, `resolveAccountIds()`; emits `applied`. Opens the panel once saved filters are loaded, saves the selection, then asks the shell to reload the report. |
| Panel | `GraspANALYTICS/ClientApp/src/home/report-filters/FilterPanel.vue` | The docked side panel: `initializeDraft()`, `refreshFields()`, `loadFields()`, `changeCompanies()`, `changeAccounts()`, `scheduleDateRangeUpdate()`, `updateDateRange()`, `addCustomFilter()`, `removeCustomFilter()`, `changeCustomFilterField()`, `loadCustomFilterValues()`, `setExpanded()`, `clear()`, `close()`, `requestSave()`; emits `save` and `update:open`. Holds the draft selection, loads option lists, checks the dates, builds the selection to save. |
| Section | `GraspANALYTICS/ClientApp/src/home/report-filters/FilterSection.vue` | A collapsible block with `title`, `summary`, optional `error`, an `actions` slot; emits `update:expanded`. |
| Checkbox list | `GraspANALYTICS/ClientApp/src/home/report-filters/CheckboxList.vue` | `load()`, `loadNextPage()`, `onSearch()`, `clearSearch()`, `retry()`, `retryMore()`, `setSelected()`, `setVisibleSelected()`, `selectAllOrClear()`; emits `update:modelValue` and `load-error`. Search box, "Select / Unselect Visible", "Select all / Clear all", a virtual-scrolled list loaded 50 at a time. |
| Custom filter row | `GraspANALYTICS/ClientApp/src/home/report-filters/CustomFilterRow.vue` | `loadOptions()`, `changeOperator()`; emits `update:field`, `update:operator`, `update:values`, `remove`, `load-error`. One custom filter: field picker, `in` / `not in` operator, and a `CheckboxList` of values. |
| API client | `GraspANALYTICS/ClientApp/src/home/report-filters/api.ts` | `getSavedFilters()`, `saveFilters()`, `getCompanyOptions()`, `getAccountOptions()`, `getCustomFilterFields()`, `getCustomFilterValues()`, `getDefaultDateRange()`. Maps the PascalCase server bodies to camelCase client types. |
| Saved filters store | `GraspANALYTICS/ClientApp/src/home/report-filters/savedFilters.ts` | `savedFilters`, `loadSavedFilters()`, `setSavedFilters()`, `savedReportFilters()`, `savedExportFilters()`, `savedFilterDateRange()`. Module-level copy of the user's saved filters; hands Power BI filters to the embed, the export, and the AI panel. |
| Power BI filter builder | `GraspANALYTICS/ClientApp/src/home/report-filters/powerBIFilters.ts` | `exportFilters()`, `reportUserFilters()`. Saved filters to Power BI basic and advanced filter objects. |
| Option cache | `GraspANALYTICS/ClientApp/src/home/report-filters/optionCache.ts` | `createOptionCache()`, which returns `loadPage()` and `reset()`. Fetches a full list once and serves search and paging from memory. |
| Custom filter helpers | `GraspANALYTICS/ClientApp/src/home/report-filters/customFilters.ts` | `MAX_CUSTOM_FILTERS`, `CUSTOM_FILTER_OPERATORS`, `defaultCustomFilterFields()`, `withDisplayNames()`, `nonEmptyValues()`, `parseCustomFilterValues()`, `serializeCustomFilterValues()`. Field list (`Sort1`-`Sort10`, `Domestic International`), operators, the 10-filter limit, value parsing and serializing. |
| Date helpers | `GraspANALYTICS/ClientApp/src/home/report-filters/filterDates.ts` | `calendarDate()`, `calendarDateText()`, `utcMidnightIso()`, `nextDayUtcMidnightIso()`, `shortDateText()`, `defaultFilterDates()`, `reportFilterDateRange()`. Calendar-date parsing, UTC-midnight ISO strings, the default Jan 1 - today range. |
| Panel preferences | `GraspANALYTICS/ClientApp/src/home/report-filters/panelPreferences.ts` + `GraspANALYTICS/ClientApp/src/shared/useDockedPanel.ts` | `storedCollapsedSections()`, `storeCollapsedSections()`, `PANEL_WIDTH_KEY`; `useDockedPanel()`, which returns `width`, `resizing`, `startResize()`, `resetWidth()`. Collapsed sections and panel width in `localStorage`. |
| Side panel exclusivity | `GraspANALYTICS/ClientApp/src/shared/sidePanels.ts` | `useExclusiveSidePanel()`, `announceSidePanelOpened()`. Only one Home side panel is open at a time. |
| Dialogs and session | `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` → `GraspANALYTICS/wwwroot/js/site.js` | `showOk()`, `showConfirm()`, `handleNullSession()`; they call the page's `ShowOkDialog`, `ShowConfirmDialog`, `HandleNullSession`. |
| Shared report state | `GraspANALYTICS/ClientApp/src/home/report-viewer/reportState.ts` | `activeReport`, `setActiveReport()`, `clearActiveReport()`, `toODataFilter()`. Read here for `metadata.fixedDate`. |
| Embed | `GraspANALYTICS/ClientApp/src/home/report-viewer/useReportEmbed.ts` + `GraspANALYTICS/ClientApp/src/home/report-viewer/powerBIEmbed.ts` + `GraspANALYTICS/ClientApp/src/home/report-viewer/powerBIFilters.ts` | `useReportEmbed()`, which returns `load()` and `reload()`; `embedReport()`, `resetEmbed()`, `basicFilterType()`; `embedFilters()`, `filterProblem()`, `toODataFilter()`. Puts the saved filters into the embed configuration. |
| Saved filters API | `GraspANALYTICS/Controllers/Api/DefaultFiltersController.cs` | `GET /api/DefaultFilters`, `POST /api/DefaultFilters`. |
| Company and account lists API | `GraspANALYTICS/Controllers/Api/FilterReferenceDataController.cs` | `GET /api/FilterReferenceData/GetCompanyNameFilter`, `POST /api/FilterReferenceData/GetAccountNameFilter`. |
| Custom filters and date range API | `GraspANALYTICS/Controllers/Api/CustomFiltersController.cs` | `POST /api/CustomFilters/GetSortFilterDefinitions`, `POST /api/CustomFilters/GetVirtualizedSortFilterValues`, `POST /api/CustomFilters/GetDefaultFilterDateRange`; also `POST /api/CustomFilters/GetSortFilterValues`, `POST /api/CustomFilters/GetSortFilterValueItems`, `GET /api/CustomFilters/GetDomIntValues`, which the panel does not call. |
| Saved filters service | `GraspANALYTICS.Core/Application/UserFilters/DefaultUserFilterService.cs` + `GraspANALYTICS.Core/Application/UserFilters/Repos/DefaultUserFilterRepository.cs` | `DefaultUserFilterService.GetAsync`, `DefaultUserFilterService.CreateOrUpdateAsync`; `DefaultUserFilterRepository.GetAsync`, `DefaultUserFilterRepository.CreateOrUpdateAsync`. Reads and writes the JSON row in `ganUserFilter`; adds the Power BI table names. |
| Reference data service | `GraspANALYTICS.Core/Application/UserFilters/FilterReferenceDataService.cs` | `FilterReferenceDataService.GetCompanyNameFilter`, `FilterReferenceDataService.GetAccountNameFilter`. Company and account lists from the agency database. It also has a `GetCompanyFilterDateRange` that no controller calls. |
| Custom filters service | `GraspANALYTICS.Core/Application/UserFilters/CustomFiltersService.cs` | `CustomFiltersService.GetSortFilterDefinitions`, `CustomFiltersService.GetCompanyFilterDateRange`, `CustomFiltersService.GetVirtualizedSortFilterValues`, plus `GetSortFilterValues`, `GetSortFilterValueItems`, `GetDomIntValues`. Sort labels, sort values, date range from the agency database. |
| Company scope | `GraspANALYTICS.Core/Application/UserFilters/CompanyScopeResolver.cs` | `CompanyScopeResolver.RestrictedCompanyId`, `CompanyScopeResolver.PermittedCompanyIds`. Limits a Company User or Company Admin to their own company. |

`CorporationsController` is not used by the Filters panel.

## 1. Opening the panel and loading the option lists

### Saved filters load with the page

The saved filters are loaded before the panel is ever opened:

```text
Browser GET /Home
  └─ ReportViewer.loadCatalog()                              (ReportViewer.vue, on mount)
       ├─ api.getCategoriesAndReports()                      (report-viewer/api.ts)
       ├─ ReportViewer.storedViewMode()
       ├─ savedFilters.loadSavedFilters()                    one request in flight at a time
       │    └─ api.getSavedFilters()  → GET /api/DefaultFilters    (section 4)
       │         └─ fills savedFilters with startDate, endDate, companyIds, accountIds,
       │              customFilters, reportTableName, corporationTableName
       └─ useReportEmbed.load(defaultPowerBIReportId)       through ReportEmbed.vue; uses savedFilters.savedReportFilters() (section 3)
```

- The first three calls run in parallel; the embed starts only after all three succeed and the catalog has categories.
- If any of the three fails, the reports list shows its failed state and `viewerLog.logViewerFailure()` logs "The reports list or the saved filters failed to load".

### Clicking Filters

```text
Browser click "Filters" (ReportFilters.vue)
  └─ ReportFilters.togglePanel()
       ├─ panel open → ReportFilters.closePanel()
       └─ ReportFilters.openPanel()
            ├─ no saved filters yet → savedFilters.loadSavedFilters()   failure → dialogs.handleNullSession()
            ├─ still none → dialogs.showOk()  "Your saved filters could not be loaded. Please try again."
            └─ the panel opens
                 ├─ sidePanels.announceSidePanelOpened()  announces "report-filters" → other side panels close
                 ├─ useDockedPanel.reserveBodySpace()     pads <body> on the right by the panel width
                 └─ FilterPanel.initializeDraft()       (watch on open)
                      ├─ copies the saved filters into the draft (dates as local calendar dates through
                      │    filterDates.calendarDate(); custom filters only when customFiltersEnabled)
                      ├─ optionCache.reset()                  company option cache emptied
                      ├─ FilterPanel.restartAccountList()    account option cache emptied
                      └─ FilterPanel.refreshFields()         custom filter labels (section 2c)
```

`customFiltersEnabled` comes from the feature flag `Feature_Enable_User_SortFilters`: `HomePresenter` puts it in `ViewData["EnableUserSortFilters"]`, `Views/Home/Index.cshtml` writes `data-custom-filters-enabled`, `homePage.readHomePage()` reads it, and `HomeShell.vue` passes it to `ReportFilters`.

Each `CheckboxList` loads its first page when it mounts. The panel is teleported to `<body>` and only exists while open, so every open starts with fresh lists.

### Option caching (`optionCache.ts`)

The company and account lists come from endpoints that return the whole list, so `FilterPanel` wraps each in `optionCache.createOptionCache()`:

- the first `loadPage` calls the fetch function once; later pages and searches filter and slice that one result in memory (case-insensitive "contains" on the option text)
- a failed fetch is forgotten so the next call retries
- `reset` drops the cached list; a fetch still in flight from before the reset cannot repopulate it
- companies: a cache over `api.getCompanyOptions()`, reset on every open
- accounts: a cache over `api.getAccountOptions()` with `draft.companyIds`, reset on every open, on Clear, and when the company selection changes; `accountListVersion` is bumped at the same time so the account `CheckboxList` remounts and reloads

Custom filter values are not cached; each page and search is a server call (section 2c).

## 2. The filter sections

The panel shows, in order: **Company Name**, **Account Name**, **Issued Date** (hidden when the active report's metadata has `fixedDate === true`), then one row per custom filter and an **Add Filter** button when custom filters are enabled.

### 2a. Checkbox lists with search (`CheckboxList.vue`)

Used for companies, accounts, and custom filter values. It takes a `loadOptions` function that receives `{ search, skip, take }` and returns `{ items, total }`.

- first page: `skip: 0, take: 50`; the virtual scroller asks for the next 50 when the last visible row is within 10 of the end
- search: waits 300 ms; a 1-character term does not search; the clear button reloads with no term
- "Select / Unselect Visible" toggles only the loaded rows
- "Select all" asks `loadOptions` for `{ search: '', skip: 0 }` with no `take` (the whole list from the cache, or `take: 999999` for custom values) and selects every value; with any selection it reads "Clear all" and empties the selection
- errors: "Unable to load values." or "Unable to load more values." with a **Retry** link; the error is also emitted as `load-error`, which the panel passes to `dialogs.handleNullSession()`
- the footer shows "Showing N of total items"

The section summary is "N selected", or "All" when nothing is selected.

**Company list**:

```text
Browser opens the panel (CheckboxList.vue mounts)
  └─ CheckboxList.load()
       └─ api.getCompanyOptions()  → GET /api/FilterReferenceData/GetCompanyNameFilter
```

On the server:

```text
FilterReferenceDataController.GetCompanyNameFilter      [RequireUserContext], [RequireAgencyDbContext]
  └─ FilterReferenceDataService.GetCompanyNameFilter(userContext, agencyDbOptions)
       ├─ CompanyScopeResolver.RestrictedCompanyId(userContext)   Company User / Company Admin → userContext.CorporationId
       ├─ IAsyncEntityConnectorResolver.GetCorporationProvider(agencyDbOptions)   active rows, only the user's company when restricted
       └─ returns UserCorporationId, Companies[{ CompanyID, CompanyName }], SelectedCompanyIds
```

The client uses only `Companies`.

**Account list** (body = the selected company ids):

```text
Browser selects companies (CheckboxList.vue)
  └─ FilterPanel.changeCompanies()
       ├─ clears the selected accounts
       ├─ FilterPanel.restartAccountList()      the account CheckboxList remounts
       │    └─ CheckboxList.load()
       │         └─ api.getAccountOptions()  → POST /api/FilterReferenceData/GetAccountNameFilter
       ├─ FilterPanel.refreshFields()           section 2c
       └─ FilterPanel.scheduleDateRangeUpdate() section 2b
```

On the server:

```text
FilterReferenceDataController.GetAccountNameFilter      [RequireUserContext], [RequireAgencyDbContext]
  └─ FilterReferenceDataService.GetAccountNameFilter(userContext, agencyDbOptions, companyIds)
       ├─ CompanyScopeResolver.PermittedCompanyIds(userContext, companyIds)   a company-role user keeps only their own company id
       │     none left → empty list
       ├─ IAsyncEntityConnectorResolver.GetCorporationToAccountProvider(agencyDbOptions)   account ids for the permitted companies
       └─ IAsyncEntityConnectorResolver.GetAccountProvider(agencyDbOptions)   dataAccount AccountID, AccountName
```

With no company selected the account list is empty.

### 2b. Dates

Two PrimeVue `DatePicker`s bound to `draft.startDate` and `draft.endDate` (`m/d/yy`). The summary is `m/d/yyyy - m/d/yyyy`, or "Not set".

When companies or accounts change, the panel fills the dates from the data:

```text
Browser selects companies or accounts (CheckboxList.vue)
  └─ FilterPanel.changeCompanies() or FilterPanel.changeAccounts()
       └─ FilterPanel.scheduleDateRangeUpdate()             waits 200 ms
            └─ FilterPanel.updateDateRange()
                 ├─ api.getDefaultDateRange()  → POST /api/CustomFilters/GetDefaultFilterDateRange
                 ├─ success → filterDates.calendarDate() fills draft.startDate and draft.endDate
                 └─ failure → "Default dates for this selection could not be loaded.", dialogs.handleNullSession()
```

On the server:

```text
CustomFiltersController.GetDefaultFilterDateRangeAsync      [RequireUserContext], [RequireAgencyDbContext]
  ├─ null body → 400 "Request cannot be null."
  ├─ both lists empty → returns CompanyFilterDateRangeResult.Empty (Jan 1 this year - today)
  └─ CustomFiltersService.GetCompanyFilterDateRange(userContext, agencyDbOptions, companyIds, accountIds)
       ├─ CustomFiltersService.ScopedAccountIdsAsync()   the requested accounts, or every account of the permitted companies
       │     company-role user → intersected with the accounts of their own company
       ├─ none left → FilterDateRange.Empty
       └─ dataBooking MinAsync and MaxAsync of IssuedDate for those accounts
```

A newer request cancels an older one. On failure the dates stay as they were.

When the Issued Date section is hidden for a fixed-date report, the draft dates are still saved and the `IssuedDate` filter is still sent to every report.

### 2c. Custom filters

Only when `customFiltersEnabled`. Each row is a `DraftCustomFilter` `{ key, field, operator, values }`.

- fields: always `Sort1`-`Sort10` plus `Domestic International`; the labels for the Sort fields come from the server
- a field already used by another row is not offered; **Add Filter** (`FilterPanel.addCustomFilter()`) picks the first unused field with operator `In`; the limit is 10 rows ("Maximum of 10 Filters allowed")
- changing the field (`FilterPanel.changeCustomFilterField()`) resets the operator to `In` and clears the values
- operators: `In` (shown "in") and `NotIn` (shown "not in")

**Labels** (body = the selected company id when exactly one company is selected, otherwise `[]`):

```text
Browser opens the panel, changes companies, or clicks Clear (FilterPanel.vue)
  └─ FilterPanel.refreshFields()                    only when customFiltersEnabled
       └─ FilterPanel.loadFields()
            ├─ api.getCustomFilterFields()  → POST /api/CustomFilters/GetSortFilterDefinitions
            ├─ success → customFilters.withDisplayNames() lays the labels over the fixed field list
            └─ failure → customFilters.defaultCustomFilterFields(), dialogs.handleNullSession()
```

On the server:

```text
CustomFiltersController.GetSortFilterDefinitions      [RequireUserContext], [RequireAgencyDbContext]
  └─ CustomFiltersService.GetSortFilterDefinitions(userContext, agencyDbOptions, companyIds)
       ├─ CompanyScopeResolver.PermittedCompanyIds(userContext, companyIds)
       │     none left → Sort1..Sort10 named as themselves
       └─ the first permitted company's active Corporation row: Sort{n}Label, or "Sort{n}" when blank
```

**Values**:

```text
Browser opens a custom filter row (CustomFilterRow.vue)
  └─ CheckboxList.load()
       └─ CustomFilterRow.loadOptions()
            └─ FilterPanel.loadCustomFilterValues()
                 └─ api.getCustomFilterValues()  → POST /api/CustomFilters/GetVirtualizedSortFilterValues
```

On the server:

```text
CustomFiltersController.GetVirtualizedSortFilterValuesAsync      [RequireUserContext], [RequireAgencyDbContext]
  └─ CustomFiltersService.GetVirtualizedSortFilterValues(userContext, agencyDbOptions, companyIds, accountIds,
       │     startDate, endDate, filterName, filterOperator, search, skip, take)
       ├─ Take ≤ 0 → 40; Skip < 0 → 0
       ├─ CustomFiltersService.PermittedAccountIdsAsync()   company-role user → the accounts of their own company
       ├─ CustomFiltersService.GetFilterValuesQuery()
       │     ├─ "Domestic International" → dataDomInt.DomIntName containing Search
       │     └─ Sort1..Sort10 → dataBooking.SortN containing Search (scoped bookings)
       ├─ distinct, ordered by value, counted, then skip and take
       └─ returns Items[{ Text, Value }], Total, UnfilteredTotal
```

The service reads only `FilterName`, `Search`, `Skip`, and `Take`; the company, account, date, and operator fields in the request are not used to narrow the values. `GetVirtualizedSortFilterValues` has no catch of its own, unlike the other filter services.

### Where the values come from

| Filter | Source |
|---|---|
| Company list | SQL, agency database, `Corporation` |
| Account list | SQL, agency database, `CorporationToAccount` + `dataAccount` |
| Default date range | SQL, agency database, `dataBooking.IssuedDate` |
| Sort labels | SQL, agency database, `Corporation.Sort{n}Label` |
| Sort values | SQL, agency database, `dataBooking.Sort1`-`Sort10` |
| Domestic International values | SQL, agency database, `dataDomInt.DomIntName` |
| Table names the filters target | SQL, `ganSetting` keys `PowerBIReportTableName` and `PowerBICorporationTableName` (agency 0) |

Nothing in the panel reads values from Power BI. Power BI only receives the filters (section 3).

### From a saved selection to Power BI filter objects (`powerBIFilters.ts`)

| Selection | Filter | Target |
|---|---|---|
| Dates | advanced, `logicalOperator: 'And'`, `GreaterThanOrEqual startDate` and `LessThan` the day after `endDate` (UTC midnight) | `{reportTableName}.IssuedDate` |
| Companies (only when any) | basic, `In`, the company ids | `{corporationTableName}.CompanyID` |
| Accounts (only when any) | basic, `In`, the account ids | `{corporationTableName}.AccountID` |
| Each custom filter | basic, `In` or `NotIn` (`Equals`/`NotEquals` are mapped to them), the non-empty values | `{reportTableName}.{field}` (`Sort1`.., `Domestic International`) |

Schemas are `http://powerbi.com/product/schema#advanced` (filterType 0) and `...#basic` (filterType 1). The table names are whatever `GET /api/DefaultFilters` returned.

Three functions in `savedFilters.ts` hand these out:

- `savedFilters.savedReportFilters()` → `powerBIFilters.reportUserFilters()`: the date, company, account, and custom filters (used by the embed)
- `savedFilters.savedExportFilters()` → `powerBIFilters.exportFilters()`: the date, company, and account filters only (used by `export-report/reportPage.ts`, whose `reportPage.exportFilterExpression()` turns them into an OData `$filter=` with `reportState.toODataFilter()`; see Home-Export-Plumbing.md §3)
- `savedFilters.savedFilterDateRange()` → `filterDates.reportFilterDateRange()`: `{ from, to }` as `yyyy-mm-dd` (used by `ai-query/reportPage.ts`; see Home-AIQuery-Plumbing.md §2)

## 3. Applying the filters to the report

Filters reach Power BI only through a fresh embed:

```text
Browser click "Save" (FilterPanel.vue)
  └─ FilterPanel.requestSave()                     section 4
       └─ ReportFilters.save()                     emits applied
            └─ HomeShell.reloadReport()
                 └─ ReportViewer.reloadReport()
                      └─ useReportEmbed.reload()   exposed by ReportEmbed.vue
                           ├─ no report requested yet → "Cannot get current report ID"
                           └─ useReportEmbed.load(requestedReportId)
                                ├─ reportState.clearActiveReport()
                                ├─ powerBIEmbed.resetEmbed(element)
                                ├─ api.getReportConfig()  → GET /api/PowerBIReportConfig/{powerBIReportId}   see Home-ReportViewer-Plumbing.md §3a
                                └─ useReportEmbed.embed()
                                     ├─ savedFilters.savedReportFilters()
                                     ├─ powerBIFilters.embedFilters(config, userFilters, basicFilterType)   config.filters, config.sortFilters, then the user filters
                                     ├─ powerBIFilters.filterProblem(filters)
                                     │     no filters at all → "An issue loading the report was found! Please try again later."
                                     │     a filter with no table or column → "Filter N is missing a target or column property."
                                     │     either → the report is not embedded
                                     ├─ powerBIEmbed.embedReport(element, config, filters)
                                     └─ reportState.setActiveReport(identity, userFilters)   activeReport.userFilters
```

- `powerBIEmbed.embedReport()` passes the filters in the embed configuration's `filters`, with settings `filterPaneEnabled: false`, `persistentFiltersEnabled: true`, `hideErrors: true`.
- Because they are in the embed configuration's `filters`, they are report-level filters. No page-level or visual-level filters are set, and the code never calls `setFilters`, `updateFilters`, or any bookmark API.
- The filter pane is hidden, so the user cannot change them inside the report.
- Opening another report from the catalog runs the same `useReportEmbed.load()`, so the saved filters follow the user to every report.
- `config.filters` and `config.sortFilters` come from the server; see Home-ReportViewer-Plumbing.md §3a.

## 4. Saved filters: load, save, clear

### Where they are stored

One row per user in `ganUserFilter` (`GraspANALYTICS.Data/Models/UserFilter.cs`): `UserFilterID`, `UserID`, `JsonFilters`, `DateCreated`, `DateModified`. `DefaultUserFilterRepository` finds the row by `UserID` only and upserts it.

`JsonFilters` holds the serialized `SaveUserFiltersRequest`:

```json
{ "StartDate": "...", "EndDate": "...", "SelectedCompanyIds": [], "SelectedAccountIds": [],
  "CustomFilters": [ { "FilterField": "Sort1", "FilterOperator": "In", "FilterValue": "[\"A\",\"B\"]" } ] }
```

`FilterValue` is itself a JSON array in a string (`customFilters.serializeCustomFilterValues()`). On load, `customFilters.parseCustomFilterValues()` also accepts a real array, a plain string, or a scalar; unusable entries are dropped and reported with `clientLogger.logToServer()` under the module name `ReportFilters`.

### Load (`GET /api/DefaultFilters`)

```text
Browser GET /Home
  └─ savedFilters.loadSavedFilters()                   section 1
       └─ api.getSavedFilters()  → GET /api/DefaultFilters
```

On the server:

```text
DefaultFiltersController.GetDefaultFiltersAsync        [RequireUserContext], [CaseInsensitiveJson]
  └─ DefaultUserFilterService.GetAsync(userContext)
       ├─ IUserRepository.GetAsync(userId, agencyId)       none → DefaultFiltersDto.Empty
       ├─ IDefaultUserFilterRepository.GetAsync(userContext)   the ganUserFilter row
       ├─ JsonSerializer.Deserialize(JsonFilters)          as DefaultFiltersDto
       ├─ no row, StartDate or EndDate default, or company or account list null
       │     └─ DefaultUserFilterService.CreateDefaultAsync()   DefaultFiltersDto.Empty (Jan 1 UTC this year - tomorrow UTC)
       │          └─ IDataSourceService.GetUserConsolidatedIDsAsync(agencyId)   DataSourceIds
       ├─ DefaultUserFilterService.AddFilterTableNamesAsync(dto)
       │     ├─ ISettingsRepository.GetAsync("PowerBIReportTableName")
       │     └─ ISettingsRepository.GetAsync("PowerBICorporationTableName")
       └─ any exception → logged, DefaultFiltersDto.Empty
```

- A user with no saved row gets the default range and no company, account, or custom filters, which is still applied to the report as an `IssuedDate` filter.
- `ISettingsRepository.GetAsync` returns an empty string for a missing key, so the `"ReportData"` and `"Corps"` fallbacks in `AddFilterTableNamesAsync` never apply; a missing setting gives an empty table name. `DefaultFiltersDto.Empty` also has empty table names. Either way the embed refuses the filters (section 3).

### Save (`POST /api/DefaultFilters`)

```text
Browser click "Save" (FilterPanel.vue)
  └─ FilterPanel.requestSave()
       ├─ start or end date missing → dialogs.showOk()  "Start and end dates are required"
       ├─ start after end           → dialogs.showOk()  "Start date must come before the end date"
       ├─ some custom rows have no values → dialogs.showConfirm()  "Missing Filter Values"; Yes drops those rows
       └─ emits save with the selection
            └─ ReportFilters.save()                       ignored while a save is running
                 ├─ ReportFilters.persist()
                 │    ├─ filterDates.utcMidnightIso()     the chosen calendar day at 00:00Z
                 │    ├─ ReportFilters.resolveAccountIds()
                 │    │     companies chosen but no accounts → api.getAccountOptions(companyIds), every account id is saved
                 │    ├─ copies the table names from the current saved filters
                 │    └─ api.saveFilters()  → POST /api/DefaultFilters
                 ├─ success → savedFilters.setSavedFilters(), ReportFilters.closePanel(), emits applied   section 3
                 └─ failure → dialogs.handleNullSession(), dialogs.showOk()  "Your filters could not be saved. Please try again."
```

On the server:

```text
DefaultFiltersController.SaveDefaultFiltersAsync        [RequireUserContext]
  ├─ DefaultUserFilterService.CreateOrUpdateAsync(userContext, request)
  │    ├─ IUserRepository.GetAsync(userId, agencyId)     none → logged, nothing saved (still 200)
  │    ├─ JsonSerializer.Serialize(request)
  │    └─ IDefaultUserFilterRepository.CreateOrUpdateAsync(userContext, jsonFilters)   DefaultUserFilterRepository
  └─ returns 200 with { "status": "success" }
```

The server stores the ids as sent; it does not check them against the user's company scope.

### Clear and Cancel

- **Clear** (`FilterPanel.clear()`) resets the draft only: dates to Jan 1 - today (local, `filterDates.defaultFilterDates()`), no companies, accounts, or custom filters. Nothing is saved until **Save**.
- **Cancel** or the close button (`FilterPanel.close()`) closes the panel. The next open copies the saved filters again, so unsaved changes are lost.

## 5. Panel preferences

Both live in `localStorage` through `shared/browserStorage.ts` (read and write failures are ignored):

| Key | Value | Set by |
|---|---|---|
| `report-filters-collapsed-sections` | JSON array of collapsed ids: `companies`, `accounts`, `dates` | `FilterPanel.setExpanded()` on every toggle |
| `report-filters-panel-width` | width in pixels | `useDockedPanel` when a drag on the left edge ends; a double-click on the edge (`resetWidth()`) resets to 420 and removes the key |

The width is kept between 320 px and 90% of the window. Custom filter rows are always expanded when they appear and their state is not stored.

## 6. Validation and error states

| Where | Condition | What the user sees |
|---|---|---|
| Open | saved filters fail to load | dialog "Your saved filters could not be loaded. Please try again." |
| Checkbox list | first page fails | "Unable to load values." + Retry |
| Checkbox list | a later page fails | "Unable to load more values." + Retry |
| Dates | date-range call fails | "Default dates for this selection could not be loaded." under Issued Date |
| Custom labels | label call fails | plain `Sort1`..`Sort10` names, no message |
| Save | a date is missing / start after end | dialog with the message, nothing sent |
| Save | custom rows without values | confirm dialog; Yes saves without those rows |
| Save | save call fails | dialog "Your filters could not be saved. Please try again." |
| Embed | a filter with no table or column (for example empty table names) | the report is not embedded; "Filter N is missing a target or column property." |
| Any call | 401 | `dialogs.handleNullSession()` → `HandleNullSession` (`GraspANALYTICS/wwwroot/js/site.js`) → `/Error?statusCode=401` |

Most server failures do not reach the browser as errors: the services catch exceptions, log them, and return an empty result with 200 (`DefaultFiltersDto.Empty`, `CompanyFilterResult.Empty`, an empty account list, `FilterDateRange.Empty`, default sort definitions).

## 7. Endpoints

All are `[ApiController]` with `[RequireUserContext]` (no user context → **401**). The `FilterReferenceData` and `CustomFilters` actions also have `[RequireAgencyDbContext]`: no app session → **401**; no agency database options → **400** "Agency database options are required." Every action except `POST /api/DefaultFilters` has `[CaseInsensitiveJson]`, which replaces the output formatter with default `System.Text.Json` options, so property names keep their C# casing (PascalCase). The POSTs send a `RequestVerificationToken` header, but these actions have no antiforgery attribute.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/DefaultFilters` | `DefaultFiltersController.GetDefaultFiltersAsync → DefaultUserFilterService.GetAsync` | - | `StartDate`, `EndDate`, `SelectedCompanyIds`, `SelectedAccountIds`, `DataSourceIds`, `CustomFilters[{ FilterField, FilterOperator, FilterValue }]`, `PowerBIReportTableName`, `PowerBICorporationTableName` | 200; 401 |
| `POST /api/DefaultFilters` | `DefaultFiltersController.SaveDefaultFiltersAsync → DefaultUserFilterService.CreateOrUpdateAsync` | `StartDate`, `EndDate`, `SelectedCompanyIds`, `SelectedAccountIds`, `CustomFilters[{ FilterField, FilterOperator, FilterValue }]` | `{ "status": "success" }` | 200; 400 (model binding); 401 |
| `GET /api/FilterReferenceData/GetCompanyNameFilter` | `FilterReferenceDataController.GetCompanyNameFilter → FilterReferenceDataService.GetCompanyNameFilter` | - | `UserCorporationId`, `Companies[{ CompanyID, CompanyName }]`, `SelectedCompanyIds` | 200; 400; 401 |
| `POST /api/FilterReferenceData/GetAccountNameFilter` | `FilterReferenceDataController.GetAccountNameFilter → FilterReferenceDataService.GetAccountNameFilter` | JSON array of company ids | `[{ AccountID, AccountName }]` | 200; 400; 401 |
| `POST /api/CustomFilters/GetSortFilterDefinitions` | `CustomFiltersController.GetSortFilterDefinitions → CustomFiltersService.GetSortFilterDefinitions` | JSON array of company ids | `[{ Name, DisplayName }]` | 200; 400; 401 |
| `POST /api/CustomFilters/GetVirtualizedSortFilterValues` | `CustomFiltersController.GetVirtualizedSortFilterValuesAsync → CustomFiltersService.GetVirtualizedSortFilterValues` | `CompanyIds`, `AccountIds`, `StartDate`, `EndDate`, `FilterName`, `FilterOperator`, `Search`, `Skip`, `Take` | `Items[{ Text, Value }]`, `Total`, `UnfilteredTotal` | 200; 400; 401 |
| `POST /api/CustomFilters/GetDefaultFilterDateRange` | `CustomFiltersController.GetDefaultFilterDateRangeAsync → CustomFiltersService.GetCompanyFilterDateRange` | `CompanyIds`, `AccountIds` | `{ DateRange: { StartDate, EndDate } }` | 200; 400 (null body: "Request cannot be null."); 401 |

`CustomFiltersController` also has `GetSortFilterValuesAsync` (`POST /api/CustomFilters/GetSortFilterValues`), `GetSortFilterValueItemsAsync` (`POST /api/CustomFilters/GetSortFilterValueItems`), and `GetDomIntValuesAsync` (`GET /api/CustomFilters/GetDomIntValues`); nothing in `GraspANALYTICS/ClientApp/src` calls them.
