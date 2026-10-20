---
summary: How the change-password page works end to end - the standalone Vue page, the page-state and change-password APIs, the Core use case, the password policy, and where the browser goes afterwards; read before touching anything on the forced password change path.
area: architecture
---

# Change-password plumbing

This doc follows a visitor from opening `/ChangePassword?u=...&t=...` to landing back on `/Login`, and names what connects to what and what calls what on the way. It describes the code as of PR 1530 (story 7102).

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Page HTML | `ClientApp/src/public/change-password/index.html` | Vite HTML entry. Built to `wwwroot/vue/src/public/change-password/index.html` with fallback text inside `#change-password-app`. |
| Page bootstrap | `ClientApp/src/public/change-password/main.ts` | Sets the app base, reads `u` and `t` from the query string, loads the page data, mounts `ChangePasswordPage.vue`. |
| Page component | `ClientApp/src/public/change-password/ChangePasswordPage.vue` | The form. Checks that the two new passwords match, shows the policy hints, calls `changePassword`, follows `nextUrl` or shows the error inline. |
| Page API client | `ClientApp/src/public/change-password/api.ts` | `loadChangePasswordPage()` and `changePassword()`. |
| Shared public calls | `ClientApp/src/public/shared/publicSettings.ts` | `GET /api/public/settings` and `GET /api/public/antiforgery-token`. |
| Page delivery | `Controllers/ChangePasswordPageController.cs` + `Infrastructure/Vite/VitePageResolver.cs` | Serves the built HTML at `/ChangePassword`. |
| Change-password API | `Controllers/Api/ChangePasswordController.cs` | `GET /api/change-password` (page state) and `POST /api/change-password` (change). |
| Contract mapping | `Api/ChangePassword/Contracts/ChangePasswordContractMapper.cs` | Request to `ChangePasswordCommand`; `ChangePasswordOutcome` to response; destination to URL. |
| Change-password use case | `GraspANALYTICS.Core/Application/ChangePassword/UseCases/ChangePasswordUseCase.cs` | The change-password rules. |
| Password policy | `.../ChangePassword/Services/PasswordPolicyService.cs` | Length, character classes, history and dictionary checks. |
| Token check | `.../ChangePassword/Services/PasswordTokenService.cs` | Validates the one-time token from the `t` query parameter and expires it. |
| Password hashing | `Core/Infrastructure/Security/Pbkdf2HashingProvider.cs` | Hashes the new password before `IUserRepository.UpdatePasswordAsync` stores it. |
| Audit | `.../ChangePassword/Services/PasswordChangeAuditService.cs` | Writes an `AuditLog` row for every outcome. |
| Notification | `Services/Mail/PasswordChangedMailer.cs` | Sends the "your password was changed" mail. |

## 1. Loading the page

```text
Browser GET /ChangePassword?u=<login>&t=<token>
  └─ ChangePasswordPageController.Index
       ├─ missing u or t → 302 to /Login
       └─ otherwise: Cache-Control: no-store
            VitePageResolver.GetPagePath("src/public/change-password/index.html")
            → PhysicalFile(wwwroot/vue/src/public/change-password/index.html)
```

- `VitePageResolver` reads the file from the web root and throws if the client build is missing.
- `/ChangePassword` answers with status **401** for an anonymous visitor even though the page renders; the change-password paths are on the anonymous allow-list of `UseGraspContentSecurityPolicy`.

Then the browser runs `main.ts`:

1. `setAppBaseFromBundleUrl(import.meta.url)` works out the application base from the bundle's own address.
2. `readTokenFromQuery()` picks `u` and `t` out of `location.search`; without both it shows "This link is not valid" and stops.
3. `loadChangePasswordPage(login, token)` makes three calls in parallel:

   | Call | Server | Returns |
   |---|---|---|
   | `GET /api/public/settings` | `PublicSettingsController` → `GeneralSettings`, `IAppVersionService` | `applicationTitle`, `version` |
   | `GET /api/public/antiforgery-token` | `PublicAntiforgeryController` → `IAntiforgery.GetAndStoreTokens` | `requestToken`, plus the antiforgery cookie |
   | `GET /api/change-password` | `ChangePasswordController.GetPageState` → `IPasswordTokenService.ValidateAsync(login, token)`, `IPasswordPolicyService.Describe()` | `tokenValid`, `policy` (min length, required classes), `errorMessage` |

   If the token is invalid the page shows "This link has expired" and a link back to `/Login`.
4. It sets `document.title` and mounts `ChangePasswordPage.vue` with the combined context.

None of these endpoints needs a session; the change-password page is reached before any session exists.

## 2. Submitting the form

`ChangePasswordPage.submit()`:

1. New password and confirmation differ, or either is blank → shows "The passwords do not match" and sends nothing.
2. Otherwise calls `changePassword(antiforgeryToken, { login, token, newPassword })`, which sends:

   ```text
   POST /api/change-password
   RequestVerificationToken: <token from step 1>
   { "login": "...", "token": "...", "newPassword": "..." }
   ```

On the server:

```text
ChangePasswordController.ChangeAsync         [ValidateAntiForgeryToken], [ApiController] model validation
  └─ request.ToCommand(clientAddress)        ChangePasswordContractMapper → ChangePasswordCommand(Login, Token, NewPassword, ClientAddress)
  └─ IChangePasswordUseCase.ExecuteAsync
  └─ result.ToResponse(Url)                  ChangePasswordOutcome → ChangePasswordResponse { nextUrl | errorMessage, policyHints }
  └─ 200, or the error status for the code (table in section 5)
```

- A missing or wrong antiforgery token is refused before the use case runs.
- A blank `newPassword` fails `ChangePasswordRequest` validation (`[Required]`) with a 400 validation problem.

## 3. Inside `ChangePasswordUseCase`

The login is trimmed and compared case-insensitively; the token is compared in constant time.

```text
ExecuteAsync(command)
  ├─ IPasswordTokenService.ValidateAsync(login, token)
  │     expired or unknown → audit TokenRejected → InvalidToken
  ├─ IUserRepository.GetActiveUserByLoginAsync(login)
  │     none → InvalidToken
  ├─ IPasswordPolicyService.Validate(user, newPassword)        (section 4)
  │     any rule fails → PolicyViolation(rules)
  ├─ IHashingProvider.Hash(newPassword)
  ├─ IUserRepository.UpdatePasswordAsync(user, hash)
  │     clears ForcePasswordReset, sets PasswordChangedDate
  ├─ IPasswordTokenService.ExpireAsync(token)
  ├─ IPasswordChangedMailer.SendAsync(user)                    failure is logged, not returned
  └─ audit PasswordChanged → LoginDestination(message: "Your password was changed. Sign in again.")
```

Any unexpected exception becomes `ChangePasswordFailed` ("Could not change the password"), logged as EventId 21041. Cancellation is not swallowed.

## 4. `PasswordPolicyService.Validate`

Rules come from `ISettingsRepository.GetGlobalAsync` (PASSWORD_MIN_LENGTH, PASSWORD_REQUIRED_CLASSES, PASSWORD_HISTORY_DEPTH).

```text
Validate(user, password)
  ├─ ISettingsRepository.GetGlobalAsync
  ├─ length below PASSWORD_MIN_LENGTH             → TooShort
  ├─ fewer character classes than required       → TooSimple
  ├─ IPasswordHistoryRepository.GetRecentAsync(user, depth)
  │     IHashingProvider.Equals(password, oldHash) for any → Reused
  ├─ ICommonPasswordClient.IsCommonAsync(password)   HaveIBeenPwnedClient
  │     true → TooCommon
  └─ otherwise → PolicyOk
```

The rule names are what `PolicyViolation(rules)` carries back to the page, which maps them to the inline hints.

## 5. Back to the browser

`ChangePasswordContractMapper.ToUrl` turns the destination into `nextUrl`:

| Destination | `nextUrl` |
|---|---|
| `LoginDestination` | `/Login`, with the confirmation message saved for `GET /api/sign-in` |
| `ReportsHomeDestination` | `/Home` (only when a signed-in user changed the password voluntarily) |

`ChangePasswordController` picks the status from the error code:

| Error code | Status |
|---|---|
| none (a destination) | 200 |
| `InvalidToken` | 401 |
| `PolicyViolation` | 422 |
| everything else (mail, unexpected) | 500 |

`changePassword()` in `api.ts`:

- 200 with a same-origin `nextUrl` → `ChangePasswordPage` calls `location.assign(nextUrl)`.
- A 422 with `policyHints` → the hints are shown next to the field that failed.
- An error body with `errorMessage` → that message inline.
- Anything else → "Could not change the password".

## 6. Where the follow-on addresses lead

### 6a. Back to the login page

`LoginDestination` sends the browser to `/Login`. The confirmation message travels through `ISignInNoticeStore.Save(message)` and `GET /api/sign-in` hands it to the login page once; see `Login-Plumbing.md` section 6b for the notice store.

### 6b. Expired links

An expired or already-used token answers `InvalidToken`; the page shows "This link has expired" with a link to `/Login`. There is no self-service "send me a new link" - an administrator resets the password, which issues a new token.

## 7. What gets logged

- **Audit log** (`AuditLog` via `ISystemEventLogRepository`): `TokenRejected`, `PolicyViolation` and `PasswordChanged`, with address, login, user and the rule names.
- **Application log**: `ChangePasswordUseCase` 21041 (rejected, Information) and 21042 (unexpected failure); `PasswordChangedMailer` 21051. See `Logging-EventId-Registry.md`.
