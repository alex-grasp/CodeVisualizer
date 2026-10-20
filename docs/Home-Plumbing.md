---
summary: Overview of the Home reports page (/Home) - the Razor host and Vue shell, the server values it receives, how the header, report viewer, filters, export, and AI query share state, the account menu and logout, session and 401 handling, and every API the page calls; read before debugging or changing anything on Home.
area: architecture
---

# Home plumbing

This doc follows a signed-in user from landing on `/Home` to logging out, and names what connects to what and what calls what. It is the overview; each section of the page has its own doc:

- Home-ReportViewer-Plumbing.md - report list, Power BI embed, default report, view mode
- Home-Filters-Plumbing.md - the Filters panel and saved filters
- Home-Export-Plumbing.md - Export Report
- Home-AIQuery-Plumbing.md - the AI Query panel

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Page controller | `GraspANALYTICS/Controllers/HomeController.cs` | `GET /Home` (`Index`) renders the host view; `GET /Home/Logout` (`Logout`) ends the session. |
| View data | `GraspANALYTICS/Web/Presenters/HomePresenter.cs` | `InitializeViewData()`: copies user name, branding, admin flag, Google Analytics id, custom-filter flag, and agency title from the `AppSession` into `ViewData`. |
| Host view | `GraspANALYTICS/Views/Home/Index.cshtml` | Full HTML page (no layout). Antiforgery hidden field, `#home-app` with `data-*` settings, the Vite entry. |
| App base shim | `GraspANALYTICS/Views/Shared/_AppBaseShim.cshtml` | Sets `window.appBase` and `window.appUrl()` for a virtual directory. |
| Vite tag helper | `GraspANALYTICS/Infrastructure/Vite/ViteTagHelper.cs` | `<vite-asset entry="src/home/shell/main.ts" />` → CSS, module preloads, and the script from `wwwroot/vue/manifest.json`. |
| Bootstrap | `GraspANALYTICS/ClientApp/src/home/shell/main.ts` | Reads the settings, sets up PrimeVue (blue palette), mounts `HomeShell.vue` on `#home-app`. |
| Settings reader | `GraspANALYTICS/ClientApp/src/home/shell/homePage.ts` + `GraspANALYTICS/ClientApp/src/home/shell/types.ts` | `readHomePage()` → `HomePageSettings`. |
| Account links reader | `GraspANALYTICS/ClientApp/src/home/report-header/accountMenuLinks.ts` | `readAccountMenu()` → `AccountMenuLinks` or `null`. |
| Shell | `GraspANALYTICS/ClientApp/src/home/shell/HomeShell.vue` | `toggleReportMenu()` and `reloadReport()`: the header bar plus the report viewer and AI panel. |
| Header brand | `GraspANALYTICS/ClientApp/src/home/report-header/HeaderBrand.vue` | Menu button (emits `toggleMenu`), logo, title. |
| Header links | `GraspANALYTICS/ClientApp/src/home/report-header/HeaderLinks.vue` | Feedback, Support, and the account menu. |
| Account menu | `GraspANALYTICS/ClientApp/src/home/report-header/AccountMenu.vue` | `toggleMenu()` and `closeMenu()`: the user-name button and its popover. |
| Shared report state | `GraspANALYTICS/ClientApp/src/home/report-viewer/reportState.ts` | `activeReport`, `setActiveReport()`, `setActiveReportMetadata()`, `markActiveReportRendered()`, `clearActiveReport()`. |
| Shared saved filters | `GraspANALYTICS/ClientApp/src/home/report-filters/savedFilters.ts` | `savedFilters`, `loadSavedFilters()`, `setSavedFilters()`, `savedReportFilters()`, `savedExportFilters()`, `savedFilterDateRange()`. |
| Side-panel exclusivity | `GraspANALYTICS/ClientApp/src/shared/sidePanels.ts` | `announceSidePanelOpened()` and `useExclusiveSidePanel()`. |
| AI panel state | `GraspANALYTICS/ClientApp/src/home/ai-query/panelState.ts` | `panelOpen`, `togglePanel()`, `closePanel()`. |
| HTTP helpers | `GraspANALYTICS/ClientApp/src/shared/http.ts` + `GraspANALYTICS/ClientApp/src/shared/apiError.ts` + `GraspANALYTICS/ClientApp/src/shared/antiforgery.ts` + `GraspANALYTICS/ClientApp/src/shared/appBase.ts` | `requestJson()`, `sendJson()`, `readJson()`, `errorMessage()`; `ApiError`; `antiforgeryToken()`; `appUrl()`. |
| Session-loss handler | `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `handleNullSession()`: on a 401 from any call, sends the browser to `/Error?statusCode=401`. |
| Client logging | `GraspANALYTICS/ClientApp/src/shared/clientLogger.ts` → `GraspANALYTICS/Controllers/Api/ClientLoggerController.cs` | `logToServer()`; `POST /api/ClientLogger` (`LogClientErrorAsync`). |
| Request checks | `GraspANALYTICS/Classes/SessionValidationMiddleware.cs` + `GraspANALYTICS/Classes/CustomAuthorizeAttribute.cs` + `GraspANALYTICS/Helpers/RequireUserContextAttribute.cs` | `SessionValidationMiddleware.Invoke()`: session claim check on every request; `CustomAuthorizeAttribute.OnAuthorization()`: app-session check on MVC pages; `RequireUserContextAttribute.OnActionExecutionAsync()`: user-context check on APIs. |
| Error pages | `GraspANALYTICS/Controllers/ErrorController.cs` + `GraspANALYTICS/Views/Error/401.cshtml` + `GraspANALYTICS/Views/Error/460.cshtml` | `GET /Error?statusCode=` (`Index`). |

## 1. Loading the page

```text
Browser GET /Home
  ├─ request pipeline                                          (section 4a)
  └─ HomeController.Index
       ├─ role check on the class: no auth cookie → 302 /Login (cookie LoginPath)
       ├─ CustomAuthorizeAttribute.OnAuthorization             on the class and on Index
       │     no AppSession in the cache, or a blank user name → ExpiredLoginResult (401) → re-executed as /Error?statusCode=401
       │     LastLoginWins is "true" and another session id   → DuplicateLoginResult (460) → re-executed as /Error?statusCode=460
       ├─ ISessionService.GetAppSession()   SessionService      the AppSession from the memory cache, keyed by login
       │     null → 302 /Login
       ├─ HomePresenter.InitializeViewData(appSession, ViewData)
       ├─ IUserSettingsService.GetReportToggleAsync() → ViewBag.AllowToggleMenu
       │     └─ ISettingsRepository.GetGlobalBooleanSettingAsync("ReportThumbnailToggleMenu")
       └─ renders Views/Home/Index.cshtml
```

- The role check is `[Authorize(Roles = "Agency User,Agency Admin,Super Admin,Company User, Company Admin")]` on `HomeController`.

`Index.cshtml` renders a full page with `Layout = null`:

- `<title>` is `GeneralSettings.ApplicationTitle` (appsettings).
- `_AppBaseShim` sets `window.appBase` to the app root. `appBase.appUrl` reads it, so every client call works under a virtual directory. Home does not call `appBase.setAppBaseFromBundleUrl`.
- The Google tag script loads with the `GoogleAnalyticsID` from the session.
- `@Html.AntiForgeryToken()` writes the hidden `__RequestVerificationToken` field that every write on the page sends (section 4d).
- `<vite-asset entry="src/home/shell/main.ts" />` emits the built bundle from `wwwroot/vue/` (or the dev server's URLs when `Vite:DevServerUrl` is set in Development). It throws if the manifest entry is missing.

### Server values that reach the client

Every value travels as a `data-*` attribute on `#home-app`; there is no separate shell-context API.

| Attribute | Value | Comes from |
|---|---|---|
| `data-page-heading` | `<ApplicationTitle> Reports` | `GeneralSettings.ApplicationTitle` |
| `data-title` | agency title, or `GeneralSettings.ApplicationTitle` when blank | `AppSession.Admin.ApplicationTitle` via `HomePresenter` |
| `data-logo` | base64 logo bytes | `AppSession.Style.HeaderLogo` |
| `data-header-background-color` | color | `AppSession.Style.HeaderBackgroundColor` |
| `data-header-text-color` | color | `AppSession.Style.HeaderTextColor` |
| `data-user-name` | login | `AppSession.CurrentUser.Username` |
| `data-my-account-url` | `/Account` | `Url.Action("Index", "Account")` |
| `data-administration-url` | `/Admin`, or absent | only when `Enums.IsAdminOrSuperAdminRole` (Agency Admin, Company Admin, Super Admin) |
| `data-logout-url` | `/Home/Logout` | `Url.Action("Logout", "Home")` |
| `data-custom-filters-enabled` | `true`/`false` | feature flag `Feature_Enable_User_SortFilters` |
| `data-allow-toggle-menu` | `true`/`false` | `ViewBag.AllowToggleMenu` (setting `ReportThumbnailToggleMenu`) |
| `data-ai-query-enabled` | `true`/`false` | `IFoundryFeatureResolver.IsEnabled()`: flag `Feature_AI_Query` and `AppSession.FoundryEnabled` |
| `data-dax-queries-enabled` | `true`/`false` | AI enabled and `IFoundryFeatureResolver.IsDaxQueriesEnabledAsync()`: the user's `AiUsageLimit.DaxQueriesEnabled` |

On the client:

```text
Browser runs the Home bundle (main.ts)
  ├─ document.getElementById('home-app')                      nothing mounts if it is missing
  ├─ homePage.readHomePage(mount.dataset)                     → HomePageSettings
  │    └─ accountMenuLinks.readAccountMenu(dataset)          null when the trimmed user name, My Account URL, or logout URL is blank
  ├─ primevue.configurePrimeVue(app, 'blue')
  └─ app.mount(mount)                                         HomeShell.vue with { settings }
```

- In `homePage.readHomePage`, strings default to `''` and flags are true only for the text `"true"`.

## 2. The shell layout and shared state

```diagram
HomeShell.vue (props: settings)
  ├─ <h1 class="page-heading">                      visually hidden, for screen readers
  ├─ <header class="header">                        background and text color from branding
  │   ├─ HeaderBrand          headerTextColor, logo, title, menuAvailable   emits toggleMenu
  │   ├─ ReportFilters        customFiltersEnabled                          emits applied
  │   ├─ ExportReport         headerTextColor
  │   ├─ AiQueryToggle        headerTextColor                               only when aiQueryEnabled
  │   └─ HeaderLinks          headerTextColor, account
  │        └─ AccountMenu     account, headerTextColor                      only when account is not null
  ├─ ReportViewer (ref="viewer")  allowToggleMenu
  │     exposes menuAvailable, togglePanel, reloadReport
  └─ AiQueryPanel             daxQueriesEnabled                             only when aiQueryEnabled
```

```text
Browser click the reports-menu button (HeaderBrand.vue)
  └─ HeaderBrand emits toggleMenu
       └─ HomeShell.toggleReportMenu()
            └─ ReportViewer.togglePanel()          opens or closes the report list; ignored until the catalog is ready

Browser saves the Filters panel (ReportFilters.vue)
  └─ ReportFilters.save(selection)
       ├─ savedFilters.setSavedFilters(next)
       ├─ closes the panel
       └─ ReportFilters emits applied
            └─ HomeShell.reloadReport()
            └─ ReportViewer.reloadReport()
                 └─ ReportEmbed.reload()
```

How the pieces talk to each other:

| From | To | How |
|---|---|---|
| `ReportViewer` | `HeaderBrand` | `HomeShell` passes `ReportViewer.menuAvailable` (true while the catalog is loading or ready) as `menuAvailable`. |
| `ReportViewer` (`useReportEmbed.ts`) | everyone | writes `reportState.ts`: `reportState.clearActiveReport()` on each load and on unmount, `reportState.setActiveReport()` after embedding, `reportState.setActiveReportMetadata()` when metadata arrives, `reportState.markActiveReportRendered()` on the Power BI `rendered` event. |
| `reportState.ts` | `ReportFilters` | `activeReport.metadata.fixedDate` → fixed-date report handling. |
| `reportState.ts` | `ExportReport` | `export-report/reportPage.ts`: `reportPage.useExportAvailability()` is true only when `activeReport.rendered`; `reportPage.currentReport()` supplies `activeReport.powerBIReportId`. |
| `reportState.ts` | `AiQueryPanel` | `ai-query/reportPage.ts`: `reportPage.onReportChanged()` watches `activeReport.metadata` and passes the `datasetId`. |
| `savedFilters.ts` | viewer, export, AI query | `savedFilters.loadSavedFilters()` (one shared request); the viewer embeds with `savedFilters.savedReportFilters()`, export builds `$filter=` from `savedFilters.savedExportFilters()`, AI query seeds dates from `savedFilters.savedFilterDateRange()`. `ReportFilters` writes it with `savedFilters.setSavedFilters()` after a save. |
| `AiQueryToggle` | `AiQueryPanel` | module state in `ai-query/panelState.ts` (`panelState.panelOpen`, `panelState.togglePanel()`, `panelState.closePanel()`). |
| `ReportFilters` ↔ `AiQueryPanel` | each other | `sidePanels.useExclusiveSidePanel()`: opening one calls `sidePanels.announceSidePanelOpened()`, which dispatches a `side-panel:opened` document event (`report-filters` or `ai-query`), and the other closes. |

`reportState.ts` and `savedFilters.ts` are module-level `shallowRef`s, so every component that imports them shares one copy for the page. `reportState` values are frozen; changes replace the whole object, and an update for a report id that is no longer active is ignored.

## 3. The header

### Brand and links

- `HeaderBrand.vue`: the reports-menu button (`pi-bars`, shown with `v-show="menuAvailable"`), the logo as `data:image/jpeg;base64,<logo>` (left out when there is no logo), and the title. The header text color applies to the button.
- `HeaderLinks.vue`: two fixed external links that open in a new tab, and the account menu.

| Link | Goes to |
|---|---|
| Feedback | `https://www.surveymonkey.com/r/DP79CJS` |
| Support | `https://www.grasptech.com/support` |

### Account menu

```text
Browser click the user name (AccountMenu.vue)
  └─ AccountMenu.toggleMenu(event)
       └─ Popover.toggle(event)                     #account-menu

Browser click "Close Menu" (AccountMenu.vue)
  └─ AccountMenu.closeMenu()
       └─ Popover.hide()
```

| Item | URL | Server | Shown when |
|---|---|---|---|
| My Account | `/Account` | `AccountController.Index` (`[CustomAuthorize]`) | always |
| Administration | `/Admin` | `AdminController.Index` (`[Authorize(Roles = "Agency Admin,Super Admin,Company Admin")]`, `[CustomAuthorize]`) | `data-administration-url` is set |
| Logout | `/Home/Logout` | `HomeController.Logout` | always |

The Home menu has no change-password item.

### Logout end to end

```text
Browser click "Logout" (AccountMenu.vue)         a plain link to GET /Home/Logout, no antiforgery token
  └─ HomeController.Logout                        the class-level role check and [CustomAuthorize] run first
       ├─ ISessionService.Logout()   SessionService
       │    ├─ reads CurrentSession.Admin.LogoutRedirect
       │    │     setting "LogoutRedirect" (agency, else global, default "Login"), loaded at sign-in
       │    ├─ HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)   not awaited
       │    ├─ IMemoryCache.Remove(username)       drops the AppSession
       │    └─ exceptions are logged and swallowed; a blank value → "Login"
       └─ redirects to the Index action of the controller the setting names → 302 /<controller>
```

- Home logout goes through `SessionService.Logout`, not `LoginService.LogoutUserAsync`. Unlike `LoginService.LogoutUserAsync`, it does not clear the ASP.NET session, so `SessionIdentity.SessionKey` stays in the session until it expires or the next sign-in replaces it.
- Because `CustomAuthorizeAttribute.OnAuthorization` runs first, a user whose `AppSession` is already gone gets the 401 "Your session has expired" page instead of the logout redirect.

## 4. Session handling while on Home

### 4a. On every request

```text
Browser any request to /Home or a Home API
  ├─ UseAuthentication                 auth cookie → HttpContext.User (Name, Role, session-id claim)
  ├─ UseAuthorization                  [Authorize] on the endpoint; no cookie → 302 /Login
  ├─ UseSession                        ASP.NET session (holds SessionIdentity.SessionKey)
  ├─ SessionValidationMiddleware.Invoke
  │    ├─ User.Identity is null                       → 302 /Login
  │    ├─ signed in, session has no session key       → signs out; API: 401 JSON, page: 302 /Login
  │    ├─ claim does not match the session key        → signs out; API: 401 JSON, page: 302 /Forbidden/Index?errorGuid=...
  │    └─ otherwise continues
  └─ UseGraspContentSecurityPolicy     CSP header; pre-sets 401 for an anonymous request outside / and login/ paths
```

- `UseCookiePolicy` runs between `UseAuthorization` and `UseSession`, and `HmacAuthenticationMiddleware` between `SessionValidationMiddleware` and `UseGraspContentSecurityPolicy` (`GraspANALYTICS/Program.cs`).
- "API" in the middleware means `X-Requested-With: XMLHttpRequest`, an `Accept` containing `application/json`, or a path under `/api`. The 401 body is:

  ```json
  {"error": "Session expired. Please log in again."}
  ```

Cookie and session lifetimes (`GraspANALYTICS/CompositionRoot/GraspAuthenticationExtensions.cs`, `GraspANALYTICS/Program.cs`): the `Cookies` scheme expires after 240 minutes with sliding expiration; the ASP.NET session idles out after 240 minutes. The `ConfigureApplicationCookie` block in the same file configures the ASP.NET Identity application scheme, which the app does not use, so its 30-minute expiry and `OnValidatePrincipal` check do not apply.

### 4b. Per endpoint

| Check | Where it applies on Home | Failure |
|---|---|---|
| `[CustomAuthorize]` | `HomeController`, `AccountController`, `AdminController`, `AiAccessController` | no `AppSession` or a blank user name → 401 (`ExpiredLoginResult`); `LastLoginWins = "true"` and `AppSession.CurrentUser.HttpContextSessionID` differs from the session id → 460 (`DuplicateLoginResult`) |
| `[RequireUserContext]` | every Home API controller | `IUserContextResolver.GetAsync` returns null (no `AppSession`, or the user is not found) → empty 401 |
| `[RequireAgencyDbContext]` | `FilterReferenceDataController`, the `CustomFiltersController` actions | no user context → empty 401; no agency database options → 400 "Agency database options are required." |
| `[Authorize]` | `AiAccessController`, `FoundryAiController`, `FoundryAiExportController`, `PowerBIReportConfigController` | no cookie → cookie challenge |

For page requests (`/Home`, `/Home/Logout`), `UseGraspStatusCodePages` re-executes an empty 401 or 460 as `/Error?statusCode=<code>`. `ErrorController.Index` renders `Views/Error/401.cshtml` ("Your session has expired." with "Return to Login") or `460.cshtml`, both linking to `/Login`. `/api` paths are excluded, so APIs return the bare status.

### 4c. On the client

There is no keepalive or periodic session check on Home. Session loss shows up only when a call fails:

```text
A Home call fails with an ApiError (any section's catch block)
  └─ dialogs.handleNullSession(error)
       ├─ window.HandleNullSession is defined → window.HandleNullSession(status)   (Home does not define it)
       └─ error.status is 401 → sets location.href to appBase.appUrl('/Error?statusCode=401')
```

- `http.requestJson()` throws `ApiError(status, body)` for any non-2xx response; `http.sendJson()` adds the antiforgery header and calls `http.requestJson()`. Export (`api.exportReport()`) and AI query (`api.streamQuery()`, `api.getQueryResultCsv()`) use `fetch` directly and throw `ApiError` themselves.
- Only 401 redirects. A 460 or any other status stays on the page, and the section shows its own message.
- `http.errorMessage(error, fallback)` reads `errorMessage` or `message` from an `ApiError` body; for another `Error` it uses its message.

### 4d. Antiforgery for writes

- The token comes from the hidden `__RequestVerificationToken` field that `Index.cshtml` renders. `antiforgery.antiforgeryToken()` reads it; Home does not call `GET /api/Antiforgery/token`.
- `http.sendJson()`, `export-report/api.exportReport()`, and `ai-query/api.streamQuery()` send it in the `RequestVerificationToken` header (`AddAntiforgery(options => options.HeaderName = "RequestVerificationToken")` in `GraspANALYTICS/Program.cs`).
- The app has no global antiforgery filter. Of the Home writes, only `SetDefaultReport` and `ExportReport` carry `[ValidateAntiForgeryToken]`; the others accept the header without checking it (tables in section 5).
- An `AntiforgeryValidationException` that reaches `UseGraspContentSecurityPolicy` becomes a redirect to `/Error?statusCode=400`.

### 4e. Client logging

```text
A viewer or filter error (viewerLog.ts, report-filters/api.ts)
  └─ clientLogger.logToServer(moduleName, message)
       ├─ crypto.randomUUID()                         the error id
       ├─ http.sendJson('POST', '/api/ClientLogger', { moduleName, message, errorId })
       │     fire and forget; a failure goes to console.error
       └─ returns the error id                        the viewer shows it in its error messages
```

On the server:

```text
ClientLoggerController.LogClientErrorAsync           [RequireUserContext]
  ├─ IsDebug → IAnalyticsLogger.LogDebug("Client[{ModuleName}] {Message}")
  ├─ otherwise → IAnalyticsLogger.LogError("Client[{ModuleName}] Exception {Message}. ErrorID={ErrorId}")
  └─ always returns 200 with status "success"
```

Callers on Home: `viewerLog.logViewerEvent()` and `viewerLog.logViewerFailure()` (module `ReportViewer`) and `report-filters/api.ts` (module `ReportFilters`, for unusable saved custom-filter values). Home never sends `isDebug`, so every entry is logged as an error.

## 5. Every API the page calls

All paths go through `appBase.appUrl`. Every one of these controllers has `[RequireUserContext]`, so every call can also answer 401 (or the middleware's 401 JSON, section 4a). "Validated" or "not validated" says whether the server checks the antiforgery header. Request and response detail lives in the section docs.

### 5a. Report viewer

Called from `report-viewer/api.ts`: `api.getCategoriesAndReports()`, `api.setDefaultReport()`, `api.getReportMetadata()`, `api.getReportConfig()`, `api.getStoredViewMode()` (only when `allowToggleMenu`), `api.saveViewMode()`. See Home-ReportViewer-Plumbing.md.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/UserReport/GetCategoriesAndReports` | `UserReportController.GetCategoriesAndReportsAsync → ICategoriesAndReportsService.GetForUserAsync` | none; header `RequestVerificationToken` (not validated) | `CategoriesAndReportsDto` | 200; 401 |
| `POST /api/UserReport/SetDefaultReport` | `UserReportController.SetDefaultReportAsync → IUserReportService.SetDefaultReportAsync` | `reportId` (a bare number); header `RequestVerificationToken` (validated) | `status` "success" | 200; 400 (antiforgery); 401; 404 (report not available) |
| `GET /api/UserReport/GetReportMetaData?powerBIReportID=` | `UserReportController.GetReportMetaDataAsync → IUserReportService.GetReportMetaDataAsync` | `powerBIReportID` | `ReportMetadata` | 200; 401; 404 (report not available) |
| `GET /api/PowerBIReportConfig/{reportId}` | `PowerBIReportConfigController.Get → IGetPowerBIReportConfigUseCase.ExecuteAsync` | `reportId` (GUID) | `ApiResponse` with `PowerBIReportConfigResponse` | 200; 401; 403 (no data source scope); 404 (not found, or no workspace); 500 (missing table, other domain error); 502 (Power BI unreachable); 503 (dataset query failed) |
| `GET /api/UserSettings` | `UserSettingsController.GetUserSettingsAsync → IUserSettingsService.GetAllAsync` | none | list of `UserSetting` (`Key`, `Value`) | 200; 401 |
| `POST /api/UserSettings` | `UserSettingsController.SaveUserSettingAsync → IUserSettingsService.CreateAsync` | `key`, `value`; header `RequestVerificationToken` (not validated) | `status` "success" | 200; 401 |

### 5b. Filters

Called from `report-filters/api.ts`: `api.getSavedFilters()` (also through `savedFilters.loadSavedFilters()`, which the viewer calls at start), `api.saveFilters()`, `api.getCompanyOptions()`, `api.getAccountOptions()`, `api.getCustomFilterFields()`, `api.getCustomFilterValues()`, `api.getDefaultDateRange()`. See Home-Filters-Plumbing.md.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/DefaultFilters` | `DefaultFiltersController.GetDefaultFiltersAsync → IDefaultUserFilterService.GetAsync` | none | `DefaultFiltersDto` | 200; 401 |
| `POST /api/DefaultFilters` | `DefaultFiltersController.SaveDefaultFiltersAsync → IDefaultUserFilterService.CreateOrUpdateAsync` | `StartDate`, `EndDate`, `SelectedCompanyIds`, `SelectedAccountIds`, `CustomFilters`; header `RequestVerificationToken` (not validated) | `status` "success" | 200; 401 |
| `GET /api/FilterReferenceData/GetCompanyNameFilter` | `FilterReferenceDataController.GetCompanyNameFilter → IFilterReferenceDataService.GetCompanyNameFilter` | none | `CompanyFilterResult` | 200; 400 (no agency database); 401 |
| `POST /api/FilterReferenceData/GetAccountNameFilter` | `FilterReferenceDataController.GetAccountNameFilter → IFilterReferenceDataService.GetAccountNameFilter` | company ids (array); header `RequestVerificationToken` (not validated) | list of `AccountClass` | 200; 400 (no agency database); 401 |
| `POST /api/CustomFilters/GetSortFilterDefinitions` | `CustomFiltersController.GetSortFilterDefinitions → ICustomFiltersService.GetSortFilterDefinitions` | company ids (array); header `RequestVerificationToken` (not validated) | list of `SortFilterDefinitionDto` | 200; 400 (no agency database); 401 |
| `POST /api/CustomFilters/GetVirtualizedSortFilterValues` | `CustomFiltersController.GetVirtualizedSortFilterValuesAsync → ICustomFiltersService.GetVirtualizedSortFilterValues` | `CompanyIds`, `AccountIds`, `StartDate`, `EndDate`, `FilterName`, `FilterOperator`, `Search`, `Skip`, `Take`; header `RequestVerificationToken` (not validated) | `VirtualizedFilterValuesResult` | 200; 400 (no agency database); 401 |
| `POST /api/CustomFilters/GetDefaultFilterDateRange` | `CustomFiltersController.GetDefaultFilterDateRangeAsync → ICustomFiltersService.GetCompanyFilterDateRange` | `CompanyIds`, `AccountIds`; header `RequestVerificationToken` (not validated) | `CompanyFilterDateRangeResult` (empty when both lists are empty) | 200; 400 (null body, or no agency database); 401 |

### 5c. Export

Called from `export-report/api.ts`: `api.exportReport()`. See Home-Export-Plumbing.md.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/UserReport/ExportReport` | `UserReportController.ExportReportAsync → IExportReportUseCase.ExportReportAsync` | `reportId`, `exportType` (`pdf` or `ppt`), `filters`; header `RequestVerificationToken` (validated) | the file | 200; 400 (antiforgery, no report id, bad export type, export failure); 401; 404 (report not available) |

### 5d. AI query

Called from `ai-query/api.ts`: `api.getUsageStatus()`, `api.streamQuery()`, `api.getQueryResultCsv()`. See Home-AIQuery-Plumbing.md.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/AiAccess` | `AiAccessController.GetAsync → IAiUsageService.GetAsync` | none | `AiUsageStatusResponse` | 200; 401; 460 (`[CustomAuthorize]` duplicate sign-in) |
| `POST /api/FoundryAi/query` | `FoundryAiController.Query → IFoundryQueryUseCase.ExecuteAsync` | the question, dataset, dates, and history; header `RequestVerificationToken` (not validated) | `text/event-stream` | 200; 400 (blank query or dataset, unknown table, unknown field filter); 401; 404 "AI Agent is not enabled." |
| `GET /api/FoundryAi/export/{exportId}/csv` | `FoundryAiExportController.ExportCsv → IFoundryCsvExportUseCase.Execute` | `exportId` | a CSV file | 200; 401; 404 "The requested CSV export is no longer available." |

### 5e. Client logging

Called from `shared/clientLogger.ts`: `clientLogger.logToServer()` (section 4e).

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/ClientLogger` | `ClientLoggerController.LogClientErrorAsync → IAnalyticsLogger.LogError` | `moduleName`, `message`, `errorId`; header `RequestVerificationToken` (not validated) | `status` "success" | 200; 401 |

Besides these, the Power BI embed loads the report from `https://app.powerbi.com` (allowed in the CSP `frame-src`), and the Google tag talks to Google Analytics.
