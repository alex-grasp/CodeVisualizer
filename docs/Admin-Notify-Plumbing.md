---
summary: How the Admin Notify tab works end to end - the Vue panel and dialogs, the notification template API, the tag substitution and password links, and the bulk email send over agency SMTP settings; read before debugging or changing notification templates or admin emails.
area: architecture
---

# Admin Notify plumbing

This doc follows an admin from opening the **Notify** tab on `/Admin` to saved notification templates and sent emails, and names what calls what on the way. The Admin shell, its tabs, the session check, and the antiforgery field are covered elsewhere: see Admin-Plumbing.md.

The tab has two jobs that share one form:

- **Templates.** A notification is a named, saved Subject + Message pair per agency (`ganNotification`). Templates with `AgencyID = 0` are system templates that every agency sees and nobody can edit or delete from this tab.
- **Sending.** The admin picks users, types or loads a Subject and Message, and sends one email per user. Tags such as `{FirstName}` and `{ChgPassURL}` are replaced per recipient on the server.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Tab root | `GraspANALYTICS/ClientApp/src/admin/notify/AdminNotify.vue` | Hosts the `admin-notify` Toast group and `NotifyPanel.vue`. Mounted by `AdminHome.vue` as the `notify` tab. |
| Panel | `GraspANALYTICS/ClientApp/src/admin/notify/NotifyPanel.vue` | User picker, Subject, Message editor, Send / Save / Clear. `load()`, `validateAndSend()`, `doSend()`, `applyNotification()`, `saveNotification()`, `clearNotification()`, `onNotificationDeleted()`. |
| Select dialog | `GraspANALYTICS/ClientApp/src/admin/notify/SelectNotificationDialog.vue` | Lists templates, loads one, deletes one. `refresh()`, `selectNotification()`, `requestDelete()`, `doDelete()`. |
| Save dialog | `GraspANALYTICS/ClientApp/src/admin/notify/SaveNotificationDialog.vue` | Asks for the template name. `save()` emits `save(title)`. |
| Tags dialog | `GraspANALYTICS/ClientApp/src/admin/notify/NotificationTagsDialog.vue` | Static list of the supported tags with example values. No server call. |
| Client API | `GraspANALYTICS/ClientApp/src/admin/notify/api.ts` | `getUsers()`, `getNotifications()`, `getNotification()`, `createNotification()`, `updateNotification()`, `deleteNotification()`, `sendEmails()`. |
| Client types | `GraspANALYTICS/ClientApp/src/admin/notify/types.ts` | `NotifyUser`, `NotificationItem`, `NotificationListItem`, `NotificationWriteResult`, `BulkEmailResult`, `SendEmailsPayload`, `NotificationSavePayload`. |
| Shared client helpers | `GraspANALYTICS/ClientApp/src/shared/antiforgery.ts` + `GraspANALYTICS/ClientApp/src/shared/dialogs.ts` | `antiforgeryToken()` reads the hidden `__RequestVerificationToken` input; `handleNullSession()` sends a 401 to the page's session handler. |
| Users API | `GraspANALYTICS/Controllers/Api/Admin/AdminUsersController.cs` | `GET /api/admin/users` feeds the user picker. |
| Notifications API | `GraspANALYTICS/Controllers/Api/AdminNotificationsController.cs` | `GET`, `POST /api/admin/notifications`; `GET`, `PUT`, `DELETE /api/admin/notifications/{notificationId}`; `GET /api/admin/notifications/with-default`. |
| Email API | `GraspANALYTICS/Controllers/Api/Admin/AdminUserEmailController.cs` | `POST /api/AdminUserEmail/SendEmails` (this tab) and `POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail` (Users tab). |
| Notification contracts | `GraspANALYTICS/Api/Notifications/Contracts/CreateNotificationRequest.cs` + `UpdateNotificationRequest.cs` + `NotificationItemResponse.cs` + `NotificationWriteResponse.cs` + `NotificationWithDefaultResponse.cs` + `NotificationContractMapper.cs` | Wire shapes and `ToResponse()` / `ToResponseList()`. |
| Email contracts | `GraspANALYTICS/Api/Users/Contracts/SendUserEmailsRequest.cs` + `BulkEmailResponse.cs` + `UserResponse.cs` + `UserContractMapper.cs` | `SendUserEmailsRequest.ToCommand()`, `BulkEmailResult.ToResponse()`, `UserDto.ToResponse()`. |
| Template service | `GraspANALYTICS.Core/Application/Notifications/AdminNotificationService.cs` | `GetNotificationAsync`, `GetNotificationsAsync`, `GetNotificationsWithDefaultAsync`, `GetNotificationsWithDefaultForSuperAdminAsync`, `CreateNotificationAsync`, `UpdateNotificationAsync`, `DeleteNotificationAsync`. |
| Default-use keys | `GraspANALYTICS.Core/Application/Notifications/NotificationDefaultSettings.cs` | `DefaultForgotPassNotifyID`, `DefaultChangePassNotifyID`, `DefaultNewUserNotifyID`; `ResolveSettingKey()`. |
| Template repository | `GraspANALYTICS.Core/Application/Notifications/Repos/NotificationRepository.cs` | `GetNotificationAsync`, `GetNotificationsAsync`, `CreateAsync`, `UpdateAsync`, `DeleteNotificationAsync`, `TitleExistsAsync`. |
| Domain models | `GraspANALYTICS.Domain/Models/Notifications/` | `NotificationItemDto`, `NotificationWriteResult`, `NotificationWriteErrorCode`, `NotificationWriteErrors`, `DeleteNotificationResult`, `NotificationDefaultContext`, `NotificationTemplateTokens`, `NotificationRecipient`, `NotificationParseResult`. |
| Get users use case | `GraspANALYTICS.Core/Application/UseCases/AdminGetUsersUseCase.cs` | `ExecuteAsync` lists the agency's users in the caller's scope. |
| Send use case | `GraspANALYTICS.Core/Application/UseCases/AdminSendUserEmailsUseCase.cs` | `ExecuteAsync` loops over the user ids; `TrySendAsync` parses and sends one email. |
| Send command and result | `GraspANALYTICS.Core/Application/Users/SendUserEmailsCommand.cs` + `BulkEmailResult.cs` | `BulkEmailResult.Completed()` and `BulkEmailResult.Fail()`. |
| Tag parser | `GraspANALYTICS.Core/Application/Notifications/NotificationParserService.cs` | `Parse()` replaces the tags in the Subject and Message. |
| Password links | `GraspANALYTICS.Core/Application/UsersPassword/UserPasswordService.cs` + `GraspANALYTICS.Core/Infrastructure/Resolvers/HttpContextUrlResolver.cs` | `ApplyChangePasswordTokenAsync()` issues the token; `GetChangePasswordUrl()`, `GetLoginUrl()`, `GetNewUserUrl()` build the links. |
| Email sender | `GraspANALYTICS.Core/Application/Services/MailKitEmailSender.cs` | `SendAsync()` builds the MIME message, embeds images, sends over SMTP. |
| SMTP settings | `GraspANALYTICS.Core/Application/Resolvers/SmtpSettingsResolver.cs` → `GraspANALYTICS/Services/SessionService.cs` | `Get(agencyId)` reads `SMTPHost`, `SMTPPort`, `SMTPUser`, `SMTPSender`, `SMTPPassword` through `SessionService.GetSettingValue()`. |
| Email images | `GraspANALYTICS.Core/Infrastructure/Resolvers/WebHostEmailImageDirectoryResolver.cs` | `GetImageDirectory()` → `wwwroot/shared/UserFiles/Images`. |
| Selected agency | `GraspANALYTICS.Core/Application/Agencies/AgenciesService.cs` | `GetSelectedAgencyIdAsync()` used by the template service. |
| Caller context | `GraspANALYTICS/Helpers/RequireUserContextAttribute.cs` → `GraspANALYTICS.Core/Application/Resolvers/UserContextResolver.cs` | Builds `UserContext` (user, role, corporation, agency) for the users and email APIs. |
| Sanitizer | `GraspANALYTICS.Data/Models/UserInputSanitizer.cs` | `ThrowExceptionOnScriptAttacks()` rejects HTML-tag-shaped text in string properties; skips `Notification.Message`. |

## 1. Opening the tab

```text
Browser click "Notify" tab (AdminHome.vue)
  └─ NotifyPanel.load()                       onMounted
       └─ api.getUsers()  → GET /api/admin/users
            ├─ keeps users with isActive, sorted by firstName
            └─ failure → dialogs.handleNullSession(error), toast "Unable to retrieve notification users."
```

On the server:

```text
AdminUsersController.GetUsersAsync          [Authorize(Roles = "Super Admin,Agency Admin,Company Admin")], [CustomAuthorizeAttribute], [RequireUserContext]
  └─ IAdminGetUsersUseCase.ExecuteAsync(userContext)   AdminGetUsersUseCase
       ├─ userContext.AgencyId <= 0 → empty list
       ├─ IUserRepository.GetByAgencyAsync(agencyId)
       ├─ caller not Super Admin  → drops Super Admin users
       ├─ caller is a company role → keeps only users with the caller's CorporationID
       └─ IRoleRepository.GetRolesAsync + IUserReportRepository.GetUserReportsExcludingDeletedAsync
  └─ returns 200 with the UserResponse list
```

- The panel uses `userId`, `email`, `firstName`, `lastName`, `login`, and `isActive` from each `UserResponse`.
- `CustomAuthorizeAttribute` answers 401 (`ExpiredLoginResult`) when the app session is gone, or `DuplicateLoginResult` when `LastLoginWins` is on and another sign-in took over. `RequireUserContext` answers 401 when no `UserContext` can be built.
- This is the same endpoint the Users tab uses; the Notify tab only reads it.

## 2. Choosing recipients

The `PickList` in `NotifyPanel.vue` holds two lists:

- **All Users** (`availableUsers`): active users not already selected, filtered by the search box against email, first name, and last name (case-insensitive substring).
- **Selected Users** (`selectedUsers`).

`NotifyPanel.onUserSelectionChange()` and `NotifyPanel.selectNextUser()` keep keyboard selection on the next row after a move. Nothing is sent to the server until Send.

Who receives the email is decided in `NotifyPanel.doSend()`:

| Selected Users | Confirmation | Recipients sent |
|---|---|---|
| none | "Send email to all users" dialog | every user in `allUsers` |
| all of them | the same dialog | every user in `allUsers` |
| some | none | the selected users |

## 3. Templates: list, load, save, delete

### 3a. Listing and loading

```text
Browser click "Select Notification" (NotifyPanel.vue)
  └─ SelectNotificationDialog.refresh()       watch(visible) on open
       └─ api.getNotifications()  → GET /api/admin/notifications
Browser click "Select" (SelectNotificationDialog.vue)
  └─ SelectNotificationDialog.selectNotification()
       ├─ nothing selected → toast "You must select a notification."
       └─ api.getNotification(id)  → GET /api/admin/notifications/{notificationId}
            └─ emits select → NotifyPanel.applyNotification()
```

- `NotifyPanel.applyNotification()` copies `Subject` and `Message` into the form and remembers `NotificationID` and `Title`. The title shows next to the Message label.
- The list shows `Title`. When the selected item has `IsInUse`, the dialog shows which defaults it serves (`DefaultFor`: forgot password, change password, new user emails).

On the server:

```text
AdminNotificationsController.GetNotificationsAsync     [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [CaseInsensitiveJson]
  └─ IAdminNotificationService.GetNotificationsAsync   AdminNotificationService
       ├─ IAgenciesService.GetSelectedAgencyIdAsync
       ├─ INotificationRepository.GetNotificationsAsync(agencyId)    AgencyID = agencyId or 0
       └─ AdminNotificationService.GetDefaultUsesByNotificationAsync(agencyId)
            └─ ISettingsRepository.GetAsync(the three default keys, agencyId)
  └─ returns 200 with the NotificationItemResponse list

AdminNotificationsController.GetNotificationAsync      same attributes
  └─ IAdminNotificationService.GetNotificationAsync(notificationId)
       ├─ IAgenciesService.GetSelectedAgencyIdAsync
       ├─ INotificationRepository.GetNotificationAsync(notificationId, agencyId)
       │     none → 404
       └─ AdminNotificationService.GetDefaultUsesByNotificationAsync(agencyId)
  └─ returns 200 with NotificationItemResponse
```

- `IsSystem` is `AgencyID == 0`. `IsInUse` is true when any of `DefaultForgotPassNotifyID`, `DefaultChangePassNotifyID`, `DefaultNewUserNotifyID` holds this id. The settings lookup prefers the agency's non-empty value and falls back to the global (`AgencyID = 0`) value.
- `IAgenciesService.GetSelectedAgencyIdAsync` returns the session user's `AgencyID` when it is above 0, otherwise the value cached under `SuperAdminAgencyID`.
- `[CaseInsensitiveJson]` swaps the output formatter for one without a camel-case policy, so these responses use the PascalCase names (`NotificationID`, `Title`, `Subject`, `Message`, `IsActive`, `IsSystem`, `IsInUse`, `DefaultFor`).
- These GET endpoints have no `[RequireUserContext]`; the agency comes from the session through `AgenciesService`.

### 3b. Saving

```text
Browser click "Save Notification" (NotifyPanel.vue)
  └─ opens SaveNotificationDialog.vue with the current title
Browser click "Save" (SaveNotificationDialog.vue)
  └─ SaveNotificationDialog.save()  emits save(title trimmed)
       └─ NotifyPanel.saveNotification(title)
            ├─ blank title   → toast "You must enter a name."
            ├─ blank subject → toast "You must enter a subject."
            ├─ a template is loaded → api.updateNotification(id, payload)  → PUT /api/admin/notifications/{notificationId}
            └─ otherwise            → api.createNotification(payload)      → POST /api/admin/notifications
```

- The payload is `{ title, subject (trimmed), message (editor HTML), isActive: true }`. The panel always sends `isActive: true`.
- On success the panel keeps the returned `NotificationId`, so the next Save updates the same template. On failure it shows `ErrorMessage` from the response, or "Error saving notification."
- The inputs cap Name at 100 and Subject at 200 characters; the server caps `Title` at 200 and `Subject` at 300.
- Loading a system template and pressing Save sends a PUT that the server answers with 404 "Notification not found." (see below). To keep a copy, the admin clears the form and saves under a new name.

On the server:

```text
AdminNotificationsController.CreateNotificationAsync     [ValidateAntiForgeryToken], [CaseInsensitiveJson], [ApiController] model validation
  └─ IAdminNotificationService.CreateNotificationAsync(title, subject, message, isActive)
       ├─ AdminNotificationService.ValidateContent → blank Title / Subject / Message → InvalidInput
       ├─ IAgenciesService.GetSelectedAgencyIdAsync
       │     agencyId <= 0 → AgencyNotSelected
       ├─ INotificationRepository.TitleExistsAsync(agencyId, title, null)
       │     true → DuplicateTitle
       ├─ AdminNotificationService.ContainsScriptAttack(notification)
       │     true → InvalidInput "Notification contains invalid content."
       ├─ INotificationRepository.CreateAsync(notification)
       └─ AdminNotificationService.WriteAuditLogAsync("Created notification {id}")
  └─ returns 201 with NotificationWriteResponse { Success, ErrorMessage, NotificationId }

AdminNotificationsController.UpdateNotificationAsync     same attributes
  ├─ notificationId <= 0 → 400 "A valid notification id is required."
  └─ IAdminNotificationService.UpdateNotificationAsync(notificationId, title, subject, message, isActive)
       ├─ AdminNotificationService.ValidateContent
       ├─ IAgenciesService.GetSelectedAgencyIdAsync
       │     agencyId <= 0 → AgencyNotSelected
       ├─ INotificationRepository.GetNotificationAsync(notificationId, agencyId)
       │     none, or AgencyID differs (system template) → NotFound (3001)
       ├─ INotificationRepository.TitleExistsAsync(agencyId, title, notificationId)
       │     true → DuplicateTitle
       ├─ AdminNotificationService.ContainsScriptAttack(notification)
       ├─ INotificationRepository.UpdateAsync(notification)
       └─ AdminNotificationService.WriteAuditLogAsync("Updated notification {id}")
  └─ returns 200 with NotificationWriteResponse
```

- `AdminNotificationsController.ToErrorResult` maps `DuplicateTitle` → 409, `NotFound` → 404, everything else (`InvalidInput`, `AgencyNotSelected`) → 400.
- A request that fails `[Required]` or `[StringLength]` is refused by `[ApiController]` with a 400 validation problem that has no `ErrorMessage`, so the panel shows the generic message.
- The title check is exact-match against the agency's templates and the system templates.
- Create stamps `DateCreated` and `CreatedBy`; update stamps `DateModified` and `ModifiedBy`. Both use the session user's `Username`.

### 3c. Deleting

```text
Browser click "Delete" (SelectNotificationDialog.vue)
  └─ SelectNotificationDialog.requestDelete()
       ├─ nothing selected → toast "You must select a notification to be deleted."
       ├─ IsSystem         → toast "You cannot delete a system notification."  (the button is also disabled)
       ├─ api.getNotification(id)  → GET /api/admin/notifications/{notificationId}
       │     IsInUse → "Delete Notification" confirm dialog → SelectNotificationDialog.doDelete()
       └─ SelectNotificationDialog.doDelete()
            └─ api.deleteNotification(id)  → DELETE /api/admin/notifications/{notificationId}
                 ├─ emits deleted → NotifyPanel.onNotificationDeleted()
                 └─ SelectNotificationDialog.refresh()
```

- `NotifyPanel.onNotificationDeleted()` forgets the loaded template id and title when the deleted one was loaded; the Subject and Message stay in the form.

On the server:

```text
AdminNotificationsController.DeleteNotificationAsync     [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  ├─ notificationId <= 0 → 400 DeleteNotificationResult "A valid notification id is required."
  └─ IAdminNotificationService.DeleteNotificationAsync(notificationId)
       ├─ IAgenciesService.GetSelectedAgencyIdAsync
       ├─ INotificationRepository.DeleteNotificationAsync(notificationId, agencyId)   AgencyID = agencyId only
       │     0 rows → 404 DeleteNotificationResult (3001)
       ├─ AdminNotificationService.RemoveDeletedNotificationSettingsAsync(notificationId, agencyId)
       │     └─ ISettingsRepository.GetByAgencyAsync(agencyId) → each default key holding this id → ISettingsRepository.UpdateAsync with ""
       └─ AdminNotificationService.WriteAuditLogAsync("Deleted notification {id}")
  └─ returns 200 with DeleteNotificationResult { Success, Error }
```

- The repository delete matches `AgencyID = agencyId` exactly, so a system template is not deleted for an agency above 0.
- Clearing a default key sets the agency's setting value to empty, so the default lookup falls back to the global value.

## 4. Tags

`NotificationTagsDialog.vue` lists the tags. The example URLs it shows are built in the browser from `location.origin` + `appUrl(path)`; the real values are built on the server.

| Tag | Constant (`NotificationTemplateTokens`) | Replaced with |
|---|---|---|
| `{FirstName}` | `FirstName` | the recipient's `FirstName` |
| `{LastName}` | `LastName` | the recipient's `LastName` |
| `{Login}` | `Login` | the recipient's `Login` |
| `{Email}` | `Email` | the recipient's `Email` |
| `{Today}` | `Today` | server local time, `MM/dd/yyyy hh:mm tt` |
| `{TodayDate}` | `TodayDate` | `MM/dd/yyyy` |
| `{TodayTime}` | `TodayTime` | `hh:mm tt` |
| `{LoginURL}` | `LoginUrl` | `IUrlResolver.GetLoginUrl()` → `{scheme}://{host}/Login` |
| `{ChgPassURL}` | `ChangePasswordUrl` | `IUrlResolver.GetChangePasswordUrl(token, login)` → `{scheme}://{host}/ChangePassword?t={token}&u={login HTML-encoded}` |
| `{NewUserURL}` | `NewUserUrl` | `IUrlResolver.GetNewUserUrl(token, login)` → `{scheme}://{host}/Welcome?t={token}&u={login URL-encoded}` |

### `NotificationParserService.Parse` — substitution

`NotificationParserService.Parse(message, subject, recipient)` runs `ReplaceTags` on the Message and then on the Subject. `ReplaceTags` is a chain of ordinal `string.Replace` calls, so tags are case-sensitive and every occurrence is replaced. Recipient values are inserted as they are, without HTML encoding.

`HttpContextUrlResolver` builds the base URL from the current request: `https://` when `Request.IsHttps`, otherwise `http://`, then `Request.Host`. It does not add `Request.PathBase`.

### Password links: creation and expiry

`AdminSendUserEmailsUseCase.ExecuteAsync` decides once per send whether tokens are needed: the raw Message contains `{ChgPassURL}` or `{NewUserURL}`. The Subject is not checked. When needed, each recipient goes through:

```text
IUserPasswordService.ApplyChangePasswordTokenAsync(userId, userContext)   UserPasswordService
  ├─ IUserRepository.GetAsync(userId, userContext.AgencyId)
  │     none → null, no token (12002)
  ├─ UserInputSanitizer.ThrowExceptionOnScriptAttacks(user)
  ├─ ISettingsRepository.GetAsync("ChangePasswordLinkTimeoutMinutes", agencyId)   default 1440
  └─ IUserRepository.UpdateUserAsync(user)
        TokenExpire = now + timeout, PasswordToken = new GUID, ForcePasswordReset = true
```

- The token is issued before the email is sent, and is kept even when the send fails.
- Each send replaces the user's previous `PasswordToken`, so an older link stops working.
- `ForcePasswordReset = true` makes the next password sign-in go to the change-password page (see Login-Plumbing.md §6c).
- `ChangePasswordLinkTimeoutMinutes` is read as the agency's non-empty value, else the global value, else 1440 minutes.
- When no token is issued, `{ChgPassURL}` and `{NewUserURL}` are filled with an empty `t=`.

## 5. Sending

```text
Browser click "Send" (NotifyPanel.vue)
  └─ NotifyPanel.validateAndSend()
       ├─ blank subject → toast "You must enter a subject."
       ├─ NotifyPanel.isMessageEmpty() → toast "You must enter a message."
       ├─ none or all users selected → "Send email to all users" dialog → NotifyPanel.doSend()
       └─ NotifyPanel.doSend()
            └─ api.sendEmails({ subject, message, userIds })  → POST /api/AdminUserEmail/SendEmails
                 ├─ success → "Success" / "Send Summary" dialog
                 └─ failure → dialogs.handleNullSession(error), toast errorMessage or "Error sending emails."
```

- `NotifyPanel.isMessageEmpty()` parses the editor HTML and treats it as empty when it has no text and no `<img>`.
- The Subject is trimmed; the Message is the editor HTML as is. Nothing is saved as a template when sending.

```http
POST /api/AdminUserEmail/SendEmails
RequestVerificationToken: <hidden input value>
Content-Type: application/json
```

```json
{ "subject": "Hello {FirstName}", "message": "<p>Reset here: {ChgPassURL}</p>", "userIds": [12, 34] }
```

On the server:

```text
AdminUserEmailController.SendEmailsAsync     [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [RequireUserContext], [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ SendUserEmailsRequest.ToCommand()   UserContractMapper → SendUserEmailsCommand(Subject, Message, UserIds)
  └─ IAdminSendUserEmailsUseCase.ExecuteAsync(command, userContext)   AdminSendUserEmailsUseCase
       ├─ userContext.AgencyId <= 0 → BulkEmailResult.Fail(AgencyNotSelected) → 400
       ├─ IEmailImageDirectoryResolver.GetImageDirectory()
       └─ for each user id, one after another
            ├─ IUsersService.GetUserAsync(userId, agencyId)    UsersService
            │     none → failed "user #{id}" (9005, 16001)
            ├─ IUserPasswordService.ApplyChangePasswordTokenAsync(userId, userContext)   only when tokens are needed (section 4)
            └─ AdminSendUserEmailsUseCase.TrySendAsync(command, recipient, imageDirectory, userContext)
                 ├─ INotificationParserService.Parse(HTML-decoded Message, HTML-decoded Subject, recipient)
                 ├─ IEmailSender.SendAsync(email, subject, body, imageDirectory, agencyId)   MailKitEmailSender
                 │     not Success → failed recipient (16002)
                 └─ exception → failed recipient (16003); cancellation rethrown
  └─ returns 200 with BulkEmailResponse { success, sentCount, totalCount, failedRecipients, errorMessage }
```

- The send is synchronous and sequential inside the HTTP request: one recipient at a time, one SMTP connection per email. There is no queue or background job.
- `success` is `sentCount == totalCount`. A partial failure is still a 200; the panel shows "Sent X of Y emails." and lists `failedRecipients` (the email address, or `user #id` when the user was not found).
- `SendUserEmailsRequest` requires a non-empty `subject` and `message` and at least one `userIds` entry; otherwise `[ApiController]` returns a 400 validation problem.
- The use case loads each user with `UserID` and `AgencyID = userContext.AgencyId`. It does not re-check `IsActive`, the caller's corporation, or the recipient's role; those filters exist only in `AdminGetUsersUseCase` and the panel.
- An exception from `ApplyChangePasswordTokenAsync` (for example the sanitizer rejecting a user record) is outside `TrySendAsync`'s try block, so it ends the request and emails already sent stay sent.

### `MailKitEmailSender.SendAsync` — one email

1. `ISmtpSettingsResolver.Get(agencyId)` (section 6).
2. Builds a `MimeMessage`: From = `SMTPSender` (display name and address), To = the recipient, Subject = the parsed subject.
3. `MailKitEmailSender.EmbedImages` finds each `<img ... src="...">`; when a file with the same file name exists in `wwwroot/shared/UserFiles/Images`, it attaches it and rewrites the `src` to `cid:{guid}`. Other images are left as they are.
4. Sets the parsed Message as the HTML body.
5. `SmtpClient.ConnectAsync(host, port, StartTls)`, `AuthenticateAsync(user, password)`, `SendAsync`, `DisconnectAsync`.
6. Any exception other than cancellation is logged ("MailKitEmailSender failed to send email for AgencyId=...", no EventId) and returned as `EmailSendResult(false, message)`.

## 6. SMTP settings

`SmtpSettingsResolver.Get(agencyId)` reads every value through `SessionService.GetSettingValue(key, default, agencyId)`:

| Key | Default |
|---|---|
| `SMTPHost` | `grasp.smtp.com` |
| `SMTPPort` | `587` |
| `SMTPUser` | `noreply@grasptech.com` |
| `SMTPSender` | `noreply@grasptech.com` |
| `SMTPPassword` | none; must be present (`Guard.NotNullOrWhiteSpace`) and is decrypted with `IEncryptionService<string>` |

- **Per agency vs global.** `SessionService.GetSettingValue` uses the current app session's agency when a session exists, and the `agencyId` argument only when there is none. It returns the agency's `ganSetting` value when that is non-empty, otherwise the global (`AgencyID = 0`) value, otherwise the default above.
- A missing `SMTPPassword` throws inside `MailKitEmailSender.SendAsync`'s try block, so every recipient fails with a logged error.
- A decrypt failure is logged ("SmtpSettingsResolver.Get: failed to decrypt SMTP password ...") and the send continues with whatever the decrypt returned; authentication then fails.

## 7. Sanitization and validation

| Field | On save (`AdminNotificationService`) | On send (`AdminSendUserEmailsUseCase`) |
|---|---|---|
| `Title` | required; `[StringLength(200)]`; must be unique; rejected when it contains an HTML tag | not sent |
| `Subject` | required; `[StringLength(300)]`; rejected when it contains an HTML tag | required; HTML-decoded; not sanitized |
| `Message` | required; no length cap; **not sanitized** | required; HTML-decoded; not sanitized |

`UserInputSanitizer.ThrowExceptionOnScriptAttacks(obj)`:

- Checks every writable `string` property of the object against an HTML-tag regex and throws when any match is found.
- Skips the property named `Message` when the class is named `Notification`. This is why a template's Message can hold the editor's HTML.
- Applies to the other string properties of `Notification` too (`Title`, `Subject`, `CreatedBy`, `ModifiedBy`).
- `AdminNotificationService.ContainsScriptAttack` catches any exception from it and reports "Notification contains invalid content." The exception message itself is never returned.

The send path does not call the sanitizer on the Subject or Message. It HTML-decodes both before tag substitution and sends the result as the HTML body.

## 8. Endpoints

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/admin/users` | `AdminUsersController.GetUsersAsync → AdminGetUsersUseCase.ExecuteAsync` | none | `UserResponse[]` | 200; 401 |
| `GET /api/admin/notifications` | `AdminNotificationsController.GetNotificationsAsync → AdminNotificationService.GetNotificationsAsync` | none | `NotificationItemResponse[]` | 200; 401 |
| `GET /api/admin/notifications/{notificationId}` | `AdminNotificationsController.GetNotificationAsync → AdminNotificationService.GetNotificationAsync` | `notificationId` | `NotificationItemResponse` | 200; 401; 404 |
| `POST /api/admin/notifications` | `AdminNotificationsController.CreateNotificationAsync → AdminNotificationService.CreateNotificationAsync` | `Title`, `Subject`, `Message`, `IsActive` | `NotificationWriteResponse` with `NotificationId` | 201; 400; 401; 409 |
| `PUT /api/admin/notifications/{notificationId}` | `AdminNotificationsController.UpdateNotificationAsync → AdminNotificationService.UpdateNotificationAsync` | `notificationId`; `Title`, `Subject`, `Message`, `IsActive` | `NotificationWriteResponse` | 200; 400; 401; 404; 409 |
| `DELETE /api/admin/notifications/{notificationId}` | `AdminNotificationsController.DeleteNotificationAsync → AdminNotificationService.DeleteNotificationAsync` | `notificationId` | `DeleteNotificationResult` | 200; 400; 401; 404 |
| `GET /api/admin/notifications/with-default` | `AdminNotificationsController.GetNotificationsWithDefaultAsync → AdminNotificationService.GetNotificationsWithDefaultAsync` | `context` (query, `NotificationDefaultContext`) | `NotificationWithDefaultResponse[]` | 200; 401 |
| `POST /api/AdminUserEmail/SendEmails` | `AdminUserEmailController.SendEmailsAsync → AdminSendUserEmailsUseCase.ExecuteAsync` | `subject`, `message`, `userIds` | `BulkEmailResponse` | 200; 400; 401 |
| `SMTP {SMTPHost}:{SMTPPort}` | `MailKitEmailSender.SendAsync → SmtpClient.SendAsync` | the MIME message, StartTLS, `SMTPUser` / `SMTPPassword` | none | failure becomes a failed recipient |

- Every write endpoint has `[ValidateAntiForgeryToken]`; `api.sendJson` and `api.deleteNotification` send the `RequestVerificationToken` header from the page's hidden `__RequestVerificationToken` input (rendered by `Views/Admin/Index.cshtml`).
- `with-default` is not called by this tab; the Users tab (`ClientApp/src/admin/users/api.ts`) uses it to fill the notification picker for password-reset and welcome emails, together with `POST /api/AdminUserEmail/{userId}/SendPasswordResetEmail`.

## 9. Super Admin counterpart

`GraspANALYTICS/Controllers/Api/SuperAdminNotificationsController.cs` (`[Route("api/[controller]")]`, `[Authorize(Roles = "Super Admin")]`, `[CustomAuthorizeAttribute]`) exposes `GET /api/SuperAdminNotifications/GetNotificationsWithDefault`, backed by `AdminNotificationService.GetNotificationsWithDefaultForSuperAdminAsync`, which throws `AgencyNotSelectedException` when no agency is selected. The Super Admin user editor (`ClientApp/src/superadmin/user-edit/api.ts`) calls it. It has no template editing or bulk send.

## 10. What gets logged

- **Audit log** (`AuditLog` via `ISystemEventLogRepository.CreateAuditLogAsync`): one row per template create, update, and delete, with `Page = "Admin/Notifications"`, the reason ("Created notification {id}", "Updated notification {id}", "Deleted notification {id}"), login, user, role, and agency. A failed audit write is logged and does not fail the request. Bulk sends write no audit row.
- **Application log**: `AdminNotificationService` 3001 (template not found, Warning) and 3002 (audit write failed, Error); `AdminSendUserEmailsUseCase` 16001 (user not found), 16002 (send failed), 16003 (send exception); `UsersService` 9005 (user not found); `UserPasswordService` 12002 (token request for an unknown user). `MailKitEmailSender` and `SmtpSettingsResolver` log errors without an EventId. See `Logging-EventId-Registry.md`.
