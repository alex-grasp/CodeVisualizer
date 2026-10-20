---
summary: How sign-in works end to end - the standalone Vue login page, the public and sign-in APIs, the Core sign-in use case, the session and cookie it creates, and the Duo, SSO, and change-password paths; read before debugging or changing anything on the sign-in path.
area: architecture
---

# Login plumbing

This doc follows a visitor from opening `/Login` to landing on Home or Admin, and names what connects to what and what calls what on the way.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Login page HTML | `GraspANALYTICS/ClientApp/src/public/login/index.html` | Vite HTML entry, built to `GraspANALYTICS/wwwroot/vue/src/public/login/index.html`. Fallback text inside `#login-app` and a `<noscript>` message. |
| Page bootstrap | `GraspANALYTICS/ClientApp/src/public/login/main.ts` | `mountLoginPage()`: loads the page data, sets the title, starts Google Analytics, sets up PrimeVue (green palette), mounts `LoginPage.vue`. |
| Page component | `GraspANALYTICS/ClientApp/src/public/login/LoginPage.vue` | `submit()`, `showError()`, `resetHumanVerification()`, `focusUsername()`: the form, the blank check, reCAPTCHA, the inline error. |
| Page API client | `GraspANALYTICS/ClientApp/src/public/login/api.ts` | `loadLoginPage()` and `signIn()`. |
| Wordmark | `GraspANALYTICS/ClientApp/src/public/login/branding.ts` | `brandWordmark()`: the application title without a leading "grasp". |
| Shared public calls | `GraspANALYTICS/ClientApp/src/public/shared/publicSettings.ts` | `loadPublicSettings()` (`GET /api/public/settings`) and `loadAntiforgeryToken()` (`GET /api/public/antiforgery-token`). |
| Google Analytics | `GraspANALYTICS/ClientApp/src/public/shared/googleAnalytics.ts` | `startGoogleAnalytics()`. |
| App base | `GraspANALYTICS/ClientApp/src/shared/appBase.ts` | `setAppBaseFromBundleUrl()` and `appUrl()`. |
| Page delivery | `GraspANALYTICS/Controllers/LoginController.cs` + `GraspANALYTICS/Infrastructure/Vite/VitePageResolver.cs` | `GET /` and `GET /Login` (`LoginController.Index`); `VitePageResolver.GetPagePath()`. |
| Public settings API | `GraspANALYTICS/Controllers/Api/PublicSettingsController.cs` | `GET /api/public/settings` (`GetSettingsAsync`). |
| Anonymous antiforgery API | `GraspANALYTICS/Controllers/Api/PublicAntiforgeryController.cs` | `GET /api/public/antiforgery-token` (`GetAntiforgeryToken`). |
| Sign-in API | `GraspANALYTICS/Controllers/Api/SignInController.cs` | `GET /api/sign-in` (`GetPageState`) and `POST /api/sign-in` (`SignInAsync`). |
| Contract mapping | `GraspANALYTICS/Api/SignIn/Contracts/SignInContractMapper.cs` | `ToCommand()`, `ToResponse()`, `ToUrl()`. |
| Sign-in notice | `GraspANALYTICS/Infrastructure/SignIn/SessionSignInNoticeStore.cs` | `Save()` and `Take()`: a one-time message kept in the ASP.NET session. |
| Sign-in use case | `GraspANALYTICS.Core/Application/SignIn/UseCases/SignInUseCase.cs` | `ExecuteAsync()`: the sign-in rules. |
| Staff sign-in | `GraspANALYTICS.Core/Application/SignIn/Services/StaffSignInService.cs` | `SignInAsync()`: `grasp\` accounts, LDAP check, then Duo. |
| Completion | `GraspANALYTICS.Core/Application/SignIn/Services/SignInCompletionService.cs` | `CompleteAsync()`, `RejectUnknownLoginAsync()`, `RejectCredentialsAsync()`. Shared with SSO. |
| Capacity resume | `GraspANALYTICS.Core/Application/SignIn/Services/CapacityControlService.cs` | `ResumeIfAllowedAsync()`. |
| Session start | `GraspANALYTICS.Core/Infrastructure/SignIn/Services/HttpAuthenticatedSessionService.cs` → `GraspANALYTICS/Services/LoginService.cs` → `GraspANALYTICS/Services/SessionService.cs` | `StartAsync()`, `EndAsync()`; `LoginService.LoginUser()`, `LoginService.LogoutUserAsync()`; `SessionService.InitSession()`. |
| Failed sign-in markers | `GraspANALYTICS.Core/Infrastructure/SignIn/Services/MemoryFailedSignInStore.cs` | `IsHumanVerificationRequired()`, `RecordFailure()`, `Clear()`. |
| reCAPTCHA check | `GraspANALYTICS.Core/Infrastructure/HumanVerification/Services/ReCaptchaHumanVerificationClient.cs` | `VerifyAsync()`. |
| Audit | `GraspANALYTICS.Core/Application/SignIn/Services/SignInAuditService.cs` | `RecordAsync()`: an `AuditLog` row for every outcome. |
| SSO completion | `GraspANALYTICS.Core/Application/SignIn/UseCases/CompleteSingleSignOnUseCase.cs` + `GraspANALYTICS/Controllers/SingleSignOnController.cs` + `GraspANALYTICS/CompositionRoot/MapSsoEndpointExtensions.cs` | `ExecuteAsync()`; `GET /login/sso`, `GET /validateOAuthSso`; `GET /auth/start`, `GET /auth/signin`. |
| Duo | `GraspANALYTICS/Controllers/DuoController.cs` | `GET /Duo` and `POST /Duo` (`Index`). |
| Change password | `GraspANALYTICS/Controllers/ChangePasswordController.cs` | `GET /ChangePassword` (`Index`). |
| Request check after sign-in | `GraspANALYTICS/Classes/SessionValidationMiddleware.cs` | `Invoke()`: matches the cookie's session claim to the server session on every request. |

## 1. Loading the page

```text
Browser GET /Login (or /)
  └─ LoginController.Index
       ├─ /Login/Index or any deeper path  → 302 to /Login (query kept)
       ├─ empty path (virtual dir root)    → 302 to the app root (query kept)
       └─ otherwise sets Cache-Control: no-store
            ├─ IVitePageResolver.GetPagePath("src/public/login/index.html")   VitePageResolver
            └─ returns the file wwwroot/vue/src/public/login/index.html
```

- `VitePageResolver.GetPagePath` reads the file from the web root and throws if the client build is missing.
- The HTML shows "Loading the login page. If it does not appear, check your connection and refresh the page." inside `#login-app` until Vue replaces it, and a `<noscript>` message without JavaScript.
- `/Login` answers with status **401** for an anonymous visitor even though the page renders; `/` answers 200. `UseGraspContentSecurityPolicy` (`GraspANALYTICS/CompositionRoot/GraspPipelineExtensions.cs`) pre-sets 401 on any anonymous request except `/`, paths containing `login/`, and error-page re-executions. The Admin and Super Admin shells' session check relies on that 401.

Then the browser runs the bundle:

```text
Browser runs the login bundle (main.ts)
  ├─ appBase.setAppBaseFromBundleUrl(import.meta.url)
  ├─ document.getElementById('login-app')                 nothing mounts if it is missing
  └─ main.mountLoginPage()
       ├─ api.loadLoginPage()                               the three calls run in parallel
       │    ├─ publicSettings.loadPublicSettings()          → GET /api/public/settings
       │    ├─ publicSettings.loadAntiforgeryToken()        → GET /api/public/antiforgery-token
       │    ├─ http.requestJson()                           → GET /api/sign-in
       │    └─ any call fails → default title, empty settings, "The login page could not be loaded. Refresh the page to try again."
       ├─ sets document.title to the application title
       ├─ googleAnalytics.startGoogleAnalytics(googleAnalyticsId)
       ├─ primevue.configurePrimeVue(app, 'green')
       └─ app.mount(mountPoint)                             LoginPage.vue with { context }
```

- `appBase.setAppBaseFromBundleUrl` works out the application base from the bundle's own address, so every `appBase.appUrl` and `http.requestJson` call works under a virtual directory.
- The page mounts even when the calls fail; the message then shows inline.

The page-load calls:

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /` and `GET /Login` | `LoginController.Index → IVitePageResolver.GetPagePath` | none (query kept on redirects) | the built login HTML, `Cache-Control: no-store` | 200 for `/`; 401 for `/Login` when anonymous (the page still renders); 302 for deeper `/Login/...` paths and the empty virtual-directory path |
| `GET /api/public/settings` | `PublicSettingsController.GetSettingsAsync → IGoogleAnalyticsIdResolver.GetAsync` (plus `IHumanVerificationWidgetResolver.Resolve`, `IAppVersionService.Version`, `GeneralSettings.ApplicationTitle`) | none | `applicationTitle`, `version`, `googleAnalyticsId`, `humanVerificationSiteKey`, `humanVerificationScriptUrl` | 200 |
| `GET /api/public/antiforgery-token` | `PublicAntiforgeryController.GetAntiforgeryToken → IAntiforgery.GetAndStoreTokens` | none | `requestToken`, plus the antiforgery cookie | 200 |
| `GET /api/sign-in` | `SignInController.GetPageState → ISignInNoticeStore.Take` (plus `IFailedSignInStore.IsHumanVerificationRequired(clientAddress)`) | none | `errorMessage` (a one-time notice, for example from a failed SSO return), `humanVerificationRequired` | 200 |

None of these endpoints needs a session; the app has no fallback authorization policy, so an API without `[Authorize]` is anonymous.

## 2. Submitting the form

```text
Browser click "Sign in" (LoginPage.vue)
  └─ LoginPage.submit()
       ├─ already submitting → ignored
       ├─ username or password blank after trimming → LoginPage.showError("Invalid Username or Password"), nothing sent
       └─ api.signIn(antiforgeryToken, { username, password, humanVerificationToken })
            └─ http.requestJson()   → POST /api/sign-in
```

The request:

```http
POST /api/sign-in
Content-Type: application/json
RequestVerificationToken: <requestToken from GET /api/public/antiforgery-token>

{ "username": "...", "password": "...", "humanVerificationToken": "..." }
```

On the server:

```text
SignInController.SignInAsync                 [ValidateAntiForgeryToken], [ApiController] model validation
  ├─ missing or wrong antiforgery token → 400 before the action runs
  ├─ blank username or password → 400 validation problem ([Required] on SignInRequest)
  ├─ SignInContractMapper.ToCommand(request, clientAddress)        → SignInCommand(Login, Password, HumanVerificationToken, ClientAddress)
  ├─ ISignInUseCase.ExecuteAsync(command)   SignInUseCase           (section 3)
  ├─ SignInContractMapper.ToResponse(result, Url)                  → SignInResponse { nextUrl | errorMessage, humanVerificationRequired }
  └─ returns 200, or the error status for the code                 (section 5)
```

- The client address is `HttpContext.Connection.RemoteIpAddress`.

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/sign-in` | `SignInController.SignInAsync → ISignInUseCase.ExecuteAsync` | `username`, `password`, `humanVerificationToken`; header `RequestVerificationToken` (validated) | `nextUrl`, `errorMessage`, `humanVerificationRequired` | 200; 400 (antiforgery, validation problem, `HumanVerificationFailed`); 401; 403; 409; 500 (section 5) |

## 3. `SignInUseCase.ExecuteAsync`

The account name used for the failed-sign-in markers is the login trimmed, without a trailing `\admin`.

```text
ISignInUseCase.ExecuteAsync(command)   SignInUseCase                          (from section 2)
  ├─ IFailedSignInStore.IsHumanVerificationRequired(accountName, clientAddress)
  │     required and IHumanVerificationClient.VerifyAsync(token) fails   ReCaptchaHumanVerificationClient
  │       → IFailedSignInStore.RecordFailure(clientAddress) → HumanVerificationFailed
  ├─ SignInUseCase.SignInAsync
  │   ├─ login starts with "grasp\" → IStaffSignInService.SignInAsync   StaffSignInService     (section 6a)
  │   └─ otherwise a trailing "\admin" is stripped and asks for the admin home
  │       ├─ IUserRepository.GetActiveUserByLoginAsync(login)
  │       │     none → ISignInCompletionService.RejectUnknownLoginAsync → InvalidCredentials
  │       ├─ user is SSO-only → IdentityProviderDestination(tenant hint)                      (section 6b)
  │       ├─ no password set → ISignInCompletionService.RejectCredentialsAsync → InvalidCredentials
  │       ├─ IHashingProvider.Equals(password, hash)
  │       │     no match → ISignInCompletionService.RejectCredentialsAsync → InvalidCredentials
  │       └─ ISignInCompletionService.CompleteAsync(user, login, attempt, PasswordSignIn)    (section 4)
  ├─ no destination → IFailedSignInStore.RecordFailure(accountName, clientAddress), returns with humanVerificationRequired
  └─ destination with verified credentials → IFailedSignInStore.Clear(accountName, clientAddress)
```

- Any unexpected exception becomes `SignInFailed` ("Could not login user"), logged as EventId 21002, and records markers like any other rejection. Cancellation is not swallowed.
- Every rejection is logged as EventId 21001 (Information) with its error code.

## 4. `SignInCompletionService.CompleteAsync`

Shared by the password sign-in and the SSO return.

```text
ISignInCompletionService.CompleteAsync(user, enteredLogin, attempt, options)   SignInCompletionService
  ├─ IRoleService.GetRoleByIdAsync(user.RoleID)
  │     none → ISignInAuditService.RecordAsync (RoleNotFound) → InvalidRole                 (21021)
  ├─ options.EnforcePasswordChange and user.ForcePasswordReset                             (password sign-in only)
  │     → ISignInAuditService.RecordAsync → PasswordChangeDestination(login, password token)  (section 6c)
  └─ SignInCompletionService.StartSessionAsync
      ├─ IAuthenticatedSessionService.StartAsync(user, role)   HttpAuthenticatedSessionService
      │   └─ ILoginService.LoginUser(httpContext, login, role name, ids)   LoginService
      │       ├─ LoginService.CreateClaimsPrincipal(userName, roleName)    Name, Role, and a new session-id claim
      │       ├─ SessionService.InitSession(sessionId, agency, user, login, role, corporation)
      │       │     a cached session for this login already exists → SimultaneousSignIn
      │       │     an exception while building it → also reported as SimultaneousSignIn
      │       │     otherwise builds the AppSession (branding, filters, agency, admin, SMTP, Foundry settings)
      │       │     and caches it by login
      │       ├─ HttpContext.Session.SetString(SessionIdentity.SessionKey, sessionId)
      │       ├─ HttpContext.SignInAsync(cookie scheme, principal)        → auth cookie
      │       └─ an exception → no session → SignInFailed                   (21022)
      ├─ SignInCompletionService.FinishSessionAsync
      │   ├─ options.ResumeCapacity                                        (password sign-in only)
      │   │   └─ ICapacityControlService.ResumeIfAllowedAsync   CapacityControlService
      │   │        ├─ ISettingsRepository.GetAsync("AppRegAllowControlCapacity")   not "true" → skipped
      │   │        ├─ IPowerBICapacityClient.Resume(appSession)   AzurePowerBICapacityClient
      │   │        └─ failure → ISignInAuditService.RecordAsync → CapacityControlFailed   (21023)
      │   ├─ SignInCompletionService.RecordSignedInAsync
      │   │   ├─ ISignInAuditService.RecordAsync (SignedIn)
      │   │   ├─ sets user.LastLoginDate
      │   │   ├─ UserInputSanitizer.ThrowExceptionOnScriptAttacks(user)
      │   │   └─ IUserRepository.UpdateUserAsync(user)
      │   └─ "\admin" was typed and the role is Agency Admin → AdminHomeDestination
      │      otherwise → ReportsHomeDestination
      └─ no destination, or an exception → IAuthenticatedSessionService.EndAsync(user)
            └─ LoginService.LogoutUserAsync   removes the AppSession, clears the ASP.NET session, signs out
```

A failed sign-in after the session start ends the session again, so it never leaves a live session behind.

## 5. Back to the browser

`SignInContractMapper.ToUrl` turns the destination into `nextUrl`:

| Destination | `nextUrl` |
|---|---|
| `ReportsHomeDestination` | `/Home` |
| `AdminHomeDestination` | `/Admin` |
| `IdentityProviderDestination` | `/auth/start`, plus `?tenant=` when the user has a tenant hint |
| `PasswordChangeDestination` | `/ChangePassword?u=<login>&t=<password token>` |
| `TwoFactorDestination` | `/Duo?requestSig=...&user=...` |

`SignInController.ToErrorResult` picks the status from the error code:

| Error code | Status |
|---|---|
| none (a destination) | 200 |
| `HumanVerificationFailed` | 400 |
| `InvalidCredentials`, `DomainUserInvalid` | 401 |
| `InvalidRole` | 403 |
| `SimultaneousSignIn` | 409 |
| everything else (directory, Duo setup, capacity, SSO, unexpected) | 500 |

What `api.signIn` returns to the page:

```text
api.signIn()
  ├─ 200 with a same-origin nextUrl → signedIn
  │     └─ LoginPage.submit() calls location.assign(nextUrl)
  ├─ error body with errorMessage → that message and humanVerificationRequired
  ├─ 400 validation problem → "Invalid Username or Password"
  └─ anything else (a refused antiforgery token, a nextUrl on another origin) → "Could not login user"
```

On a rejection `LoginPage.submit()` clears the password, shows reCAPTCHA if the server now requires it, calls `LoginPage.resetHumanVerification()`, and calls `LoginPage.showError()`, which shows the message inline and focuses the username field.

## 6. Where the follow-on addresses lead

### 6a. Staff accounts (`grasp\...`): LDAP, then Duo

```text
IStaffSignInService.SignInAsync(login, password, attempt)   StaffSignInService     (from section 3)
  ├─ ISettingsRepository.GetGlobalAsync
  │     Duo keys (DUO_TWOFACT_IKEY, SKEY, AKEY, SIGNATURE_KEY) and directory
  │     settings (ActiveDirectoryGroups, AuthenticationDomain, LDAPServers; LDAPServers blank → DC5, DC6)
  ├─ IEncryptionService.Decrypt for each Duo key
  ├─ IDirectoryCredentialClient.Validate(ldapServers, login, password)   LdapDirectoryCredentialClient
  │     Unavailable → DirectoryUnavailable;  Invalid → DomainUserInvalid
  ├─ ITwoFactorClient.SignRequest(keys, user)                            DuoTwoFactorClient
  │     user is the login without "grasp\"
  └─ TwoFactorDestination(requestSig, user)  → browser goes to /Duo
```

Each failure calls `ISignInAuditService.RecordAsync` and returns its own error code (EventIds 21031-21036).

```text
Browser GET /Duo?requestSig=...&user=...
  └─ DuoController.Index                     Razor view with the Duo frame

Duo frame POST /Duo (sig_response)
  └─ DuoController.Index
       ├─ DuoController.ValidateDuoResponse(sigResponse, user)
       │     └─ Duo.Web.VerifyResponse(ikey, skey, akey, sigResponse)
       │     fails → IAuditLogService.Create, "Unable to verify two-factor authentication"
       ├─ ILoginService.LoginUser(...)                 as Super Admin (agency -1, user 0)
       ├─ setting AppRegAllowControlCapacity is true → resumes the capacity through AzureManagement
       ├─ IAuditLogService.Create                      "Login Success"
       └─ 302 /SuperAdmin
```

### 6b. SSO-only users: OAuth round trip

```text
Browser GET /auth/start?tenant=...
  └─ MapSsoEndpointExtensions.StartAuth
       ├─ IOAuthAuthorizationUseCase.Execute(httpContext, request)
       └─ 302 to the identity provider

Identity provider redirects the browser to GET /auth/signin?code&state
  └─ MapSsoEndpointExtensions.SignIn
       ├─ IOAuthTokenExchangeUseCase.ExecuteAsync(httpContext, request)
       ├─ IMemoryCache.Set("success:<sid>", token response, 5 minutes)
       └─ 302 /validateOAuthSso?sid=<sid>

Browser GET /validateOAuthSso?sid=<sid>
  └─ SingleSignOnController.ValidateOAuthSsoAsync
       ├─ SingleSignOnController.TakeSingleSignOnLogin(sid)     the cached login, one use
       ├─ ICompleteSingleSignOnUseCase.ExecuteAsync(command)   CompleteSingleSignOnUseCase
       │   ├─ no cached login → SingleSignOnFailed
       │   ├─ IUserRepository.GetActiveUserByLoginAsync(login)
       │   │     none → ISignInCompletionService.RejectUnknownLoginAsync
       │   ├─ ISignInCompletionService.CompleteAsync(user, login, attempt, SingleSignOn)     (section 4)
       │   │     no forced password change, no capacity resume, always the reports home
       │   └─ rejected → IFailedSignInStore.RecordFailure(clientAddress)               (21011; unexpected 21012)
       ├─ success → 302 to the destination URL
       └─ failure → ISignInNoticeStore.Save(message), 302 /Login
```

After a failed SSO return the login page loads again, and `GET /api/sign-in` hands it the saved notice once (`SessionSignInNoticeStore.Take` reads it from the ASP.NET session and removes it). `GET /login/sso` (`SingleSignOnController.LoginSso`) is a separate Razor entry page for SSO (`GraspANALYTICS/Views/Login/LoginSso.cshtml`).

### 6c. Forced password change

```text
Browser GET /ChangePassword?u=...&t=...
  └─ ChangePasswordController.Index
       ├─ IValidateChangePasswordLinkUseCase.ExecuteAsync(login, passwordToken)
       │     invalid or expired → the page with the link error
       ├─ IHumanVerificationWidgetResolver.Resolve()
       └─ GraspANALYTICS/Views/ChangePassword/Index.cshtml   Vue island, src/public/change-password/main.ts
```

No session is started before the password is changed.

## 7. When reCAPTCHA is required

`MemoryFailedSignInStore` (singleton, in memory) keeps markers for 15 minutes:

- every rejected sign-in records a marker for the **client address** and one for the **account name** (case-insensitive); a failed reCAPTCHA or SSO return records the address only
- reCAPTCHA is required when either marker exists (the page-state call only knows the address, so it checks the address)
- a sign-in that verified credentials clears both markers
- if the store fills up (100,000 markers), every sign-in requires reCAPTCHA for 15 minutes

The check runs server-side (`ReCaptchaHumanVerificationClient.VerifyAsync`), so the browser cannot skip it. An app restart clears all markers.

## 8. Every request after sign-in

```text
Browser any request
  ├─ UseAuthentication                 reads the auth cookie → HttpContext.User (Name, Role, session-id claim)
  ├─ UseSession                        the ASP.NET session (holds SessionIdentity.SessionKey)
  └─ SessionValidationMiddleware.Invoke
       ├─ signed in, but the session has no session key  → signs out; API: 401 JSON "Session expired", page: 302 /Login
       ├─ claim does not match the session key           → signs out; API: 401 JSON, page: 302 /Forbidden/Index?errorGuid=...
       └─ otherwise continues
```

The app session itself (`AppSession`) lives in `SessionService`'s memory cache, keyed by login. That cache entry is what makes a second sign-in for the same login fail with `SimultaneousSignIn` until the first session ends, and it is lost on an app restart.

## 9. What gets logged

- **Audit log** (`AuditLog` via `ISystemEventLogRepository.CreateAuditLogAsync`): every rejection with its reason (`SignInAuditReasons`), the forced password change, and every successful sign-in, with address, page, version, login, user, role, and agency.
- **Application log**: `SignInUseCase` 21001 (rejected, Information) and 21002 (unexpected failure); `CompleteSingleSignOnUseCase` 21011 and 21012; `SignInCompletionService` 21021 and 21022; `CapacityControlService` 21023; `StaffSignInService` 21031-21036. See Logging-EventId-Registry.md.
