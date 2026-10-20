---
summary: Overview of the agency Admin page (/Admin) - the MVC host, the Vue shell and its six tabs, the session check on tab switch, who may open Admin and how agency and company scope is enforced, and a table of every Admin API with the section doc that covers it; read first before working on any Admin tab.
area: architecture
---

# Admin plumbing

This doc follows an administrator from opening `/Admin` to working in one of its tabs, and names what connects to what and what calls what on the way. It stops at the edge of each tab: the section docs cover what a tab does inside (see `Admin-Users-Plumbing.md`, `Admin-ReportsCategories-Plumbing.md`, `Admin-BrandingSettings-Plumbing.md`, and `Admin-Notify-Plumbing.md`).

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Page controller | `Controllers/AdminController.cs` | `Index` (renders the host view) and `ValidateSession` (the tab-switch session probe). |
| Host view | `Views/Admin/Index.cshtml` | No layout. Page title, `_AppBaseShim`, Google Analytics tag, the antiforgery hidden field, `#admin-home-app`, and the `src/admin/home/main.ts` Vite entry. |
| App base shim | `Views/Shared/_AppBaseShim.cshtml` | Sets `window.appBase` and `window.appUrl`, and prefixes `fetch` calls when the app runs under a virtual directory. |
| Bootstrap | `ClientApp/src/admin/home/main.ts` | Creates the Vue app from `AdminHome.vue`, runs `configurePrimeVue(app, 'blue')`, mounts on `#admin-home-app`. |
| Tab host | `ClientApp/src/admin/home/AdminHome.vue` | The six tabs, `initialTab()` from `?rtn=`, `onTabSelect()` with the unsaved-changes guards, and `confirmSession()`. |
| Shell | `ClientApp/src/admin/home/AdminShell.vue` | Header (logo, title, agency line), account menu (Reporting, Logout), `loadContext()`, and the live branding listener `onHeaderBrandingChanged()`. |
| Shell API client | `ClientApp/src/admin/home/api.ts` | `getShellContext()` and `validateSession()`. |
| Shell types | `ClientApp/src/admin/home/types.ts` | `AdminShellContext`. |
| Shell context API | `Controllers/Api/Admin/AdminShellController.cs` | `GET /api/admin/shell-context`. |
| Shell context use case | `GraspANALYTICS.Core/Application/Admin/Shell/UseCases/AdminGetShellContextUseCase.cs` | `ExecuteAsync`: header values from the app session, `GeneralSettings`, and `ICorporationsService`. |
| Shell contract | `Api/Shell/Contracts/AdminShellContextResponse.cs` + `Api/Shell/Contracts/ShellContractMapper.cs` | Response record and `ToResponse()`, which turns the logo bytes into base64. |
| Live header branding | `ClientApp/src/admin/branding/headerBranding.ts` | `publishHeaderBranding()` and `HEADER_BRANDING_CHANGED_EVENT` (`admin:header-branding-changed`). |
| Session filter | `Classes/CustomAuthorizeAttribute.cs` | `OnAuthorization`: no app session → 401 (`ExpiredLoginResult`); duplicate login with `LastLoginWins` → 460 (`DuplicateLoginResult`). |
| User context filter | `Helpers/RequireUserContextAttribute.cs` → `GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs` | Builds `UserContext` from the app session and stores it in `HttpContext.Items`; no context → 401. |
| User context accessor | `Extensions/HttpContextExtensions.cs` | `GetUserContext()`. |
| Agency scope | `GraspANALYTICS.Core/Application/Agencies/AgenciesService.cs` | `GetSelectedAgencyIdAsync()`, the agency every session-scoped Admin service works on. |
| Role helpers | `GraspANALYTICS.Data/Enumerations/Enums.cs` | `Roles`, `IsCompanyRole()`, `IsAgencyRole()`, `IsAdminRole()`, `IsSuperAdminRole()`. |
| Shared HTTP | `ClientApp/src/shared/http.ts` + `ClientApp/src/shared/antiforgery.ts` + `ClientApp/src/shared/apiError.ts` | `requestJson()`, `sendJson()`, `errorMessage()`, `antiforgeryToken()`, `ApiError`. |
| Shared dialogs | `ClientApp/src/shared/dialogs.ts` | `showOk()`, `showConfirm()`, `handleNullSession()`, `escapeHtml()`. |
| Shared app base | `ClientApp/src/shared/appBase.ts` | `appUrl()`. |
| Shared logging | `ClientApp/src/shared/clientLogger.ts` | `logToServer()` → `POST /api/ClientLogger`. |
| Shared PrimeVue setup | `ClientApp/src/shared/primevue.ts` | `configurePrimeVue()`: Aura preset with the blue palette, and `ToastService`. |
| Tab components | `ClientApp/src/admin/users/AdminUsers.vue`, `ClientApp/src/admin/reports/AdminReports.vue`, `ClientApp/src/admin/notify/AdminNotify.vue`, `ClientApp/src/admin/settings/AdminSettings.vue`, `ClientApp/src/admin/branding/AdminBranding.vue`, `ClientApp/src/admin/categories/AdminCategories.vue` | One component per tab; each has its own `api.ts` and `types.ts`. |
| Admin APIs | `Controllers/Api/Admin/*.cs` + `Controllers/Api/AdminNotificationsController.cs` | Every endpoint in section 4. |

## 1. Loading the page

```text
Browser GET /Admin
  └─ AdminController.Index            [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute]
       ├─ not signed in → cookie scheme redirects to /Login
       ├─ signed in with another role → cookie scheme redirects to /Forbidden
       ├─ CustomAuthorizeAttribute.OnAuthorization
       │     no app session or blank username → 401, re-executed as /Error?statusCode=401
       │     LastLoginWins "true" and another session id → 460, re-executed as /Error?statusCode=460
       ├─ ISessionService.IsSessionActive is false
       │     → ISessionService.Logout(), then 302 to /Login
       └─ AdminController.InitializeViewData
            ├─ ISessionService.GetAppSession(false)
            │     null → IAnalyticsLogger.LogError("AdminController Index AppSession is null")
            └─ copies AppSession.GoogleAnalyticsID into ViewData["GoogleAnalyticsID"]
            → renders Views/Admin/Index.cshtml
```

- `CustomAuthorizeAttribute` derives from `AuthorizeAttribute`, so it also demands a signed-in user, and it runs on the class and again on `Index`.
- The 401 and 460 pages come from `UseGraspStatusCodePages` (`CompositionRoot/GraspPipelineExtensions.cs`), which re-executes `/Error?statusCode={0}` for every path outside `/api`.
- `ISessionService.IsSessionActive` (`Services/SessionService.cs`) is true when the request has an ASP.NET session, a signed-in user name, and an app session in the memory cache under that name.
- `Index` sets status 403 before it returns the redirect; the redirect replaces it, so the browser sees 302.

`Views/Admin/Index.cshtml` uses no layout. It renders:

- `<title>` from `GeneralSettings.Value.ApplicationTitle`.
- `_AppBaseShim`, then the Google Analytics tag with `ViewData["GoogleAnalyticsID"]`.
- `@Html.AntiForgeryToken()`. `shared/antiforgery.ts` reads this hidden `__RequestVerificationToken` field for every write the page sends.
- `<div id="admin-home-app">` and `<vite-asset entry="src/admin/home/main.ts" />`.

The view does not load `wwwroot/js/site.js` or `wwwroot/js/dialogs.js`, so the jQuery-era `ShowOkDialog`, `ShowConfirmDialog`, and `HandleNullSession` globals are not on this page (section 2d).

Then the browser runs `main.ts`:

```text
Browser loads src/admin/home/main.ts
  └─ main.ts
       ├─ createApp(AdminHome)
       ├─ primevue.configurePrimeVue(app, 'blue')
       └─ mounts on #admin-home-app
            ├─ AdminHome.initialTab()                      (section 2b)
            └─ AdminShell.onMounted
                 ├─ adds the admin:header-branding-changed listener
                 └─ AdminShell.loadContext()
                      └─ api.getShellContext()  → GET /api/admin/shell-context
```

On the server:

```text
AdminShellController.GetShellContextAsync     [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [RequireUserContext]
  ├─ RequireUserContextAttribute.OnActionExecutionAsync
  │    └─ IUserContextResolver.GetAsync(...)   UserContextResolver
  │         no app session, or the user is missing from the analytics database → 401
  ├─ IAdminGetShellContextUseCase.ExecuteAsync(userContext, cancellationToken)   AdminGetShellContextUseCase
  │    ├─ ISessionService.GetAppSession(exceptionOnNull: false)
  │    │     null → controller returns 401
  │    └─ AdminGetShellContextUseCase.ResolveCorporationNameAsync(userContext, cancellationToken)
  │         ├─ not a company role → no corporation name
  │         └─ ICorporationsService.GetCorporationOptionAsync(agencyId, corporationId, cancellationToken)
  └─ ShellContractMapper.ToResponse(context)
       → 200 with the shell context
```

The response fields and where each comes from:

| Field | Source |
|---|---|
| `applicationTitle` | `GeneralSettings.ApplicationTitle` (the app-wide setting, not the agency's branding title) |
| `username` | `AppSession.CurrentUser.Username` |
| `agencyName` | `AppSession.Agency.AgencyName` |
| `agencyId` | `AppSession.Agency.AgencyID` |
| `corporationName` | `ICorporationsService.GetCorporationOptionAsync` for a Company Admin or Company User; otherwise `null` |
| `headerLogo` | `AppSession.Style.HeaderLogo` as base64, or `null` when empty |
| `headerTextColor` | `AppSession.Style.HeaderTextColor` |
| `headerBackgroundColor` | `AppSession.Style.HeaderBackgroundColor` |

`AppSession.Style` is filled at sign-in by `SessionService.PopulateBrandingSession` from the agency's branding row.

## 2. The shell

### 2a. Tabs

`AdminHome.vue` wraps PrimeVue `Tabs` in `AdminShell`. The tabs, in order:

| Value | Label | Mounts | Unsaved-changes guard |
|---|---|---|---|
| `users` | Users | `admin/users/AdminUsers.vue` | `adminUsersHasUnsavedChanges`, `adminUsersGuardSwitch` |
| `reports` | Report | `admin/reports/AdminReports.vue` | `adminReportsHasUnsavedChanges`, `adminReportsGuardSwitch` |
| `notify` | Notify | `admin/notify/AdminNotify.vue` | none |
| `settings` | Settings | `admin/settings/AdminSettings.vue` | `adminSettingsHasUnsavedChanges`, `adminSettingsGuardSwitch` |
| `branding` | Branding | `admin/branding/AdminBranding.vue` | `adminBrandingHasUnsavedChanges`, `adminBrandingGuardSwitch` |
| `categories` | Categories | `admin/categories/AdminCategories.vue` | none |

- The `Tabs` element is not `lazy`, so PrimeVue mounts all six panels at page load and hides the inactive ones. Every tab loads its data on mount, not on first selection.
- Each guarded tab puts its two functions on `globalThis` in `onMounted` and deletes them in `onUnmounted`. `AdminHome` reads them through `AdminHome.tabGuards()`.

### 2b. Choosing the first tab

`AdminHome.initialTab()` reads `?rtn=`. `reports`, `settings`, and `categories` open that tab; any other value, or none, opens `users`.

### 2c. Switching tabs

```text
Browser click a tab (AdminHome.vue)
  └─ AdminHome.onTabSelect(value)
       ├─ same tab → nothing
       ├─ AdminHome.dirtyGuards(previous)
       │    └─ AdminHome.tabGuards()
       ├─ the tab being left has unsaved changes
       │    ├─ shows the target, waits a tick, and puts the previous tab back
       │    └─ AdminHome.runGuards(guards)
       │         └─ the tab's guardSwitch, for example AdminSettings.guardSwitch()
       │              ├─ shows the tab's own PrimeVue Dialog
       │              ├─ user stays → switch cancelled
       │              └─ user saves or discards → switch goes ahead
       ├─ selects the target tab
       └─ AdminHome.confirmSession()
            └─ api.validateSession()  → POST /Admin/ValidateSession
```

- The back-and-forth with `nextTick` undoes PrimeVue's optimistic tab change while the guard dialog is open.
- `confirmSession()` runs after the new tab is already showing, and the page does not wait for it.

On the server:

```text
AdminController.ValidateSession        [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute]
  ├─ not signed in → cookie scheme redirects to /Login, which answers 401   see Login-Plumbing.md §1
  ├─ CustomAuthorizeAttribute.OnAuthorization
  │     no app session → 401
  │     duplicate login with LastLoginWins → 460
  └─ returns true
```

- `ValidateSession` has no HTTP verb attribute and no antiforgery check. The client sends a POST with the `RequestVerificationToken` header because `http.sendJson()` always adds it.
- Any failed probe sends the browser to the error page:

```text
AdminHome.confirmSession() catches an ApiError
  ├─ dialogs.handleNullSession(error)
  │     no HandleNullSession global on this page; status 401 → location /Error?statusCode=401
  └─ location /Error?statusCode=<status>
```

### 2d. 401 in the header and 401 in a tab

| Where | Call | On 401 | On any other failure |
|---|---|---|---|
| Header | `AdminShell.loadContext()` → `GET /api/admin/shell-context` | `location` → `/Login` | `clientLogger.logToServer('AdminShell', ...)`; the header keeps the page title and shows "Account" |
| Tab switch | `AdminHome.confirmSession()` → `POST /Admin/ValidateSession` | `location` → `/Error?statusCode=401` | `location` → `/Error?statusCode=<status>` (460 for a duplicate login) |
| A tab's own API call | the tab calls `dialogs.handleNullSession(error)` in its catch | `location` → `/Error?statusCode=401` | the tab shows its own toast or message |

### 2e. Header and account menu

- `AdminShell.title` shows `<applicationTitle> Administration` (a non-breaking space between). Before the context arrives it uses the view's `<title>`.
- `AdminShell.logoUrl` shows `headerLogo` as a `data:image/jpeg;base64,` image, unless the Branding tab has sent a `logoUrl`.
- `AdminShell.agencyLine` shows `<agencyName> (#<agencyId>)`, followed by ` - <corporationName>` for a company role.
- `AdminShell.headerStyle` applies `headerBackgroundColor` and `headerTextColor` when present; otherwise the CSS default (`--p-primary-800`, white text).
- The account button shows `username` and toggles a PrimeVue popup `Menu` with two links:

  | Item | Link |
  |---|---|
  | Reporting | `appUrl('/Home/Index')` |
  | Logout | `appUrl('/Home/Logout')`, a plain GET to `HomeController.Logout` |

Live branding from the Branding tab:

```text
Browser click "Save" (AdminBranding.vue)
  └─ AdminBranding saves, then headerBranding.publishHeaderBranding(change)
       └─ dispatches admin:header-branding-changed on globalThis
            └─ AdminShell.onHeaderBrandingChanged(event)
                 └─ merges applicationTitle, headerTextColor, headerBackgroundColor, logoUrl into the header
```

`AdminBranding` also publishes `logoUrl` after it loads the branding and after it deletes the logo. What it saves: see `Admin-BrandingSettings-Plumbing.md`.

```text
HomeController.Logout                  [Authorize(Roles = "Agency User,Agency Admin,Super Admin,Company User, Company Admin")], [CustomAuthorize]
  ├─ ISessionService.Logout()
  └─ 302 to the agency's LogoutRedirect controller, or to /Login when it is blank
```

### 2f. Dialogs, toasts, and shared code

- `dialogs.showOk()` and `dialogs.showConfirm()` call `ShowOkDialog` and `ShowConfirmDialog` when those globals exist. The Admin view loads neither `dialogs.js` nor `site.js`, so on this page they fall back to the browser's `alert` and `confirm` with the HTML stripped. The Reports and Branding tabs use `showOk()` for errors.
- The styled dialogs on this page are PrimeVue: each guarded tab renders its own unsaved-changes `Dialog`, and tab-specific dialogs live in the tab folders (for example `admin/notify/SaveNotificationDialog.vue`, `admin/users/SendPasswordResetDialog.vue`).
- Toasts: `configurePrimeVue()` installs `ToastService`. Each tab renders its own `Toast` with its own group (`admin-users`, `admin-user-edit`, `admin-notify`, `admin-settings`, `admin-branding`, `admin-categories`, and the Reports tab's `TOAST_GROUP`), so one tab's toasts never show in another tab's `Toast`.

Shared code the Admin page uses:

| Module | Used by | What for |
|---|---|---|
| `shared/primevue.ts` | `main.ts` | `configurePrimeVue(app, 'blue')` |
| `shared/http.ts` | `home/api.ts`, every tab's `api.ts`, `AdminShell.vue` | `requestJson()`, `sendJson()` (adds `RequestVerificationToken`), `errorMessage()` |
| `shared/antiforgery.ts` | `http.ts`, and the tabs that call `fetch` directly (branding logo upload, users, notify) | `antiforgeryToken()` from the hidden field |
| `shared/apiError.ts` | `http.ts`, `AdminShell.vue`, tabs | `ApiError` with `status` and `body` |
| `shared/appBase.ts` | `AdminHome.vue`, `AdminShell.vue`, tabs | `appUrl()` |
| `shared/dialogs.ts` | `AdminHome.vue`, tabs | `handleNullSession()`, `showOk()`, `escapeHtml()` |
| `shared/clientLogger.ts` | `AdminShell.vue`, tabs | `logToServer()` → `POST /api/ClientLogger` |
| `shared/CorporationSelector.vue` + `shared/corporations.ts` | `admin/users/UserForm.vue` | company picker → `GET /api/Corporations/Search`, `GET /api/Corporations/GetOption`; see `Admin-Users-Plumbing.md` |

## 3. Who may open Admin and what they see

### 3a. Roles

The role names come from the sign-in cookie's role claims; the ids are `Enums.Roles`.

| Role | Id | Opens `/Admin` | Agency scope | Company scope |
|---|---|---|---|---|
| Agency Admin | 2 | yes; sign-in lands here (`SignInCompletionService` returns `AdminHomeDestination` for this role when the caller prefers the Admin home) | the agency they signed in to | none |
| Company Admin | 5 | yes, from the account menu on Home | the agency they signed in to | their own corporation (`CurrentUser.CorporationID`); the header shows the corporation name, and some services narrow by company role (for example `AdminReportsService.GetAgencyReportsAsync` hides TMC-only reports) |
| Super Admin | 3 | yes, from the account menu on Home | the agency in `CurrentUser.AgencyID`, which the Super Admin agency picker changes through `AgenciesService.SetSelectedAgencyIdAsync` | none |
| Agency User, Company User | 1, 4 | no; the cookie scheme sends them to `/Forbidden` | | |

`AccountHeader.vue` shows the "Administration" link when `isAdmin` is true, which `UserAccountService` sets from `Enums.IsAdminOrSuperAdminRole(roleId)`.

### 3b. How the APIs enforce it

Every controller in section 4 carries the same building blocks:

- `[Authorize(Roles = "...")]`. All Admin controllers allow `Agency Admin,Super Admin,Company Admin`, except `AdminAiAccessController` (class `[Authorize]`, every action `[Authorize(Roles = "Super Admin")]`) and `FoundryMetadataAdminController` (`Super Admin`).
- `[CustomAuthorizeAttribute]` on every Admin controller: no app session → 401; duplicate login → 460.
- `[RequireUserContext]` only on `AdminShellController`, `AdminUsersController`, and `AdminUserEmailController`. Those actions read `HttpContext.GetUserContext()` and pass the `UserContext` (`UserId`, `RoleId`, `AgencyId`, `CorporationId`, `DataSecurityCorporationId`, `WorkspaceId`, `CorrelationId`) into the use case, which applies role and company rules.
- `[ValidateAntiForgeryToken]` on every write, except the four `FoundryMetadataAdminController` actions. The header name is `RequestVerificationToken` (`Program.cs`: `AddAntiforgery(options => options.HeaderName = "RequestVerificationToken")`). A missing or wrong token fails with 400.

Where the agency comes from:

```text
Admin API call without [RequireUserContext]
  └─ the service, for example AdminSettingsService.GetAsync
       └─ IAgenciesService.GetSelectedAgencyIdAsync()   AgenciesService
            ├─ CurrentSession.CurrentUser.AgencyID > 0 → that agency
            └─ otherwise the "SuperAdminAgencyID" memory-cache entry, or 0
                 0 → the service answers "agency not selected"
```

```text
Admin API call with [RequireUserContext]
  └─ RequireUserContextAttribute.OnActionExecutionAsync
       └─ IUserContextResolver.GetAsync(...)   UserContextResolver
            ├─ ISessionService.GetAppSession(false)
            └─ IUsersService.GetUserAsync(userId, agencyId, cancellationToken) → DataSecurityCorporationId
```

Both paths read the same in-memory app session, so a Super Admin's agency picker changes the scope of both.

## 4. Admin API endpoints

Antiforgery failures (400), missing app session (401), and duplicate login (460) apply to every row and are not repeated. Rows with `[RequireUserContext]` can also answer 401 when no user context resolves.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /Admin` | `AdminController.Index` | none | the host view | 200; 302 |
| `POST /Admin/ValidateSession` | `AdminController.ValidateSession` | `RequestVerificationToken` header (not checked) | `true` | 200; 401; 460 |
| `GET /api/admin/shell-context` | `AdminShellController.GetShellContextAsync → IAdminGetShellContextUseCase.ExecuteAsync` | none | the shell context (section 1) | 200; 401 |
| `GET /api/admin/users` | `AdminUsersController.GetUsersAsync → IAdminGetUsersUseCase.ExecuteAsync` | none | user list | 200; 401 |
| `GET /api/admin/users/{userId}` | `AdminUsersController.GetUserAsync → IAdminGetUserUseCase.ExecuteAsync` | `userId` | user detail | 200; 401; 404 |
| `GET /api/admin/users/{userId}/sort-filters` | `AdminUsersController.GetUserSortFiltersAsync → IAdminGetUserSortFiltersUseCase.ExecuteAsync` | `userId`, `corporationId` | the user's custom filters | 200; 401 |
| `POST /api/admin/users` | `AdminUsersController.CreateUserAsync → IAdminSaveUserUseCase.ExecuteAsync` | the user form | write result | 201; 400; 401; 403; 404; 409 |
| `PUT /api/admin/users/{userId}` | `AdminUsersController.UpdateUserAsync → IAdminSaveUserUseCase.ExecuteAsync` | `userId`, the user form | write result | 200; 400; 401; 403; 404; 409 |
| `DELETE /api/admin/users/{userId}` | `AdminUsersController.DeleteUserAsync → IAdminDeleteUserUseCase.ExecuteAsync` | `userId` | none | 204; 400; 401; 403; 404; 409 |
| `POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail` | `AdminUserEmailController.SendPasswordResetEmailAsync → IAdminSendPasswordResetEmailUseCase.ExecuteAsync` | `userId`, `notificationId` | write result | 200; 400; 401; 403; 404 |
| `POST /api/AdminUserEmail/SendEmails` | `AdminUserEmailController.SendEmailsAsync → IAdminSendUserEmailsUseCase.ExecuteAsync` | the bulk email request | bulk email result | 200; 400; 401 |
| `GET /api/admin/notifications` | `AdminNotificationsController.GetNotificationsAsync → IAdminNotificationService.GetNotificationsAsync` | none | notification list | 200 |
| `GET /api/admin/notifications/with-default` | `AdminNotificationsController.GetNotificationsWithDefaultAsync → IAdminNotificationService.GetNotificationsWithDefaultAsync` | `context` (`NewUser`, `ChangePassword`, `None`) | notifications with the default flagged | 200 |
| `GET /api/admin/notifications/{notificationId}` | `AdminNotificationsController.GetNotificationAsync → IAdminNotificationService.GetNotificationAsync` | `notificationId` | one notification | 200; 404 |
| `POST /api/admin/notifications` | `AdminNotificationsController.CreateNotificationAsync → IAdminNotificationService.CreateNotificationAsync` | `title`, `subject`, `message`, `isActive` | write result | 201; 400; 404; 409 |
| `PUT /api/admin/notifications/{notificationId}` | `AdminNotificationsController.UpdateNotificationAsync → IAdminNotificationService.UpdateNotificationAsync` | `notificationId`, `title`, `subject`, `message`, `isActive` | write result | 200; 400; 404; 409 |
| `DELETE /api/admin/notifications/{notificationId}` | `AdminNotificationsController.DeleteNotificationAsync → IAdminNotificationService.DeleteNotificationAsync` | `notificationId` | delete result | 200; 400; 404 |
| `GET /api/admin/reports` | `AdminReportsController.GetReportsAsync → IAdminReportsService.GetAgencyReportsAsync` | none | report list | 200 |
| `PATCH /api/admin/reports/{id}` | `AdminReportsController.RenameReportAsync → IAdminReportsService.RenameReportAsync` | `id`, `name` | write result | 200; 400; 404 |
| `PATCH /api/admin/reports/{id}/active` | `AdminReportsController.SetReportActiveAsync → IAdminReportsService.SetReportActiveAsync` | `id`, `isActive` | write result | 200; 400; 404 |
| `GET /api/admin/categories` | `AdminCategoriesController.GetCategoriesAsync → IAdminCategoryService.GetCategoriesAsync` | `isSystemCategory` | category list | 200 |
| `GET /api/admin/categories/manage` | `AdminCategoriesController.GetManageableCategoriesAsync → IAdminCategoryService.GetManageableCategoriesAsync` | none | category list | 200 |
| `GET /api/admin/reports/{reportId}/categories` | `AdminCategoriesController.GetReportCategoriesAsync → IAdminCategoryService.GetReportCategoriesAsync` | `reportId` | the report's categories | 200 |
| `GET /api/admin/reports/{reportId}/categories/available` | `AdminCategoriesController.GetAvailableReportCategoriesAsync → IAdminCategoryService.GetAvailableCategoriesForReportAsync` | `reportId` | assignable categories | 200 |
| `POST /api/admin/categories` | `AdminCategoriesController.CreateCategoryAsync → IAdminCategoryService.CreateCategoryAsync` | `name`, `isSystemCategory` | write result | 201; 400; 403; 404; 409 |
| `PATCH /api/admin/categories/{id}` | `AdminCategoriesController.RenameCategoryAsync → IAdminCategoryService.RenameCategoryAsync` | `id`, `name` | write result | 200; 400; 403; 404; 409 |
| `PATCH /api/admin/categories/{id}/active` | `AdminCategoriesController.ToggleCategoryActiveAsync → IAdminCategoryService.ToggleCategoryActiveAsync` | `id` | write result | 200; 400; 403; 404; 409 |
| `POST /api/admin/reports/{reportId}/categories/{categoryId}` | `AdminCategoriesController.AssignCategoryToReportAsync → IAdminCategoryService.AssignCategoryToReportAsync` | `reportId`, `categoryId` | write result | 200; 400; 403; 404; 409 |
| `DELETE /api/admin/categories/{id}` | `AdminCategoriesController.DeleteCategoryAsync → IAdminCategoryService.DeleteCategoryAsync` | `id` | none | 204; 400; 403; 404; 409 |
| `GET /api/admin/settings` | `AdminSettingsController.GetAsync → IAdminSettingsService.GetAsync` | none | SMTP sender, default notification ids, link timeout | 200; 400 |
| `PUT /api/admin/settings` | `AdminSettingsController.SaveAsync → IAdminSettingsService.SaveAsync` | `smtpSender`, the three default notification ids, `changePasswordLinkTimeoutMinutes` | save result | 200; 400; 403 |
| `GET /api/admin/branding` | `AdminBrandingController.GetAsync → IAdminBrandingService.GetAsync` | none | branding | 200; 400 |
| `PUT /api/admin/branding` | `AdminBrandingController.SaveAsync → IAdminBrandingService.SaveAsync` | `applicationTitle`, `headerTextColor`, `headerBackgroundColor` | write result | 200; 400; 404 |
| `PUT /api/admin/branding/logo` | `AdminBrandingController.SaveLogoAsync → IAdminBrandingService.SaveLogoAsync` | multipart `file` (request limit 4 MB) | write result | 200; 400; 404; 413 |
| `DELETE /api/admin/branding/logo` | `AdminBrandingController.DeleteLogoAsync → IAdminBrandingService.DeleteLogoAsync` | none | none | 204; 400; 404 |
| `GET /api/admin/ai-access` | `AdminAiAccessController.GetForSelectedAgencyAsync → IAiUsageService.GetForSelectedAgencyAsync` | none | the selected agency's AI limits | 200 |
| `PUT /api/admin/ai-access` | `AdminAiAccessController.SetAsync → IAiUsageService.SetLimitAsync` | `userId`, `agencyId`, `tokenLimit`, `accessEnabled`, `daxQueriesEnabled` | AI usage status | 200; 404 |
| `POST /api/admin/ai-access/reset` | `AdminAiAccessController.ResetAsync → IAiUsageService.ResetAsync` | `userId`, `agencyId` | AI usage status | 200; 404 |
| `POST /api/admin/foundry-metadata/refresh` | `FoundryMetadataAdminController.RefreshAllAsync → IFoundryMetadataAdminService.RefreshAllAsync` | none | refresh result | 200 |
| `POST /api/admin/foundry-metadata/reports/{reportId}/refresh` | `FoundryMetadataAdminController.RefreshReportAsync → IFoundryMetadataAdminService.RefreshReportAsync` | `reportId` | refresh result | 200; 404 |
| `DELETE /api/admin/foundry-metadata` | `FoundryMetadataAdminController.DeleteAllAsync → IFoundryMetadataAdminService.DeleteAllAsync` | none | number of reports cleared | 200 |
| `DELETE /api/admin/foundry-metadata/reports/{reportId}` | `FoundryMetadataAdminController.DeleteReportAsync → IFoundryMetadataAdminService.DeleteReportAsync` | `reportId` | none | 204; 404 |

Which UI calls each group, and where the detail lives:

| Endpoints | Called from | Section doc |
|---|---|---|
| `/Admin`, `/Admin/ValidateSession`, `/api/admin/shell-context` | `admin/home` (this doc) | this doc |
| `/api/admin/users*` | Users tab (`admin/users/api.ts`); Notify tab reads `GET /api/admin/users` (`admin/notify/api.ts`); the Super Admin user editor (`superadmin/user-edit/api.ts`) | see `Admin-Users-Plumbing.md` |
| `/api/AdminUserEmail/{userId}/SendPasswordResetEmail` | Users tab (`admin/users/api.ts`) | see `Admin-Users-Plumbing.md` |
| `/api/AdminUserEmail/SendEmails` | Notify tab (`admin/notify/api.ts`) | see `Admin-Users-Plumbing.md` and `Admin-Notify-Plumbing.md` |
| `/api/admin/notifications*` | Notify tab (list, get, create, update, delete); Settings tab (`GET /api/admin/notifications`); Users tab (`GET /api/admin/notifications/with-default`) | see `Admin-Notify-Plumbing.md` |
| `/api/admin/reports`, `/api/admin/reports/{id}`, `/api/admin/reports/{id}/active`, `POST /api/admin/reports/{reportId}/categories/{categoryId}` | Report tab (`admin/reports/api.ts`) | see `Admin-ReportsCategories-Plumbing.md` |
| `/api/admin/categories*` | Categories tab (`admin/categories/api.ts`); Report tab reads `?isSystemCategory=false`; Super Admin system categories and system reports read `?isSystemCategory=true` | see `Admin-ReportsCategories-Plumbing.md` |
| `GET /api/admin/reports/{reportId}/categories`, `GET /api/admin/reports/{reportId}/categories/available` | no caller in `ClientApp/src` | see `Admin-ReportsCategories-Plumbing.md` |
| `/api/admin/settings` | Settings tab (`admin/settings/api.ts`) | see `Admin-BrandingSettings-Plumbing.md` |
| `/api/admin/branding*` | Branding tab (`admin/branding/api.ts`) | see `Admin-BrandingSettings-Plumbing.md` |
| `/api/admin/ai-access*` | not the Admin page: `shared/aiAccess.ts`, used by the Super Admin agency users list (`superadmin/agency-users/AgencyUsers.vue`) and user editor (`superadmin/user-edit/UserEdit.vue`) | see `Admin-Users-Plumbing.md` |
| `/api/admin/foundry-metadata*` | no caller in `ClientApp/src`; called by hand (Super Admin only) | this doc |
