---
summary: How the Admin Users tab works end to end - list and search, the user editor (roles, corporation, reports, custom filters), create, update, delete, the welcome and password-reset emails, the bulk email endpoint, and the Super Admin AI access endpoints; read before debugging or changing user management.
area: architecture
---

# Admin Users plumbing

This doc follows an Agency Admin, Company Admin, or Super Admin through the **Users** tab of `/Admin`: loading the list, opening the editor, saving, deleting, and sending a password reset email. It also covers how the emails are built and sent, and the AI access endpoints that sit next to the user endpoints. For the Admin shell (the Razor host, the tab strip, the session check, and the antiforgery input) see Admin-Plumbing.md.

The Super Admin user editor (`GraspANALYTICS/ClientApp/src/superadmin/user-edit/`) calls the same `GET /api/admin/users/{userId}`, `POST /api/admin/users`, `PUT /api/admin/users/{userId}`, and `DELETE /api/admin/users/{userId}` endpoints, so every server rule in sections 3-6 applies to it too. It loads custom filters and notifications from other endpoints (`/api/Corporations/GetCorporationSorts`, `/api/SuperAdminNotifications/GetNotificationsWithDefault`) and is the only caller of the AI access endpoints in this doc besides the Super Admin agency users page. Its UI is not described here.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Tab host | `GraspANALYTICS/ClientApp/src/admin/home/AdminHome.vue` | Mounts `AdminUsers.vue` in the `users` tab panel; calls the `adminUsersGuardSwitch` global before leaving the tab. |
| Users island | `GraspANALYTICS/ClientApp/src/admin/users/AdminUsers.vue` | Switches between the list and the editor. `openUser()`, `closeUser()`, `hasUnsavedChanges()`, `guardSwitch()`, `saveAndLeave()`, `discardAndLeave()`. Publishes `adminUsersHasUnsavedChanges` and `adminUsersGuardSwitch` on `globalThis`. |
| User list | `GraspANALYTICS/ClientApp/src/admin/users/UsersList.vue` | The grid with search, sort, and paging. `refresh()`, `openResetDialog()`; emits `edit` and `create`. |
| User editor | `GraspANALYTICS/ClientApp/src/admin/users/UserForm.vue` | User, Assign Reports, and Custom Filters tabs. `load()`, `apply()`, `applyTmcFilter()`, `validationError()`, `buildPayload()`, `save()`, `doDelete()`, `hasUnsavedChanges()`. |
| Reset email dialog | `GraspANALYTICS/ClientApp/src/admin/users/SendPasswordResetDialog.vue` | Picks a notification template and sends the reset email. `load()`, `send()`. |
| Users API client | `GraspANALYTICS/ClientApp/src/admin/users/api.ts` | `getUsers()`, `getUser()`, `createUser()`, `updateUser()`, `deleteUser()`, `getSortFilters()`, `getNotifications()`, `sendPasswordResetEmail()`. |
| Client types and formatting | `GraspANALYTICS/ClientApp/src/admin/users/types.ts` + `GraspANALYTICS/ClientApp/src/admin/users/format.ts` | `UserListItem`, `UserDetail`, `UserWritePayload`, `UserWriteResult`, `NotificationOption`; `formatDateTime()`. |
| Corporation picker | `GraspANALYTICS/ClientApp/src/shared/CorporationSelector.vue` → `GraspANALYTICS/ClientApp/src/shared/corporations.ts` | `searchCorporations()` (`GET /api/Corporations/Search`) and `getCorporationOption()` (`GET /api/Corporations/GetOption`). |
| Shared client helpers | `GraspANALYTICS/ClientApp/src/shared/antiforgery.ts` + `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `antiforgeryToken()` reads the `__RequestVerificationToken` input; `handleNullSession()`. |
| Users API | `GraspANALYTICS/Controllers/Api/Admin/AdminUsersController.cs` | `GET /api/admin/users`, `GET /api/admin/users/{userId}`, `GET /api/admin/users/{userId}/sort-filters`, `POST /api/admin/users`, `PUT /api/admin/users/{userId}`, `DELETE /api/admin/users/{userId}`. |
| User email API | `GraspANALYTICS/Controllers/Api/Admin/AdminUserEmailController.cs` | `POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail`, `POST /api/AdminUserEmail/SendEmails`. |
| Notification list API | `GraspANALYTICS/Controllers/Api/AdminNotificationsController.cs` | `GET /api/admin/notifications/with-default`. |
| AI access API | `GraspANALYTICS/Controllers/Api/Admin/AdminAiAccessController.cs` | `GET /api/admin/ai-access`, `PUT /api/admin/ai-access`, `POST /api/admin/ai-access/reset` (Super Admin only). |
| Contracts | `GraspANALYTICS/Api/Users/Contracts/UserContractMapper.cs` + `UserWriteRequest.cs` + `UserDetailResponse.cs` + `UserResponse.cs` + `UserWriteResponse.cs` + `SendPasswordResetEmailRequest.cs` + `SendUserEmailsRequest.cs` + `BulkEmailResponse.cs` | `ToSaveRequest()`, `ToCommand()`, `ToResponse()`, `ToResponseList()`. |
| AI contracts | `GraspANALYTICS/Api/Ai/Contracts/AiContractMapper.cs` + `SetAiUsageLimitRequest.cs` + `ResetAiUsageLimitRequest.cs` + `AiUsageLimitsResponse.cs` + `AiUsageStatusResponse.cs` | `ToResponse()`. |
| Request filters | `GraspANALYTICS/Helpers/RequireUserContextAttribute.cs` + `GraspANALYTICS/Classes/CustomAuthorizeAttribute.cs` + `GraspANALYTICS/Helpers/CaseInsensitiveJsonAttribute.cs` | Builds the `UserContext`; checks the app session; swaps the response JSON formatter. |
| User context | `GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs` | `GetAsync()`: user id, role, corporation, data-security corporation, and agency from the app session. |
| List use case | `GraspANALYTICS.Core/Application/UseCases/AdminGetUsersUseCase.cs` | `ExecuteAsync()`. |
| Detail use case | `GraspANALYTICS.Core/Application/UseCases/AdminGetUserUseCase.cs` | `ExecuteAsync()`; builds the role options and the report lists. |
| Custom filters use case | `GraspANALYTICS.Core/Application/UseCases/AdminGetUserSortFiltersUseCase.cs` → `GraspANALYTICS.Core/Application/UseCases/GetCorporationSortsUseCase.cs` | `ExecuteAsync()`. |
| Save use case | `GraspANALYTICS.Core/Application/UseCases/AdminSaveUserUseCase.cs` | `ExecuteAsync()`: role and scope rules, then the save, then the optional email. |
| Delete use case | `GraspANALYTICS.Core/Application/UseCases/AdminDeleteUserUseCase.cs` | `ExecuteAsync()`. |
| User service | `GraspANALYTICS.Core/Application/Users/UsersService.cs` | `GetUserAsync()`, `SaveUserAsync()`, `DeleteUserAsync()`. |
| Errors | `GraspANALYTICS.Core/Application/Users/UserWriteErrors.cs` + `UserWriteErrorCode.cs` + `UserWriteResult.cs` | The messages and codes every write returns. |
| Reset email use case | `GraspANALYTICS.Core/Application/UseCases/AdminSendPasswordResetEmailUseCase.cs` | `ExecuteAsync()`. |
| Bulk email use case | `GraspANALYTICS.Core/Application/UseCases/AdminSendUserEmailsUseCase.cs` | `ExecuteAsync()`. |
| Password tokens | `GraspANALYTICS.Core/Application/UsersPassword/UserPasswordService.cs` | `ApplyChangePasswordTokenAsync()`. |
| Template email sender | `GraspANALYTICS.Core/Application/Users/UserNotificationSender.cs` | `SendAsync()`: loads the template, fills the tokens, sends. |
| Template tokens | `GraspANALYTICS.Core/Application/Notifications/NotificationParserService.cs` + `GraspANALYTICS.Domain/Models/Notifications/NotificationTemplateTokens.cs` | `Parse()`. |
| Email links | `GraspANALYTICS.Core/Infrastructure/Resolvers/HttpContextUrlResolver.cs` | `GetChangePasswordUrl()`, `GetLoginUrl()`, `GetNewUserUrl()`. |
| SMTP | `GraspANALYTICS.Core/Application/Services/MailKitEmailSender.cs` → `GraspANALYTICS.Core/Application/Resolvers/SmtpSettingsResolver.cs` | `SendAsync()`; `Get()` reads the agency SMTP settings. |
| Email images | `GraspANALYTICS.Core/Infrastructure/Resolvers/WebHostEmailImageDirectoryResolver.cs` | `GetImageDirectory()`: `wwwroot/shared/UserFiles/Images`. |
| Notification defaults | `GraspANALYTICS.Core/Application/Notifications/AdminNotificationService.cs` + `NotificationDefaultSettings.cs` | `GetNotificationsWithDefaultAsync()`. |
| AI usage | `GraspANALYTICS.Core/Application/AiUsage/AiUsageService.cs` | `GetForSelectedAgencyAsync()`, `SetLimitAsync()`, `ResetAsync()`. |
| User data | `GraspANALYTICS.Core/Application/Users/Repos/UserRepository.cs` | `GetAsync()`, `GetByAgencyAsync()`, `GetById()`, `GetUserByLoginAsync()`, `CreateUserAsync()`, `UpdateUserAsync()`, `DeleteUserAsync()`. |

## 1. Who can manage whom

Every Users endpoint carries `[Authorize(Roles = "Super Admin,Agency Admin,Company Admin")]`, `[CustomAuthorizeAttribute]`, and `[RequireUserContext]`. The role ids come from `Enums.Roles` (`GraspANALYTICS.Data/Enumerations`): Agency User 1, Agency Admin 2, Super Admin 3, Company User 4, Company Admin 5. "Company role" means 4 or 5.

| Rule | Where it is enforced |
|---|---|
| Every read and write is limited to `UserContext.AgencyId`; no agency selected → empty list, 404 on detail, `AgencyNotSelected` on writes | every use case; `UserRepository.GetAsync` matches `UserID` and `AgencyID` |
| Non-Super Admins never see, open, update, or delete a Super Admin user | `AdminGetUsersUseCase`, `AdminGetUserUseCase`, `AdminSaveUserUseCase.ValidateExistingUserInScopeAsync`, `AdminDeleteUserUseCase` |
| Nobody can create a Super Admin or move a user to the Super Admin role here | `AdminSaveUserUseCase` (`SuperAdminNotAllowed`, 403) |
| A Company Admin sees and manages only users whose `CorporationID` is their own | same four use cases (`Forbidden`, 403; detail returns 404) |
| A Company Admin can only assign company roles, and only their own corporation | `AdminSaveUserUseCase` (`RoleNotAllowed` and `Forbidden`, 403) |
| A Company Admin cannot pick a corporation; the editor gets `canAssignCorporation: false` and the corporation id defaults to theirs | `AdminGetUserUseCase.BuildUserDetailAsync` |
| The role list offers Company Admin only when it is the user's current role | `AdminGetUserUseCase.BuildAvailableRoles` (the UI list only; the save does not check this) |
| A company role requires a corporation; any other role saves `CorporationID = 0` | `UsersService.SaveUserAsync` (`CorporationRequired`) |
| TMC-only reports are never offered to a Company Admin editor and cannot be saved for a company-role user | `AdminGetUserUseCase.BuildUserDetailAsync`; `UsersService.ResolveAssignableReportsAsync` (`ReportNotAllowed`) |
| AI access settings are Super Admin only | `AdminAiAccessController` (`[Authorize(Roles = "Super Admin")]` on each action) |

`UserContextResolver.GetAsync` builds the `UserContext` from the app session (`SessionService`): `UserId`, `RoleId`, `CorporationId`, and `AgencyId` from the session's current user, plus `DataSecurityCorporationId` re-read from the user row. No session user, or a user that no longer exists → `RequireUserContextAttribute` answers 401. `CustomAuthorizeAttribute` answers 401 (`ExpiredLoginResult`) when the app session is gone and 460 (`DuplicateLoginResult`) when `LastLoginWins` is on and another sign-in took the session.

## 2. Loading the list

```text
Browser click "Users" tab (AdminHome.vue)
  └─ AdminHome.onTabSelect('users')
       └─ AdminUsers.vue mounts UsersList.vue
            └─ UsersList.refresh()
                 └─ api.getUsers()  → GET /api/admin/users
                      failure → handleNullSession(error), toast "Unable to load users."
```

On the server:

```text
AdminUsersController.GetUsersAsync          [Authorize(Roles)], [CustomAuthorizeAttribute], [RequireUserContext], [CaseInsensitiveJson]
  └─ IAdminGetUsersUseCase.ExecuteAsync(userContext)   AdminGetUsersUseCase
       ├─ AgencyId <= 0 → empty list
       ├─ IUserRepository.GetByAgencyAsync(agencyId)
       ├─ caller is not Super Admin → drops Super Admin users
       ├─ caller is a company role → keeps users with the caller's CorporationID
       ├─ IRoleRepository.GetRolesAsync()
       ├─ IUserReportRepository.GetUserReportsExcludingDeletedAsync()   counts reports per user
       └─ AdminUserMapper.ToUserDto(user, roles, numReports)   ordered by first name
  └─ UserContractMapper.ToResponseList(users)  → 200
```

Each row is a `UserResponse`: `userId`, `email`, `firstName`, `lastName`, `isActive`, `login`, `lastLoginDate`, `dateCreated`, `numReports`, `roleName`, `roleId`.

### Search, sort, and paging

All three run in the browser on the full list; the server has no paging for users.

- `UsersList` search box: PrimeVue global filter, "contains", over `firstName`, `lastName`, `login`, `email`, `roleName`.
- Sorting: every column is sortable; the default is first name ascending.
- Paging: 25 rows per page, with 10, 25, or 50 to choose; the footer reads "{first}–{last} of {totalRecords} users".
- An empty agency shows "There are no users for this agency."
- `Last Login` is shown with `format.formatDateTime()` as `MM/DD/YY hh:mm AM`.

Each row has two buttons: the pencil ("Edit user") emits `edit` with the user id, and the envelope ("Send password reset email") opens the reset dialog (section 7).

## 3. Opening the editor

```text
Browser click "New User" (UsersList.vue)
  └─ UsersList emits create
       └─ AdminUsers.openUser(0)
            └─ UserForm.vue mounts with userId 0
                 └─ UserForm.load()
                      ├─ api.getUser(0)                     → GET /api/admin/users/0
                      └─ api.getNotifications('NewUser')    → GET /api/admin/notifications/with-default?context=NewUser
```

```text
Browser click "Edit user" (UsersList.vue)
  └─ UsersList emits edit
       └─ AdminUsers.openUser(userId)
            └─ UserForm.load()
                 ├─ api.getUser(userId)                     → GET /api/admin/users/{userId}
                 └─ api.getNotifications('None')            → GET /api/admin/notifications/with-default?context=None
                      failure of either → toast "Unable to load the user.", editor closes
```

After both calls, `UserForm.apply()` fills the form, `UserForm.applyTmcFilter()` hides TMC-only reports when the selected role is a company role, and the form takes a snapshot so `UserForm.hasUnsavedChanges()` can compare later. For a new user the email template preselects the agency default (`SelectedID`); for an existing user it starts at "Do not send email".

On the server:

```text
AdminUsersController.GetUserAsync           [Authorize(Roles)], [CustomAuthorizeAttribute], [RequireUserContext], [CaseInsensitiveJson]
  └─ IAdminGetUserUseCase.ExecuteAsync(userId, userContext)   AdminGetUserUseCase
       ├─ AgencyId <= 0 → null → 404
       ├─ userId <= 0 → BuildUserDetailAsync with an empty user (IsActive true)
       ├─ IUserRepository.GetAsync(userId, agencyId)
       │     none → 404
       │     caller is a company role and the user is in another corporation → 404
       │     user is Super Admin and the caller is not → 404
       ├─ IUserReportRepository.GetUserReportsExcludingDeletedAsync()   the user's assigned report ids
       ├─ IUserReportRepository.GetDefaultAsync(userId)
       └─ AdminGetUserUseCase.BuildUserDetailAsync
            ├─ IRoleRepository.GetRolesAsync()
            ├─ IAgencyReportsRepository.GetSelectedAgencyReportsAsync(agencyId)
            ├─ AdminGetUserUseCase.BuildAvailableRoles(roles, editorRoleId, currentRoleId)
            └─ AdminGetUserUseCase.AssignableRoles(roles, callerRoleId)
  └─ UserContractMapper.ToResponse(detail)  → 200
```

`UserDetailResponse` fields:

| Field | Value |
|---|---|
| `user` | the `UserResponse` row (empty with `isActive: true` for a new user) |
| `assignedReports` | active agency reports assigned to the user, by name, with `isDefault` on the default one |
| `availableReports` | the other active agency reports; TMC-only ones are left out when the caller is a company role |
| `availableRoles` | role options for the dropdown: never Super Admin; Company Admin only when it is the user's current role; company roles only when the caller is a company role |
| `roles` | every non-Super Admin role (company roles only for a company caller); the editor does not use it |
| `corporationId` | the user's corporation; for a new user, the caller's own when the caller is a company role, otherwise 0 |
| `canAssignCorporation` | false for a company-role caller |
| `isSsoOnly`, `tenantHint` | from the user row |

### Notification templates

```text
AdminNotificationsController.GetNotificationsWithDefaultAsync   [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [CaseInsensitiveJson]
  └─ IAdminNotificationService.GetNotificationsWithDefaultAsync(context)   AdminNotificationService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       ├─ INotificationRepository.GetNotificationsAsync(agencyId)
       ├─ NotificationDefaultSettings.ResolveSettingKey(context)
       └─ ISettingsRepository.GetAsync(settingKey, agencyId)   the default template id
  └─ 200 with active templates by title
```

Each item is `{ "NotificationID", "Title", "SelectedID" }`; `SelectedID` is the same default id on every row. The default comes from the agency setting `DefaultNewUserNotifyID` (`NewUser`), `DefaultChangePassNotifyID` (`ChangePassword`), or `DefaultForgotPassNotifyID` (`ForgotPassword`); `None` returns `"0"`.

### Corporation and custom filters

The **Corporation** field appears only when the selected role is a company role and `canAssignCorporation` is true. `CorporationSelector` lists corporations in batches of 50 as the list scrolls (`GET /api/Corporations/Search?search&skip&take`) and searches 300 ms after typing stops; it loads the selected name with `GET /api/Corporations/GetOption?corporationId=`. `CorporationsController` is limited to `Super Admin,Agency Admin`. Switching to a non-company role sets the corporation back to 0.

Every change of the corporation id reloads the custom filters:

```text
UserForm watches form.corporationId
  ├─ corporationId <= 0 → no filters, Custom Filters tab hidden
  └─ api.getSortFilters(userId, corporationId)   → GET /api/admin/users/{userId}/sort-filters?corporationId=
```

On the server:

```text
AdminUsersController.GetUserSortFiltersAsync   [Authorize(Roles)], [CustomAuthorizeAttribute], [RequireUserContext], [CaseInsensitiveJson]
  └─ IAdminGetUserSortFiltersUseCase.ExecuteAsync(userId, corporationId, userContext)   AdminGetUserSortFiltersUseCase
       ├─ caller is a company role → uses the caller's corporation instead of the query value
       ├─ no agency, no corporation, or feature flag Feature_SortFilters_Admin off → empty list
       ├─ userId > 0 and IUserRepository.GetAsync(userId, agencyId) finds nobody → empty list
       └─ IGetCorporationSortsUseCase.ExecuteAsync(request)   GetCorporationSortsUseCase
            ├─ IAgenciesService.GetAgencyDatabaseResourceAsync(agencyId)
            ├─ ICorporationsRepository.GetCorporationAsync(agencyDb, corpId)
            └─ ISortFilterRepository.GetSortFilterAsync(userId, corpId)
  └─ 200 with { id, label, value } items
```

- `GetCorporationSortsUseCase`: every non-blank `*Label` property on the corporation becomes one item. The id is the property name without `Label` (for example `Sort1`), the label is the corporation's text, and the value is the user's saved `Sort1`...`Sort10`. Any exception is logged and returns what was collected so far.
- The **Custom Filters** tab shows one text box per item and appears only when there is at least one item.

## 4. Saving

```text
Browser click "Save" (UserForm.vue)
  └─ UserForm.save()
       ├─ UserForm.validationError()
       │     message → inline error and toast, nothing sent
       ├─ UserForm.buildPayload()
       ├─ new user → api.createUser(payload)            → POST /api/admin/users
       └─ existing → api.updateUser(userId, payload)    → PUT /api/admin/users/{userId}
            success → editor closes, list reloads on mount
            failure → handleNullSession(error), inline error and toast
```

`UserForm.validationError()` checks, in order:

| Check | Message |
|---|---|
| first name blank | "First name is required." |
| first name over 50 | "First name cannot exceed 50 characters." |
| email not 3-50 characters or no `x@y` shape | "A valid email address (3-50 characters) is required." |
| login not 3-50 characters | "Login must be between 3 and 50 characters." |
| no role | "A role is required." |
| company role and no corporation | "You must select a corporation." |

`UserForm.buildPayload()` trims the text fields, sends `lastName` and `tenantHint` as null when blank, sends `corporationId` 0 for a non-company role, and sends `sortFilters` as `{ "Sort1": "...", ... }` only when the Custom Filters tab has items. The request carries the `RequestVerificationToken` header from `antiforgery.antiforgeryToken()`.

```json
{
  "roleId": 4, "isActive": true,
  "firstName": "Ana", "lastName": "Ruiz",
  "email": "ana@example.com", "login": "aruiz",
  "corporationId": 1234,
  "selectedReportIds": [11, 12], "defaultReportId": 11,
  "notificationId": 7,
  "sortFilters": { "Sort1": "East" },
  "isSsoOnly": false, "tenantHint": null
}
```

On the server:

```text
AdminUsersController.CreateUserAsync / UpdateUserAsync   [ValidateAntiForgeryToken], [ApiController] model validation, [RequireUserContext]
  ├─ UserWriteRequest validation fails → 400 validation problem
  ├─ UserContractMapper.ToSaveRequest(request, userId)   create uses userId 0
  ├─ IAdminSaveUserUseCase.ExecuteAsync(saveRequest, userContext)   (section 4a)
  ├─ failure → AdminUsersController.ToErrorResult(errorCode, result)   409, 403, 404, or 400
  └─ success → create: 201 { success, errorMessage, userId }; update: 200
```

`UserWriteRequest` validation: `roleId` `[Range(1, int.MaxValue)]`; `firstName` `[Required]`, 1-50; `email` `[Required]`, `[EmailAddress]`, 3-50; `login` `[Required]`, 3-50. `ToSaveRequest` turns missing optional fields into `false`, `0`, `""`, or an empty list. The editor shows the first message from `errors`.

### 4a. `AdminSaveUserUseCase.ExecuteAsync`

```text
AdminSaveUserUseCase.ExecuteAsync(request, userContext)
  ├─ AgencyId <= 0 → AgencyNotSelected
  ├─ request role is Super Admin → SuperAdminNotAllowed (Forbidden)
  ├─ caller is a company role, requested role is not → RoleNotAllowed (Forbidden)
  ├─ caller is a company role, CorporationId is not the caller's → Forbidden
  ├─ AdminSaveUserUseCase.ValidateExistingUserInScopeAsync   update only
  │     IUsersService.GetUserAsync(userId, agencyId)   none → UserNotFound (NotFound)
  │     other corporation for a company caller, or a Super Admin target → Forbidden
  ├─ IUsersService.SaveUserAsync(agencyId, request)   UsersService (section 4b)
  │     failure → logged (10001) → that error, or SaveFailed when it has no code
  ├─ NotificationId > 0 → AdminSaveUserUseCase.SendNotificationAsync
  │     IUserNotificationSender.SendAsync(userId, notificationId, agencyId)   (section 8)
  │     failure → logged (10003); the save still succeeds
  └─ UserWriteResult.Saved(userId)
  any exception → logged (10002) → SaveFailed
```

### 4b. `UsersService.SaveUserAsync`

```text
UsersService.SaveUserAsync(agencyId, request)
  ├─ company role with CorporationId 0 → CorporationRequired (9001)
  ├─ UsersService.IsLoginInUseAsync(login, userId)
  │     IUserRepository.GetUserByLoginAsync(login)   any agency
  │     another user owns it → DuplicateLogin (9002)
  ├─ UsersService.ResolveAssignableReportsAsync(agencyId, request)
  │     IAgencyReportsRepository.GetSelectedAgencyReportsAsync(agencyId)
  │     not assignable → ReportNotAllowed (9003)
  ├─ IUserRepository.GetAsync(userId, agencyId)   decides create or update
  ├─ UserInputSanitizer.ThrowExceptionOnScriptAttacks(request)
  ├─ UsersService.ApplyRequest(user, request, corporationId)
  ├─ new → IUserRepository.CreateUserAsync(user)
  │  existing → IUserRepository.UpdateUserAsync(user)
  ├─ UsersService.ReplaceSortFiltersAsync(userId, corporationId, sortFilters)
  │     ISortFilterRepository.GetSortFilterAsync(userId, corporationId)
  │     ISortFilterRepository.UpsertSortFilterAsync(sortFilter)
  └─ UsersService.ReplaceUserReportsAsync(userId, request, assignableReports)
        IUserReportRepository.DeleteUserReportsAsync(userId)
        IUserReportRepository.CreateUserReportAsync(userReport)   once per selected report
  persistence exception → logged (9004) → SaveFailed
```

What gets written:

- `UsersService.ApplyRequest`: first and last name, email, login, role, active flag, `CorporationID` and `DataSecurityCorporationID` (the corporation for a company role, otherwise 0), `IsSsoOnly`, `TenantHint`, and `ModifiedCreatedBy` (the signed-in user's id from the session, or -1). A corporation above 0 also sets `DataSecurityExpression`.
- `UserInputSanitizer`: any string field on the request that contains an HTML tag throws. The exception reaches `AdminSaveUserUseCase` and the caller sees "Unable to save the user." The sort filter values are a nested object and are not checked.
- Updates set `ModifiedDate`; creates set `DateCreated` and `AgencyID`.
- Sort filters are written only when the request carries `sortFilters`; they are keyed by user and corporation.
- Reports: every existing assignment is removed and the selected ones are created again, each with its `PowerBIReportID` and `IsDefault` when it is the default report. Assignments to inactive reports are never shown in the editor, so a save removes them.

### Report rules

`UsersService.ResolveAssignableReportsAsync` returns `ReportNotAllowed` when:

- no reports are selected but a default report is set
- a selected report is not an active report of the agency
- the role is a company role and a selected report is TMC-only
- the default report is not one of the selected reports

The editor keeps these from happening: the default-report list contains only the user's reports, removing the default report from the user's list resets the default to "No default report", and TMC-only reports move out of sight when a company role is picked.

### Password handling

Admins never see or set a password. A **new** user is created with an empty password, `ForcePasswordReset = true`, a new GUID `PasswordToken`, and `TokenExpire` 24 hours (1440 minutes) ahead. The user sets a password through the link in the welcome email (`{NewUserURL}` → `/Welcome`). An **update** does not touch the password fields. The reset email (section 7) issues a new token.

## 5. Leaving the editor with unsaved changes

```text
Browser click another Admin tab (AdminHome.vue)
  └─ AdminHome.onTabSelect(tab)
       └─ globalThis.adminUsersGuardSwitch()   AdminUsers.guardSwitch
            ├─ UserForm.hasUnsavedChanges() is false → leave
            └─ "Unsaved changes" dialog
                 ├─ "Cancel"  → AdminUsers.settleGuard(false), stay
                 ├─ "Discard" → AdminUsers.discardAndLeave()
                 └─ "Save"    → AdminUsers.saveAndLeave()
                                  └─ UserForm.save()   leaves only when the save succeeds
```

`UserForm.hasUnsavedChanges()` compares the current payload (without custom filters) to the snapshot taken after loading, and the custom filter values to the last loaded set. The **Back** and **Cancel** buttons close the editor without asking.

## 6. Deleting

```text
Browser click "Delete User" (UserForm.vue)   existing users only
  └─ "Delete User" confirmation dialog
       └─ Browser click "Delete"
            └─ UserForm.doDelete()
                 └─ api.deleteUser(userId)   → DELETE /api/admin/users/{userId}
                      success → editor closes
                      failure → inline error and toast
```

On the server:

```text
AdminUsersController.DeleteUserAsync        [ValidateAntiForgeryToken], [RequireUserContext]
  └─ IAdminDeleteUserUseCase.ExecuteAsync(userId, userContext)   AdminDeleteUserUseCase
       ├─ userId <= 0 → UserNotFound (NotFound)
       ├─ AgencyId <= 0 → AgencyNotSelected
       ├─ IUsersService.GetUserAsync(userId, agencyId)   none → UserNotFound (NotFound)
       ├─ company caller and another corporation, or a Super Admin target → Forbidden
       └─ IUsersService.DeleteUserAsync(userId)   UsersService
            ├─ IUserRepository.GetById(userId)   none → logged (9006), nothing deleted
            ├─ IUserRepository.DeleteUserAsync(userId)
            └─ IUserReportRepository.DeleteUserReportsAsync(userId)
       exception → logged (13001) → DeleteFailed
  └─ 204, or the error status (section 9)
```

The delete removes the user row and the report assignments. It does not remove the user's sort filters or AI usage limit rows.

## 7. Sending a password reset email

```text
Browser click "Send password reset email" (UsersList.vue)
  └─ UsersList.openResetDialog(user)
       └─ SendPasswordResetDialog.load()
            └─ api.getNotifications('ChangePassword')   → GET /api/admin/notifications/with-default?context=ChangePassword
                 failure → "Unable to load notifications."
```

```text
Browser click "Send" (SendPasswordResetDialog.vue)
  └─ SendPasswordResetDialog.send()
       ├─ no template picked → "You must select a notification."
       └─ api.sendPasswordResetEmail(userId, notificationId)   → POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail
            success → dialog closes, toast "Password reset email sent to <email>."
            failure → errorMessage from the body, or "Unable to send the password reset email."
```

The dialog preselects the agency's `DefaultChangePassNotifyID` template.

On the server:

```text
AdminUserEmailController.SendPasswordResetEmailAsync   [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [RequireUserContext], [ValidateAntiForgeryToken]
  ├─ notificationId < 1 → 400 validation problem   ([Range(1, int.MaxValue)])
  └─ IAdminSendPasswordResetEmailUseCase.ExecuteAsync(userId, notificationId, userContext)   AdminSendPasswordResetEmailUseCase
       ├─ AgencyId <= 0 → AgencyNotSelected
       ├─ notificationId <= 0 → NotificationRequired
       ├─ IUserPasswordService.ApplyChangePasswordTokenAsync(userId, userContext)   UserPasswordService
       │     IUserRepository.GetAsync(userId, agencyId)   none → logged (12002) → UserNotFound (NotFound)
       │     UserInputSanitizer.ThrowExceptionOnScriptAttacks(user)
       │     ISettingsRepository.GetAsync("ChangePasswordLinkTimeoutMinutes", agencyId)   default 1440
       │     IUserRepository.UpdateUserAsync(user)   new PasswordToken, TokenExpire, ForcePasswordReset = true
       ├─ IUserNotificationSender.SendAsync(userId, notificationId, agencyId)   (section 8)
       │     failure → logged (15001) → EmailSendFailed
       └─ 200 { success: true }
       exception → logged (15002) → EmailSendFailed
```

The use case checks the agency only. It does not apply the Company Admin corporation rule or the Super Admin target rule that the user endpoints apply.

## 8. How an email is built and sent

`UserNotificationSender.SendAsync` serves the welcome email (section 4a) and the reset email (section 7):

```text
UserNotificationSender.SendAsync(userId, notificationId, agencyId)
  ├─ IUsersService.GetUserAsync(userId, agencyId)
  ├─ INotificationRepository.GetNotificationAsync(notificationId, agencyId)
  │     none → logged (17001) → fail
  ├─ INotificationParserService.Parse(message, subject, recipient)   NotificationParserService
  ├─ IEmailImageDirectoryResolver.GetImageDirectory()   WebHostEmailImageDirectoryResolver
  └─ IEmailSender.SendAsync(email, subject, body, imageDirectory, agencyId)   MailKitEmailSender
        failure → logged (17002) → fail
  exception → logged (17003) → fail; cancellation is rethrown
```

- The template's `Message` and `Subject` are HTML-decoded before the tokens are filled.
- The recipient is the user's first name, last name, login, email, and current `PasswordToken`.

`NotificationParserService.Parse` replaces these tokens in the subject and body:

| Token | Becomes |
|---|---|
| `{FirstName}`, `{LastName}`, `{Login}`, `{Email}` | the user's values |
| `{Today}`, `{TodayDate}`, `{TodayTime}` | server time as `MM/dd/yyyy hh:mm tt`, `MM/dd/yyyy`, `hh:mm tt` |
| `{ChgPassURL}` | `HttpContextUrlResolver.GetChangePasswordUrl`: `<scheme>://<host>/ChangePassword?t=<token>&u=<login, HTML-encoded>` |
| `{NewUserURL}` | `HttpContextUrlResolver.GetNewUserUrl`: `<scheme>://<host>/Welcome?t=<token>&u=<login, URL-encoded>` |
| `{LoginURL}` | `<scheme>://<host>/Login` |

The scheme and host come from the current request; the path base is not added.

`MailKitEmailSender.SendAsync`:

1. `ISmtpSettingsResolver.Get(agencyId)` (`SmtpSettingsResolver`) reads the agency settings through `ISessionService.GetSettingValue`: `SMTPPassword` (required, decrypted with `IEncryptionService<string>`; a failed decrypt is logged), `SMTPHost` (default `grasp.smtp.com`), `SMTPPort` (default 587), `SMTPUser` and `SMTPSender` (default `noreply@grasptech.com`).
2. Builds the message from `SMTPSender` to the user's address. Every `<img src>` whose file name exists in `wwwroot/shared/UserFiles/Images` is attached and pointed at by `cid:`.
3. Connects with STARTTLS, signs in with `SMTPUser`, sends, disconnects.
4. Any exception except cancellation is logged and returns a failed `EmailSendResult`.

## 9. Write errors and statuses

All user writes return `UserWriteResponse` `{ "success", "errorMessage", "userId" }` (`userId` only on a successful save). Messages are from `UserWriteErrors`.

| Error code | Message | Users endpoints | Reset email endpoint |
|---|---|---|---|
| `AgencyNotSelected` | "No agency is currently selected." | 400 | 400 |
| `CorporationRequired` | "You must select a corporation." | 400 | - |
| `ReportNotAllowed` | "One or more selected reports are not available for this user." | 400 | - |
| `DuplicateLogin` | "The Login you entered is already in use." | 409 | - |
| `NotFound` | "User not found." | 404 | 404 |
| `Forbidden` | "You are not allowed to manage this user." | 403 | - |
| `Forbidden` (Super Admin role requested) | "Super Admin users cannot be created or modified here." | 403 | - |
| `Forbidden` (non-company role from a company caller) | "You are not allowed to assign this role." | 403 | - |
| `SaveFailed` | "Unable to save the user." | 400 | - |
| `DeleteFailed` | "Unable to delete the user." | 400 | - |
| `NotificationRequired` | "No notification was selected." | - | 400 |
| `EmailSendFailed` | "Unable to send the email." | - | 400 |

Other statuses: 400 validation problem from `[ApiController]`; 400 from a missing or wrong antiforgery token; 401 from `RequireUserContextAttribute` or `CustomAuthorizeAttribute` (expired session); 460 from `CustomAuthorizeAttribute` (duplicate sign-in); and a signed-in user in another role is sent to `/Forbidden` by the cookie scheme (`AccessDeniedPath` in `GraspANALYTICS/CompositionRoot/GraspAuthenticationExtensions.cs`). The client hands every failure to `handleNullSession()`, which lets the Admin shell's `HandleNullSession` handle session statuses.

## 10. Bulk email (used by the Notify tab)

`POST /api/AdminUserEmail/SendEmails` lives on the same controller. The Notify tab calls it (`GraspANALYTICS/ClientApp/src/admin/notify/api.ts` → `sendEmails()`), and it also loads its recipient list from `GET /api/admin/users`; the Users tab does not call it.

```text
AdminUserEmailController.SendEmailsAsync    [ValidateAntiForgeryToken], [RequireUserContext]
  ├─ SendUserEmailsRequest validation fails → 400   (subject and message required, at least one user id)
  ├─ UserContractMapper.ToCommand(request)
  └─ IAdminSendUserEmailsUseCase.ExecuteAsync(command, userContext)   AdminSendUserEmailsUseCase
       ├─ AgencyId <= 0 → 400 { errorMessage: "No agency is currently selected." }
       ├─ IEmailImageDirectoryResolver.GetImageDirectory()
       └─ for each user id
            ├─ IUsersService.GetUserAsync(userId, agencyId)   none → logged (16001), "user #<id>" failed
            ├─ message contains {ChgPassURL} or {NewUserURL}
            │     → IUserPasswordService.ApplyChangePasswordTokenAsync(userId, userContext)
            ├─ INotificationParserService.Parse(message, subject, recipient)
            └─ IEmailSender.SendAsync(...)   failure → logged (16002); exception → logged (16003)
  └─ 200 { success, sentCount, totalCount, failedRecipients }
```

`success` is true only when every email went out. Like the reset email, this use case checks the agency only, not the corporation or Super Admin rules.

## 11. AI access endpoints (Super Admin only)

`AdminAiAccessController` carries `[Authorize]` and `[CustomAuthorizeAttribute]` on the class and `[Authorize(Roles = "Super Admin")]` on each action. The Admin Users tab does not call it; the Super Admin user editor and agency users page do, through `GraspANALYTICS/ClientApp/src/shared/aiAccess.ts` (`getAiUsageLimits()`, `setAiUsageLimit()`, `resetAiUsage()`).

```text
AdminAiAccessController.GetForSelectedAgencyAsync
  └─ IAiUsageService.GetForSelectedAgencyAsync()   AiUsageService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()   none → { agencyId: 0, limits: [] }
       └─ IAiUsageLimitRepository.GetByAgencyAsync(agencyId)
  └─ 200 { agencyId, limits: [{ userId, agencyId, tokenLimit, tokensUsed, accessEnabled, daxQueriesEnabled }] }
```

```text
AdminAiAccessController.SetAsync            [ValidateAntiForgeryToken]
  └─ IAiUsageService.SetLimitAsync(userId, agencyId, tokenLimit, accessEnabled, daxQueriesEnabled)
       ├─ IAiUsageLimitRepository.GetAsync(userId, agencyId)
       │     none → AiUsageService.EnsureAgencyExistsAsync, AiUsageService.EnsureUserExistsAsync, new row
       ├─ no tokenLimit → setting AiDefaultTokenLimit, or 100,000
       └─ IAiUsageLimitRepository.UpsertAsync(limit)
  ├─ AiUsageAgencyNotFoundException → 404 "Agency <id> was not found."
  ├─ AiUsageUserNotFoundException → 404 "User <id> was not found."
  └─ 200 { consumed, tokenLimit, remaining, allowed, accessEnabled, message }
```

```text
AdminAiAccessController.ResetAsync          [ValidateAntiForgeryToken]
  └─ IAiUsageService.ResetAsync(userId, agencyId)
       ├─ AiUsageService.EnsureAgencyExistsAsync, AiUsageService.EnsureUserExistsAsync
       ├─ no row → access-disabled status with the AiAccessNotEnabledMessage setting
       └─ tokens used set to 0, IAiUsageLimitRepository.UpsertAsync(limit)
  └─ 200 status, or the same 404s
```

- `SetAiUsageLimitRequest`: `UserId` and `AgencyId` `[Range(1, int.MaxValue)]`, `TokenLimit` optional `[Range(1, int.MaxValue)]`, `AccessEnabled` and `DaxQueriesEnabled` required.
- The status `message` is empty when allowed, the `AiAccessNotEnabledMessage` setting when access is off, and `AiUsageDepletedMessage` plus `AiFairUseStatement` when the tokens are used up.
- Usage resets on its own on the first `IAiUsageService.GetAsync` call in a new month.

## 12. Endpoints

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/admin/users` | `AdminUsersController.GetUsersAsync → AdminGetUsersUseCase.ExecuteAsync` | none | `UserResponse[]` | 200; 302; 401; 460 |
| `GET /api/admin/users/{userId}` | `AdminUsersController.GetUserAsync → AdminGetUserUseCase.ExecuteAsync` | `userId` (0 for a new user) | `UserDetailResponse` | 200; 302; 401; 404; 460 |
| `GET /api/admin/users/{userId}/sort-filters` | `AdminUsersController.GetUserSortFiltersAsync → AdminGetUserSortFiltersUseCase.ExecuteAsync` | `userId`, `corporationId` | `[{ id, label, value }]` | 200; 302; 401; 460 |
| `POST /api/admin/users` | `AdminUsersController.CreateUserAsync → AdminSaveUserUseCase.ExecuteAsync` | `UserWriteRequest` | `UserWriteResponse` with `userId` | 201; 302; 400; 401; 403; 404; 409; 460 |
| `PUT /api/admin/users/{userId}` | `AdminUsersController.UpdateUserAsync → AdminSaveUserUseCase.ExecuteAsync` | `userId`, `UserWriteRequest` | `UserWriteResponse` | 200; 302; 400; 401; 403; 404; 409; 460 |
| `DELETE /api/admin/users/{userId}` | `AdminUsersController.DeleteUserAsync → AdminDeleteUserUseCase.ExecuteAsync` | `userId` | no body | 204; 302; 400; 401; 403; 404; 460 |
| `GET /api/admin/notifications/with-default` | `AdminNotificationsController.GetNotificationsWithDefaultAsync → AdminNotificationService.GetNotificationsWithDefaultAsync` | `context` (`None`, `NewUser`, `ChangePassword`, `ForgotPassword`) | `[{ NotificationID, Title, SelectedID }]` | 200; 302; 401; 460 |
| `POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail` | `AdminUserEmailController.SendPasswordResetEmailAsync → AdminSendPasswordResetEmailUseCase.ExecuteAsync` | `userId`, `notificationId` | `UserWriteResponse` | 200; 302; 400; 401; 403; 404; 460 |
| `POST /api/AdminUserEmail/SendEmails` | `AdminUserEmailController.SendEmailsAsync → AdminSendUserEmailsUseCase.ExecuteAsync` | `subject`, `message`, `userIds` | `BulkEmailResponse` | 200; 302; 400; 401; 460 |
| `GET /api/admin/ai-access` | `AdminAiAccessController.GetForSelectedAgencyAsync → AiUsageService.GetForSelectedAgencyAsync` | none | `AiUsageLimitsResponse` | 200; 302; 401; 460 |
| `PUT /api/admin/ai-access` | `AdminAiAccessController.SetAsync → AiUsageService.SetLimitAsync` | `userId`, `agencyId`, `tokenLimit`, `accessEnabled`, `daxQueriesEnabled` | `AiUsageStatusResponse` | 200; 302; 400; 401; 404; 460 |
| `POST /api/admin/ai-access/reset` | `AdminAiAccessController.ResetAsync → AiUsageService.ResetAsync` | `userId`, `agencyId` | `AiUsageStatusResponse` | 200; 302; 400; 401; 404; 460 |
| `GET /api/Corporations/Search` | `CorporationsController.SearchAsync` | `search`, `skip`, `take` | corporation options | 200; 302; 401; 460 |
| `GET /api/Corporations/GetOption` | `CorporationsController.GetOptionAsync` | `corporationId` | one corporation option | 200; 302; 401; 460 |
| SMTP `<SMTPHost>:<SMTPPort>` | `MailKitEmailSender.SendAsync` | STARTTLS, `SMTPUser` sign-in, the message | send result | success or a logged failure |

## 13. What gets logged

Application log only; none of these flows writes an `AuditLog` row. See `Logging-EventId-Registry.md`.

| EventId | Source | Level | When |
|---|---|---|---|
| 9001 | `UsersService` | Warning | company role saved without a corporation |
| 9002 | `UsersService` | Warning | login already in use |
| 9003 | `UsersService` | Warning | a selected or default report is not assignable |
| 9004 | `UsersService` | Error | user, sort filter, or report write threw |
| 9005 | `UsersService` | Error | `GetUserAsync` found no user in the agency |
| 9006 | `UsersService` | Error | delete found no user |
| 10001 | `AdminSaveUserUseCase` | Warning | save rejected, with the error code |
| 10002 | `AdminSaveUserUseCase` | Error | save threw |
| 10003 | `AdminSaveUserUseCase` | Warning | the welcome or template email after a save failed |
| 12002 | `UserPasswordService` | Warning | change-password token asked for an unknown user |
| 13001 | `AdminDeleteUserUseCase` | Error | delete threw |
| 15001 | `AdminSendPasswordResetEmailUseCase` | Warning | reset email not sent |
| 15002 | `AdminSendPasswordResetEmailUseCase` | Error | reset email threw |
| 16001 | `AdminSendUserEmailsUseCase` | Warning | bulk recipient not found |
| 16002 | `AdminSendUserEmailsUseCase` | Warning | bulk email not sent |
| 16003 | `AdminSendUserEmailsUseCase` | Error | bulk email threw |
| 17001 | `UserNotificationSender` | Error | template not found for the agency |
| 17002 | `UserNotificationSender` | Error | SMTP send failed |
| 17003 | `UserNotificationSender` | Error | send threw |

`MailKitEmailSender`, `SmtpSettingsResolver`, and `GetCorporationSortsUseCase` log errors without an EventId. The save, delete, and email use cases include `UserContext.CorrelationId` (the request's `TraceIdentifier`) in their messages.
