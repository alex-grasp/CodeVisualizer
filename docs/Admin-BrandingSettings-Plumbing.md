---
summary: How the Admin Branding and Settings tabs work end to end - the Vue islands, the branding and settings APIs, the Core services, the ganBranding and ganSetting rows they write, logo validation, and how saved values reach the AppSession and the running app; read before debugging or changing either tab.
area: architecture
---

# Admin Branding and Settings plumbing

This doc follows an agency admin through the Branding tab and the Settings tab of `/Admin`: what loads, what is validated where, what gets written, and when the running app sees the change. The Admin shell, the tab strip, the session check on tab switch, and the unsaved-changes guard wiring are covered in `see Admin-Plumbing.md`.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Branding tab | `ClientApp/src/admin/branding/AdminBranding.vue` | The form. `load()`, `onLogoSelected()`, `save()`, `deleteLogo()`, `guardSwitch()`, `hasUnsavedChanges()`. |
| Branding API client | `ClientApp/src/admin/branding/api.ts` | `getBranding()`, `saveBranding()`, `uploadBrandingLogo()`, `deleteBrandingLogo()`. |
| Header branding event | `ClientApp/src/admin/branding/headerBranding.ts` | `publishHeaderBranding()` dispatches `admin:header-branding-changed`. |
| Branding types | `ClientApp/src/admin/branding/types.ts` | `BrandingDto`, `BrandingWriteResult`. |
| Admin header | `ClientApp/src/admin/home/AdminShell.vue` | `onHeaderBrandingChanged()` applies the event to the header title, logo, and colors. |
| Settings tab | `ClientApp/src/admin/settings/AdminSettings.vue` | The form. `load()`, `validate()`, `save()`, `guardSwitch()`, `hasUnsavedChanges()`. |
| Settings API client | `ClientApp/src/admin/settings/api.ts` | `getAdminSettings()`, `getNotificationOptions()`, `saveAdminSettings()`. |
| Settings types | `ClientApp/src/admin/settings/types.ts` | `AdminSettings`, `NotificationOption`, `SaveAdminSettingsResponse`. |
| Shared fetch helpers | `ClientApp/src/shared/http.ts` + `ClientApp/src/shared/antiforgery.ts` | `requestJson()`, `sendJson()`, `errorMessage()`, `antiforgeryToken()`. |
| Branding API | `Controllers/Api/Admin/AdminBrandingController.cs` | `GET /api/admin/branding`, `PUT /api/admin/branding`, `PUT /api/admin/branding/logo`, `DELETE /api/admin/branding/logo`. |
| Branding contracts | `Api/Branding/Contracts/*` + `Models/UploadBrandingLogoRequest.cs` | `SaveBrandingRequest`, `BrandingResponse`, `BrandingWriteResponse`, `UploadBrandingLogoRequest`; `BrandingContractMapper.ToResponse()`. |
| Branding service | `GraspANALYTICS.Core/Application/Admin/Branding/AdminBrandingService.cs` | `GetAsync`, `SaveAsync`, `SaveLogoAsync`, `DeleteLogoAsync`; logo validation. |
| Branding domain models | `GraspANALYTICS.Domain/Models/Branding/*` + `GraspANALYTICS.Domain/Services/IAdminBrandingService.cs` | `BrandingDto`, `SaveBrandingCommand`, `BrandingLogoUpload`, `BrandingWriteResult`, `BrandingWriteErrorCode`, `BrandingWriteErrors`. |
| Branding repository | `GraspANALYTICS.Core/Application/Branding/Repos/BrandingRepository.cs` | `GetByAgencyAsync`, `CreateAsync`, `UpdateAsync` on `ganBranding`. |
| Settings API | `Controllers/Api/Admin/AdminSettingsController.cs` | `GET /api/admin/settings`, `PUT /api/admin/settings`. |
| Settings contracts | `Api/Settings/Contracts/*` | `AdminSettingsResponse`, `SaveAdminSettingsRequest`, `SaveAdminSettingsResponse`. |
| Settings service | `GraspANALYTICS.Core/Application/Admin/Settings/AdminSettingsService.cs` | `GetAsync`, `SaveAsync`; the five setting keys. |
| Settings models | `GraspANALYTICS.Core/Application/Admin/Settings/*` | `IAdminSettingsService`, `AdminSettingsSnapshot`, `SaveAdminSettingsCommand`, `AdminSettingsSaveResult`, `AdminSettingsSaveErrorCode`, `AdminSettingsErrors`. |
| Settings repository | `GraspANALYTICS.Core/Application/Settings/Repos/SettingsRepository.cs` | `GetAsync` (agency then global), `GetByAgencyAsync`, `CreateAsync`, `UpdateAsync` on `ganSetting`. |
| Notification list API | `Controllers/Api/AdminNotificationsController.cs` → `GraspANALYTICS.Core/Application/Notifications/AdminNotificationService.cs` | `GET /api/admin/notifications` fills the Settings drop-downs; `GetNotificationsAsync` is also the access check on save. |
| Selected agency | `GraspANALYTICS.Core/Application/Agencies/AgenciesService.cs` | `GetSelectedAgencyIdAsync` decides which agency every read and write targets. |
| Session refresh | `Services/SessionService.cs` | `ReloadSession()` rebuilds the caller's branding and admin values in the in-memory `AppSession`. |
| Session check | `Classes/CustomAuthorizeAttribute.cs` | `OnAuthorization` refuses a request whose `AppSession` is gone (401) or that lost a last-login-wins race (460). |
| Script check | `GraspANALYTICS.Data/Models/UserInputSanitizer.cs` | `ContainsScriptAttack()` on the branding row and on each setting row before they are written. |

## 1. Shared rules for both tabs

- Both controllers carry `[ApiController]`, `[Authorize(Roles = "Agency Admin,Super Admin,Company Admin")]`, and `[CustomAuthorizeAttribute]`. `CustomAuthorizeAttribute.OnAuthorization` answers 401 (`ExpiredLoginResult`) when the `AppSession` is missing or has no user name, and 460 (`DuplicateLoginResult`) when `LastLoginWins` is `"true"` and the session id does not match.
- Every write action carries `[ValidateAntiForgeryToken]`. The client sends the token from the hidden `__RequestVerificationToken` field that `Views/Admin/Index.cshtml` writes with `@Html.AntiForgeryToken()`, in the `RequestVerificationToken` header (`antiforgery.antiforgeryToken()`, used by `http.sendJson()` and by `api.uploadBrandingLogo()`).
- The target agency is always `IAgenciesService.GetSelectedAgencyIdAsync()`, never a value from the request:
  - `AppSession.CurrentUser.AgencyID` when it is greater than 0 (every agency or company user, and a Super Admin after picking an agency).
  - Otherwise the process-wide memory-cache entry `SuperAdminAgencyID`, else 0.
  - 0 or less means no agency: reads return 400 with an `ApiResponse` body (status `"error"` and the message), writes return 400 with the `AgencyNotSelected` message.
- `[ApiController]` model validation runs before each action, so a request that fails a `[Required]`, `[RegularExpression]`, `[StringLength]`, `[EmailAddress]`, or `[Range]` attribute gets a 400 validation problem and the service never runs.
- Neither controller nor either service writes a log entry; there are no EventIds on these paths. `SessionService.ReloadSession()` writes Debug and Error lines through `IAnalyticsLogger` without EventIds.
- Both tabs register `adminBrandingHasUnsavedChanges`/`adminBrandingGuardSwitch` and `adminSettingsHasUnsavedChanges`/`adminSettingsGuardSwitch` on `globalThis` in `onMounted` and remove them in `onUnmounted`. `AdminHome.onTabSelect()` calls them before leaving the tab; the dialog offers Cancel, Discard (restores the last loaded or saved values), and Save.
- A failed call goes through `dialogs.handleNullSession()` first, which handles a lost session (`see Admin-Plumbing.md`).

## Part A - Branding

## 2. Loading the Branding tab

```text
Browser opens the Branding tab (AdminBranding.vue mounts)
  └─ AdminBranding.load()
       ├─ api.getBranding()   → GET /api/admin/branding
       ├─ AdminBranding.normalizeHexColor()   for headerTextColor and headerBackgroundColor
       ├─ AdminBranding.snapshot()   becomes the unsaved-changes baseline
       └─ headerBranding.publishHeaderBranding()   logo only
            └─ AdminShell.onHeaderBrandingChanged()
```

On the server:

```text
AdminBrandingController.GetAsync          [CaseInsensitiveJson]
  └─ IAdminBrandingService.GetAsync()   AdminBrandingService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → null → 400, body Status "error", Message "No agency is currently selected."
       ├─ IBrandingRepository.GetByAgencyAsync(agencyId)   BrandingRepository
       │     no row → BrandingDto with every field null
       └─ row → BrandingDto, logo bytes as base64, null when empty
  └─ BrandingContractMapper.ToResponse()   → 200 BrandingResponse
```

```json
{ "applicationTitle": "Acme Analytics", "headerTextColor": "#FFFFFF", "headerBackgroundColor": "#000080", "headerLogo": "<base64>" }
```

- `normalizeHexColor` accepts `white`, `black`, `#rrggbb`, `rrggbb`, and three-digit short hex; anything else falls back to `ffffff` (text) or `000000` (background). The `ColorPicker` holds the value without `#`.
- The logo preview is `data:image/jpeg;base64,<headerLogo>` whatever the stored file type.
- On failure the tab shows the dialog "There was an error when getting branding".
- `[CaseInsensitiveJson]` swaps in a `SystemTextJsonOutputFormatter` with default options, so the no-agency `ApiResponse` body is PascalCase (`Status`, `Message`); `BrandingResponse` keeps camelCase through its `[JsonPropertyName]` attributes.

## 3. Selecting a logo

```text
Browser picks a file in "Select file" (AdminBranding.vue)
  └─ AdminBranding.onLogoSelected()
       ├─ type not image/*          → "You can only upload image files."
       ├─ extension not gif/jpg/png → "Only gif, jpg, and png files can be uploaded."
       ├─ size over 3145728 bytes   → "The selected file exceeds the maximum size of 3 MB."
       ├─ AdminBranding.readAsDataUrl()
       ├─ AdminBranding.measureImage()
       │     width or height over 500 → "Image dimensions exceed the allowed limits. ..."
       │     not decodable            → "You can only upload image files."
       └─ keeps the file as the pending logo; nothing is sent yet
```

- The file input has `accept=".gif,.jpg,.png"`. A rejected file clears the pending logo.
- A pending logo counts as an unsaved change and replaces the preview ("New Logo"). The Delete button only shows for a stored logo with nothing pending.
- The 500 x 500 dimension limit is checked only in the browser.

## 4. Saving

```text
Browser click "Save" (AdminBranding.vue)
  └─ AdminBranding.save()
       ├─ title fails ^[a-zA-Z0-9\s_.-]+$ → "Application title can only include letters, numbers, ..."
       ├─ api.saveBranding()   → PUT /api/admin/branding   (section 4a)
       │     success not true → error dialog, stop
       ├─ pending logo: api.uploadBrandingLogo()   → PUT /api/admin/branding/logo   (section 4b)
       │     success not true → error dialog "Error uploading logo", stop
       ├─ AdminBranding.snapshot()   new baseline
       ├─ headerBranding.publishHeaderBranding()   title, both colors, logo
       │     └─ AdminShell.onHeaderBrandingChanged()
       └─ toast "Branding saved."
```

- The title, colors, and logo are two separate requests. If the logo upload fails, the title and colors are already saved.
- The title input has `maxlength="200"`. Colors are sent as `#rrggbb`.
- Error dialogs show `http.errorMessage()`: the response's `errorMessage` or `message`, else the fixed fallback text.

### 4a. Title and colors

```http
PUT /api/admin/branding
RequestVerificationToken: <token>
Content-Type: application/json

{ "applicationTitle": "Acme Analytics", "headerTextColor": "#ffffff", "headerBackgroundColor": "#000080" }
```

On the server:

```text
AdminBrandingController.SaveAsync         [ValidateAntiForgeryToken], [CaseInsensitiveJson], [ApiController] model validation
  └─ IAdminBrandingService.SaveAsync(SaveBrandingCommand)   AdminBrandingService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → AgencyNotSelected → 400
       ├─ IBrandingRepository.GetByAgencyAsync(agencyId)
       │     no row → new ganBranding row for the agency with an empty logo and DateCreated now
       ├─ copies the title and both colors onto the row
       ├─ UserInputSanitizer.ContainsScriptAttack(branding)
       │     true → "Branding contains invalid content." InvalidInput → 400
       ├─ IBrandingRepository.CreateAsync(branding) or IBrandingRepository.UpdateAsync(branding)
       └─ ISessionService.ReloadSession()   (section 6)
  └─ BrandingContractMapper.ToResponse()   → 200 { success: true, errorMessage: null }
```

`SaveBrandingRequest` rules:

| Field | Rules |
|---|---|
| `applicationTitle` | `[Required(AllowEmptyStrings = false)]`, `[StringLength(200)]`, `[RegularExpression(@"^[a-zA-Z0-9\s_.-]+$")]` |
| `headerTextColor` | `[Required(AllowEmptyStrings = false)]`, `[RegularExpression("^#[0-9a-fA-F]{6}$")]` |
| `headerBackgroundColor` | `[Required(AllowEmptyStrings = false)]`, `[RegularExpression("^#[0-9a-fA-F]{6}$")]` |

### 4b. Logo upload

```http
PUT /api/admin/branding/logo
RequestVerificationToken: <token>
Content-Type: multipart/form-data

file=<the image>
```

On the server:

```text
AdminBrandingController.SaveLogoAsync     [ValidateAntiForgeryToken], [CaseInsensitiveJson], [RequestSizeLimit(MaxLogoRequestBytes)] = 4 MB
  ├─ [FromForm] UploadBrandingLogoRequest: form field "file", [Required]
  ├─ copies the file into a MemoryStream
  └─ IAdminBrandingService.SaveLogoAsync(BrandingLogoUpload)   AdminBrandingService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → AgencyNotSelected → 400
       ├─ AdminBrandingService.ValidateLogo()
       │     0 bytes                     → "Logo file is empty." → 400
       │     over 3 MB (3 * 1024 * 1024) → "Logo file exceeds the maximum size of 3 MB." → 400
       │     extension not .gif/.jpg/.png → "Logo must be a gif, jpg, or png file." → 400
       │     AdminBrandingService.MatchesImageSignature() fails → same message → 400
       ├─ IBrandingRepository.GetByAgencyAsync(agencyId)
       │     no row → "No branding exists for the agency." NotFound → 404
       ├─ stores the raw bytes in ganBranding.HeaderLogo
       ├─ IBrandingRepository.UpdateAsync(branding)
       └─ ISessionService.ReloadSession()   (section 6)
  └─ 200 { success: true, errorMessage: null }
```

- The request body limit is 4 MB (`MaxLogoRequestBytes`) so that a 3 MB file plus multipart overhead reaches the service; a larger body is refused by the server before the action runs.
- The extension check is case-insensitive. `.jpeg` is refused on both sides.
- Signatures: PNG `89 50 4E 47 0D 0A 1A 0A`, GIF `47 49 46 38`, JPG `FF D8 FF`. The extension decides which signature is required.
- The stored bytes are the uploaded file as is. Nothing re-encodes, resizes, or checks dimensions on the server.
- `api.uploadBrandingLogo()` calls `fetch` directly (not `http.sendJson()`) so the browser sets the multipart boundary.

## 5. Deleting the logo

```text
Browser click "Delete" (AdminBranding.vue)
  └─ AdminBranding.deleteLogo()
       ├─ api.deleteBrandingLogo()   → DELETE /api/admin/branding/logo
       │     failure → dialog "Unable to delete logo"
       └─ headerBranding.publishHeaderBranding()   logo null
            └─ AdminShell.onHeaderBrandingChanged()
```

On the server:

```text
AdminBrandingController.DeleteLogoAsync   [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ IAdminBrandingService.DeleteLogoAsync()   AdminBrandingService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → AgencyNotSelected → 400
       ├─ IBrandingRepository.GetByAgencyAsync(agencyId)
       │     no row → NotFound → 404
       ├─ sets ganBranding.HeaderLogo to an empty array
       ├─ IBrandingRepository.UpdateAsync(branding)
       └─ ISessionService.ReloadSession()   (section 6)
  └─ 204 No Content
```

Delete takes effect at once; it is not part of Save and is not undone by Discard.

## 6. How branding reaches the running app

`ganBranding` holds one row per agency: `BrandingID`, `AgencyID`, `ApplicationTitle`, `HeaderTextColor`, `HeaderBackgroundColor`, `HeaderLogo` (bytes), `DateCreated`.

```text
Browser signs in   see Login-Plumbing.md §4
  └─ SessionService.InitSession()
       └─ SessionService.PopulateBrandingSession()
            └─ SessionService.GetBranding(agencyID)
                  agency 0 or -1, or no row → defaults "graspANALYTICS", "#79bee8", "black", empty logo
            → AppSession.Style.HeaderLogo, HeaderTextColor, HeaderBackgroundColor
       └─ SessionService.PopulateAdminSession()   → AppSession.Admin.ApplicationTitle

Browser saves branding or settings (AdminBranding.vue or AdminSettings.vue)
  └─ ISessionService.ReloadSession()   SessionService, after every successful write
       ├─ SessionService.GetAppSession() missing → logs an Error and throws InvalidOperationException (the write is already committed)
       ├─ SessionService.PopulateBrandingSession()   for AppSession.CurrentUser.AgencyID
       ├─ SessionService.PopulateAdminSession()
       ├─ SessionService.PopulateAgencySession()
       ├─ SessionService.PopulateRootSession()
       ├─ SessionService.PopulateFoundrySession()
       └─ SessionService.SetAppSession()   memory cache, keyed by the user name
```

- `ReloadSession` refreshes only the caller's `AppSession`. Other users of the same agency who are already signed in keep the old branding until their next sign-in, because their `AppSession` was built at their own sign-in.
- Home reads `AppSession.Style.*` and `AppSession.Admin.ApplicationTitle` through `HomePresenter.InitializeViewData` into the `data-logo`, `data-header-*`, and `data-title` attributes; see Home-Plumbing.md §1 and §3.
- The Admin header loads `AppSession.Style.*` through the shell context (`AdminGetShellContextUseCase.ExecuteAsync`), but its title comes from `GeneralSettings.ApplicationTitle`, not the agency title. The `admin:header-branding-changed` event updates the header in place after a save, including the agency title; after a page reload the Admin header shows the general title again.
- The logo is always rendered as `data:image/jpeg;base64,...` on Home and in the Admin header.

## Part B - Settings

## 7. Loading the Settings tab

```text
Browser opens the Settings tab (AdminSettings.vue mounts)
  └─ AdminSettings.load()
       ├─ api.getAdminSettings()        → GET /api/admin/settings
       ├─ api.getNotificationOptions()  → GET /api/admin/notifications   sorted by Title
       │     either fails → inline error "Unable to load Admin settings." (or the server message), form hidden
       └─ AdminSettings.snapshot()   becomes the unsaved-changes baseline
```

On the server:

```text
AdminSettingsController.GetAsync
  └─ IAdminSettingsService.GetAsync()   AdminSettingsService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → null → 400, body status "error", message "Select an agency before saving Admin settings."
       ├─ ISettingsRepository.GetAsync(keys, agencyId)   SettingsRepository
       │     per key: the agency row when its value is not empty, else the global row, else ""
       ├─ ISettingsRepository.GetByAgencyAsync(agencyId)
       └─ builds AdminSettingsSnapshot
             SMTPSender                → resolved value
             three notification keys   → the agency row's value even when empty, else the resolved value; not a positive integer → null
             ChangePasswordLinkTimeoutMinutes → resolved value; not 1-43200 → 1440
  └─ 200 AdminSettingsResponse

AdminNotificationsController.GetNotificationsAsync   [CaseInsensitiveJson]
  └─ IAdminNotificationService.GetNotificationsAsync()   AdminNotificationService
       └─ INotificationRepository.GetNotificationsAsync(agencyId)   active and inactive
  └─ 200 list of NotificationItemResponse   (NotificationID, Title, Subject, Message, IsActive, IsSystem, IsInUse, DefaultFor)
```

```json
{ "smtpSender": "noreply@acme.com", "defaultForgotPasswordNotificationId": 12, "defaultChangePasswordNotificationId": null, "defaultNewUserNotificationId": 14, "changePasswordLinkTimeoutMinutes": 1440 }
```

The drop-downs list every notification of the agency, inactive ones included, by `Title`, with `NotificationID` as the value and a clear button.

## 8. Saving settings

```text
Browser click "Save" (AdminSettings.vue)
  └─ AdminSettings.save()
       ├─ AdminSettings.validate()
       │     sender fails ^[^\s@]+@[^\s@.]+\.[^\s@]+$ → "Enter a valid sender email address."
       │     timeout not a whole number 1-43200       → "Enter a whole number from 1 through 43200."
       ├─ trims the sender
       ├─ api.saveAdminSettings()   → PUT /api/admin/settings
       │     success false or error → toast "Save failed" with the server message
       └─ AdminSettings.snapshot(), toast "Settings saved."
```

```http
PUT /api/admin/settings
RequestVerificationToken: <token>
Content-Type: application/json

{ "smtpSender": "noreply@acme.com", "defaultForgotPasswordNotificationId": 12, "defaultChangePasswordNotificationId": null, "defaultNewUserNotificationId": 14, "changePasswordLinkTimeoutMinutes": 1440 }
```

On the server:

```text
AdminSettingsController.SaveAsync         [ValidateAntiForgeryToken], [ApiController] model validation
  └─ IAdminSettingsService.SaveAsync(SaveAdminSettingsCommand)   AdminSettingsService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync()
       │     0 or less → AgencyNotSelected → 400
       ├─ IAdminNotificationService.GetNotificationsAsync()   the agency's notifications
       │     a chosen id not in the list → "One or more selected notifications are not available for this agency." → 403
       ├─ ISettingsRepository.GetByAgencyAsync(agencyId)
       ├─ builds five Setting rows for the agency (sender trimmed, empty id → "")
       ├─ UserInputSanitizer.ContainsScriptAttack(setting)   any row
       │     true → "One or more settings contain invalid content." → 400
       ├─ AdminSettingsService.UpsertAsync()   per key
       │     ├─ no agency row → ISettingsRepository.CreateAsync(setting)
       │     └─ agency row    → ISettingsRepository.UpdateAsync(setting)
       └─ ISessionService.ReloadSession()   (section 6)
  └─ 200 SaveAdminSettingsResponse { success: true, errorMessage: null }
```

`SaveAdminSettingsRequest` rules:

| Field | Rules |
|---|---|
| `smtpSender` | `[Required(AllowEmptyStrings = false)]`, `[EmailAddress]` |
| `defaultForgotPasswordNotificationId` | optional, `[Range(1, int.MaxValue)]` |
| `defaultChangePasswordNotificationId` | optional, `[Range(1, int.MaxValue)]` |
| `defaultNewUserNotificationId` | optional, `[Range(1, int.MaxValue)]` |
| `changePasswordLinkTimeoutMinutes` | required, `[Range(1, 43200)]` |

- Save always writes all five keys as agency rows (`AgencyID` = the selected agency). It never writes a global row (`AgencyID` 0) and never reads or honors `IsLocked` or `IsVisible`. New rows are created with the `Setting` defaults.
- The upserts are separate writes, not one transaction.

## 9. Setting keys

| UI field | `ganSetting.Key` | Scope written | Read on the Settings tab | Read by the running app |
|---|---|---|---|---|
| Default Email Sender Address | `SMTPSender` | agency row | agency value when not empty, else global | `SmtpSettingsResolver.Get` → `ISessionService.GetSettingValue("SMTPSender", "noreply@grasptech.com", agencyId)` on every email (`MailKitEmailSender`) |
| Default Forgot Password Notification | `DefaultForgotPassNotifyID` | agency row, `""` when cleared | agency row even when empty, else global | `NotificationService.GetForgotPasswordNotificationAsync` → `ISettingsRepository.GetAsync(key, agencyId)` (agency when not empty, else global) |
| Default Change Password Notification | `DefaultChangePassNotifyID` | agency row, `""` when cleared | same | `AdminNotificationService.GetNotificationsWithDefaultAsync` presets the user-editor drop-down (`GET /api/admin/notifications/with-default`) |
| Default New User Notification | `DefaultNewUserNotifyID` | agency row, `""` when cleared | same | same as above |
| Change password link timeout (minutes) | `ChangePasswordLinkTimeoutMinutes` | agency row | agency value when not empty, else global; invalid → 1440 | `UserPasswordService.ApplyChangePasswordTokenAsync` → `ISettingsRepository.GetAsync(key, agencyId)`, default 1440 |

### When saved settings apply

- Every runtime reader in the table above queries `ganSetting` on each use, so a saved setting applies to the next email, forgot-password request, user-editor load, or change-password link for every user of the agency, with no sign-in needed.
- `ReloadSession` also refreshes `AppSession.Admin.ChangePasswordLinkTimeoutMinutes`, but nothing reads that copy. `AppSession.SMTP.SMTPSender` is filled only at sign-in (`PopulateSMTPSession` is not part of `ReloadSession`) and nothing reads it either.
- Clearing a notification stores `""` in the agency row. The Settings tab then shows no selection, but the runtime readers treat an empty agency value as missing and fall back to the global row.

## 10. Endpoints

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/admin/branding` | `AdminBrandingController.GetAsync → AdminBrandingService.GetAsync` | none | `applicationTitle`, `headerTextColor`, `headerBackgroundColor`, `headerLogo` (base64) | 200; 400 no agency; 401; 460 |
| `PUT /api/admin/branding` | `AdminBrandingController.SaveAsync → AdminBrandingService.SaveAsync` | `applicationTitle`, `headerTextColor`, `headerBackgroundColor`; antiforgery header | `success`, `errorMessage` | 200; 400 validation, script content, or no agency; 401; 460 |
| `PUT /api/admin/branding/logo` | `AdminBrandingController.SaveLogoAsync → AdminBrandingService.SaveLogoAsync` | multipart `file`; antiforgery header | `success`, `errorMessage` | 200; 400 empty, too large, wrong type or signature, or no agency; 404 no branding row; 401; 460 |
| `DELETE /api/admin/branding/logo` | `AdminBrandingController.DeleteLogoAsync → AdminBrandingService.DeleteLogoAsync` | antiforgery header | no body | 204; 400 no agency; 404 no branding row; 401; 460 |
| `GET /api/admin/settings` | `AdminSettingsController.GetAsync → AdminSettingsService.GetAsync` | none | `smtpSender`, `defaultForgotPasswordNotificationId`, `defaultChangePasswordNotificationId`, `defaultNewUserNotificationId`, `changePasswordLinkTimeoutMinutes` | 200; 400 no agency; 401; 460 |
| `PUT /api/admin/settings` | `AdminSettingsController.SaveAsync → AdminSettingsService.SaveAsync` | the same five fields; antiforgery header | `success`, `errorMessage` | 200; 400 validation, script content, or no agency; 403 notification not in the agency; 401; 460 |
| `GET /api/admin/notifications` | `AdminNotificationsController.GetNotificationsAsync → AdminNotificationService.GetNotificationsAsync` | none | list of `NotificationID`, `Title`, `IsActive`, ... | 200; 401; 460 |
