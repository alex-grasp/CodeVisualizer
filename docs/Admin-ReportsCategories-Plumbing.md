---
summary: How the Admin Report tab and the Admin Categories tab work end to end - the Vue islands, the admin reports and categories APIs, the Core services and repositories, the ganReport, ganReportToAgency, ganReportCategory, and ganCategory tables, typed write results, and the effect on the Home report list; read before changing either tab.
area: architecture
---

# Admin Reports and Categories plumbing

This doc follows an agency admin through the Admin page's **Report** tab (rename a report, pick its category, turn it on or off) and **Categories** tab (create, rename, activate, and delete the agency's own categories). It names what connects to what and what calls what. For the Admin page shell, the tab strip, the unsaved-changes guard, and the session check on a tab switch, see Admin-Plumbing.md.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Admin tab host | `ClientApp/src/admin/home/AdminHome.vue` | Renders `<AdminReports />` in the `reports` tab (label "Report") and `<AdminCategories />` in the `categories` tab. Calls `adminReportsHasUnsavedChanges()` and `adminReportsGuardSwitch()` before leaving the Report tab. |
| Admin page host | `Views/Admin/Index.cshtml` | `@Html.AntiForgeryToken()` and `#admin-home-app`. The hidden `__RequestVerificationToken` field is what every write sends. |
| Report tab component | `ClientApp/src/admin/reports/AdminReports.vue` | The reports grid: `load()`, `startNameEdit()`, `submitNameEdit()`, `commitRename()`, `onCategoryChange()`, `onToggleActive()`, `guardSwitch()`, `categoryOptionsFor()`. |
| Report tab API client | `ClientApp/src/admin/reports/api.ts` | `getAgencyReports()`, `getAgencyCategories()`, `updateReportCategory()`, `renameReport()`, `setReportActive()`. |
| Report tab types | `ClientApp/src/admin/reports/types.ts` | `ReportRow` (camelCase) and `CategoryOption` (PascalCase). |
| Report tab tests | `ClientApp/src/admin/reports/AdminReports.spec.ts` | Vitest: load failure logs to the server and shows the ErrorID; success does not log. |
| Categories tab component | `ClientApp/src/admin/categories/AdminCategories.vue` | The categories grid and dialogs: `load()`, `add()`, `rename()`, `toggleActive()`, `remove()`, `validateCategoryName()`, `notifyChanged()`. |
| Categories tab API client | `ClientApp/src/admin/categories/api.ts` | `getManageableCategories()`, `createCategory()`, `renameCategory()`, `toggleCategoryActive()`, `deleteCategory()`. |
| Categories tab types | `ClientApp/src/admin/categories/types.ts` | `AdminCategory` (PascalCase) and `CategoryWriteResult` (camelCase). |
| Shared HTTP | `ClientApp/src/shared/http.ts` + `ClientApp/src/shared/antiforgery.ts` | `requestJson()`, `sendJson()` (adds `RequestVerificationToken`), `errorMessage()`, `antiforgeryToken()`. |
| Shared dialogs and logging | `ClientApp/src/shared/dialogs.ts` + `ClientApp/src/shared/clientLogger.ts` | `handleNullSession()`, `showOk()`, `escapeHtml()`, `logToServer()`. |
| Reports API | `Controllers/Api/Admin/AdminReportsController.cs` | `GET api/admin/reports`, `PATCH api/admin/reports/{id}`, `PATCH api/admin/reports/{id}/active`. |
| Categories API | `Controllers/Api/Admin/AdminCategoriesController.cs` | `GET api/admin/categories`, `GET api/admin/categories/manage`, `GET api/admin/reports/{reportId}/categories`, `GET api/admin/reports/{reportId}/categories/available`, `POST api/admin/categories`, `PATCH api/admin/categories/{id}`, `PATCH api/admin/categories/{id}/active`, `POST api/admin/reports/{reportId}/categories/{categoryId}`, `DELETE api/admin/categories/{id}`. |
| Session filter | `Classes/CustomAuthorizeAttribute.cs` | `OnAuthorization()`: no app session → `ExpiredLoginResult` (401); duplicate login with `LastLoginWins` → `DuplicateLoginResult` (460). |
| Output formatter | `Helpers/CaseInsensitiveJsonAttribute.cs` | Replaces the output formatter with default `System.Text.Json` options, so properties without `[JsonPropertyName]` come out PascalCase. |
| Report contracts | `Api/Reports/Contracts/RenameReportRequest.cs` + `ReportListItemResponse.cs` + `ReportWriteResponse.cs` + `ReportContractMapper.cs` | Request and response records; `ToResponse()`, `ToResponseList()`. |
| Report active request | `GraspANALYTICS.Domain/Models/Reports/SetReportActiveRequest.cs` | `{ IsActive }`, bound by the controller even though it lives in Domain. |
| Category contracts | `Api/Categories/Contracts/CreateCategoryRequest.cs` + `RenameCategoryRequest.cs` + `CategoryWriteResponse.cs` + `CategoryContractMapper.cs` | Request and response records; `ToResponse()`. |
| Reports service | `GraspANALYTICS.Core/Application/Admin/Reports/AdminReportsService.cs` (`IAdminReportsService` in `GraspANALYTICS.Domain/Services/`) | `GetAgencyReportsAsync()`, `RenameReportAsync()`, `SetReportActiveAsync()`. |
| Categories service | `GraspANALYTICS.Core/Application/Admin/Categories/AdminCategoryService.cs` (`IAdminCategoryService` in `GraspANALYTICS.Domain/Services/`) | `GetCategoriesAsync()`, `GetManageableCategoriesAsync()`, `GetReportCategoriesAsync()`, `GetAvailableCategoriesForReportAsync()`, `CreateCategoryAsync()`, `RenameCategoryAsync()`, `ToggleCategoryActiveAsync()`, `AssignCategoryToReportAsync()`, `DeleteCategoryAsync()`. |
| Selected agency | `GraspANALYTICS.Core/Application/Agencies/AgenciesService.cs` | `GetSelectedAgencyIdAsync()`: the session user's `AgencyID`, else the `SuperAdminAgencyID` memory-cache entry, else 0. |
| Agency reports repository | `GraspANALYTICS.Core/Application/AgencyReports/Repos/AgencyReportsRepository.cs` | `GetAssignedAgencyReportsAsync()`, `IsReportInAgencyAsync()`, `GetReportAsync()`, `UpdateReportAsync()`. |
| Category repository | `GraspANALYTICS.Core/Application/CategoriesAndReports/Repos/CategoryRepository.cs` | `GetAsync()`, `GetActiveByAgencyScopeAsync()`, `GetByAgencyScopeAsync()`, `GetOwnedByAgencyAsync()`, `GetByIdsAsync()`, `CreateAsync()`, `UpdateAsync()`, `DeleteAsync()`. |
| Report-category repository | `GraspANALYTICS.Core/Application/CategoriesAndReports/Repos/ReportCategoryRepository.cs` | `GetForAgencyScopeAsync()`, `GetAssignedCategoryIdsAsync()`, `ExistsForAgencyAsync()`, `CreateAsync()`, `DeleteForAgencyExceptCategoryAsync()`, `DeleteForCategoryAndAgencyAsync()`. |
| Write results | `GraspANALYTICS.Domain/Models/Reports/ReportWriteResult.cs` + `ReportWriteErrorCode.cs` + `ReportWriteErrors.cs`; `GraspANALYTICS.Domain/Models/Categories/CategoryWriteResult.cs` + `CategoryWriteErrorCode.cs` + `CategoryWriteErrors.cs` | Typed success or failure with an error code and a fixed message. |
| Name rule | `GraspANALYTICS.Domain/Models/Categories/CategoryNamePattern.cs` | `^[a-zA-Z0-9\s_.&/()',-]+$`, shared by the contracts and the service. |
| Tables | `GraspANALYTICS.Data/Models/Report.cs` → `ganReport`; `ReportToAgency.cs` → `ganReportToAgency`; `ReportCategory.cs` → `ganReportCategory`; `Category.cs` → `ganCategory` | The data model below. |
| DI | `CompositionRoot/GraspApplicationServicesExtensions.cs` | `IAdminCategoryService` → `AdminCategoryService`, `IAdminReportsService` → `AdminReportsService`, both scoped. |

## The data model

| Table | Key columns | Meaning |
|---|---|---|
| `ganReport` | `ReportID`, `PowerBIReportID`, `PowerBIName`, `Name`, `TMCOnly`, `DatasetID`, `IsActive`, `IsDeleted` | One row per Power BI report. It has **no** `AgencyID`: the row is shared by every agency the report is assigned to. `ReportName` (not mapped) is `Name`, or `PowerBIName` when `Name` is blank. |
| `ganReportToAgency` | `AgencyID`, `ReportID` | Which reports an agency has. Maintained by Super Admin (`AgencyReportsRepository.AddReportToAgencyAsync` / `RemoveReportFromAgencyAsync`), not by these tabs. |
| `ganCategory` | `CategoryID`, `AgencyID`, `Name`, `IsActive` | `AgencyID = 0` is a global (system default) category; any other value is owned by that agency. |
| `ganReportCategory` | `ReportID`, `CategoryID`, `AgencyID` | Places a report in a category. `AgencyID = 0` is the global placement; an agency row overrides it for that agency. `CategoryID = 0` is the placeholder row `AddReportToAgencyAsync` writes when a report is added to an agency; every reader skips it (`CategoryID > 0`). |
| `ganUserReport` | `UserID`, `ReportID`, `IsDefault` | Which reports a user sees on Home. Neither tab reads or writes it; the Users tab does. |

## Part A - the Report tab

## 1. Loading the reports grid

```text
Browser opens the "Report" tab (AdminHome.vue)
  └─ AdminReports.load()                       also runs on mount and on "admin:categories-changed"
       ├─ api.getAgencyReports()      → GET /api/admin/reports
       └─ api.getAgencyCategories()   → GET /api/admin/categories?isSystemCategory=false
       then
       ├─ both succeed → reports and categories set, grid renders
       └─ either throws → dialogs.handleNullSession(error)
                          loadError "Unable to load reports."
                          clientLogger.logToServer("Reports", "AdminReports load failed: <message>")
                          dialogs.showOk("Error", "Admin Reports grid datasource error. ... ErrorID = <id>")
```

On the server:

```text
AdminReportsController.GetReportsAsync        [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [CaseInsensitiveJson]
  └─ AdminReportsService.GetAgencyReportsAsync
       ├─ AgenciesService.GetSelectedAgencyIdAsync() → agencyId
       ├─ agencyId <= 0 → empty list
       ├─ IAgencyReportsRepository.GetAssignedAgencyReportsAsync(agencyId)   AgencyReportsRepository
       │     ganReportToAgency rows for the agency → ganReport rows, ordered by ReportID
       │     active, inactive, and deleted reports are all returned
       ├─ Company Admin or Company User role → TMCOnly reports dropped
       ├─ IReportCategoryRepository.GetForAgencyScopeAsync(agencyId)   ReportCategoryRepository
       │     ganReportCategory with CategoryID > 0 and AgencyID = agencyId or 0
       │     per report the row with the higher AgencyID wins (agency over global)
       └─ maps each report to ReportListItemDto (Name = ReportName)
  └─ ReportContractMapper.ToResponseList → 200
```

The category list for the per-row picker:

```text
AdminCategoriesController.GetCategoriesAsync      same attributes; query isSystemCategory (default false)
  └─ AdminCategoryService.GetCategoriesAsync(isSystemCategory: false)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() → agencyId
       ├─ ICategoryRepository.GetActiveByAgencyScopeAsync(agencyId)   CategoryRepository
       │     active ganCategory rows with AgencyID = 0 or agencyId, ordered by Name
       └─ AdminCategoryService.ToListItem   IsSystemCategory = AgencyID is 0;
                                            Label "(System Default)" on global rows
  └─ 200, PascalCase CategoryListItemDto[] (no [JsonPropertyName] on the record)
```

- `isSystemCategory=true` scopes to agency 0, so only global categories come back and no `Label` is set. The Report tab never sends it.
- The report rows are camelCase (`ReportListItemResponse` has `[JsonPropertyName]`), the category rows are PascalCase (`Id`, `Name`, `IsActive`, `IsSystemCategory`, `Label`). `types.ts` matches both.
- `AdminReports.filteredReports` filters client-side on name, Power BI name and id, category id, dataset id, and the active and deleted flags. The grid pages 25 rows by default and sorts by Report ID.

### What the admin sees and can change

- **Which reports**: every report in `ganReportToAgency` for the selected agency, including inactive and deleted ones. A Company Admin does not see `TMCOnly` reports.
- **Which categories**: `AdminReports.categoryOptionsFor(row)` offers the agency's active categories, plus a global one only when it is the row's current category. A global category cannot be picked in the UI, but the server accepts one (section 3).
- A report whose current category is inactive has no matching option, so the picker shows the placeholder.
- **TMC Only** is read-only here.

## 2. Renaming a report

```text
Browser click a report name (AdminReports.vue)
  └─ AdminReports.startNameEdit(row)
       ├─ another row is in edit → AdminReports.commitRename(previousId, previousName)
       └─ opens the inline input (maxlength 150)
Browser presses Enter, or the input loses focus for 150 ms (AdminReports.vue)
  └─ AdminReports.submitNameEdit()
       └─ AdminReports.commitRename(reportId, name)
            ├─ name unchanged → nothing sent
            └─ api.renameReport(reportId, name)   → PATCH /api/admin/reports/{id}  { name }
                 ├─ success → toast "Report name updated.", AdminReports.load()
                 └─ failure → dialogs.handleNullSession(error), error toast with the server message
                              or "Unable to update report name."
Browser presses Esc (AdminReports.vue)
  └─ AdminReports.cancelNameEdit()
```

On the server:

```text
AdminReportsController.RenameReportAsync      [ValidateAntiForgeryToken], [CaseInsensitiveJson], [ApiController] model validation
  ├─ Name longer than 150 → 400 validation problem
  └─ AdminReportsService.RenameReportAsync(id, request.Name)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ AdminReportsService.GetAgencyReportAsync(reportId, agencyId)
       │     IAgencyReportsRepository.IsReportInAgencyAsync   ganReportToAgency
       │     IAgencyReportsRepository.GetReportAsync          ganReport
       │     not in the agency or missing → NotFound → 404
       ├─ ganReport.Name = name, or "" when null
       ├─ UserInputSanitizer.ContainsScriptAttack(report) → InvalidInput → 400 "Name contains invalid content."
       ├─ IAgencyReportsRepository.UpdateReportAsync(report)
       ├─ AdminReportsService.WriteAuditLogAsync("Renamed report {id} to {name}")   Page "Admin/Reports"
       └─ ReportWriteResult.Ok() → 200 { success: true, errorMessage: null }
```

- The name is not trimmed and may be empty. An empty `Name` makes `ReportName` fall back to `PowerBIName`.
- The write updates the shared `ganReport` row, so the new name shows for **every** agency that has the report, on Admin and on Home.

### Leaving the tab with an unsaved name

```text
Browser click another tab (AdminHome.vue)
  └─ AdminHome.onTabSelect(value)                     see Admin-Plumbing.md
       └─ AdminReports.guardSwitch()   via globalThis.adminReportsGuardSwitch
            ├─ AdminReports.hasUnsavedChanges() false → proceed
            └─ shows "Unsaved changes" dialog
                 ├─ Browser click "Save"    → AdminReports.saveAndLeave() → AdminReports.commitRename(...)
                 ├─ Browser click "Discard" → AdminReports.discardAndLeave()
                 └─ Browser click "Cancel"  → AdminReports.settleGuard(false), stay on the tab
```

## 3. Choosing a report's category

```text
Browser picks a category in a row (AdminReports.vue)
  └─ AdminReports.onCategoryChange(row, categoryId)
       ├─ not a number or unchanged → nothing sent
       ├─ row.categoryId set optimistically
       └─ api.updateReportCategory(reportId, categoryId)   → POST /api/admin/reports/{reportId}/categories/{categoryId}
            ├─ success → toast "Report category updated.", AdminReports.load()
            └─ failure → row.categoryId restored, dialogs.handleNullSession(error), error toast
```

On the server:

```text
AdminCategoriesController.AssignCategoryToReportAsync     [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ AdminCategoryService.AssignCategoryToReportAsync(reportId, categoryId)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ IAgencyReportsRepository.IsReportInAgencyAsync(reportId, agencyId) false
       │     → NotFound "Report not found for agency." → 404
       ├─ ICategoryRepository.GetAsync(categoryId)
       │     missing, or owned by another agency → NotFound "Category is not accessible for agency." → 404
       ├─ category not active → CategoryInactive → 409
       ├─ IReportCategoryRepository.ExistsForAgencyAsync(reportId, categoryId, agencyId) false
       │     → IReportCategoryRepository.CreateAsync   ganReportCategory with AgencyID = agencyId
       ├─ IReportCategoryRepository.DeleteForAgencyExceptCategoryAsync(reportId, agencyId, categoryId)
       │     removes the agency's other rows for the report, including the CategoryID 0 placeholder
       ├─ AdminCategoryService.WriteAuditLogAsync("Assigned category {categoryId} to report {reportId}")
       └─ CategoryWriteResult.Ok() → 200 { success: true, errorMessage: null }
```

- A report has at most one agency row after this call. The global row (`AgencyID = 0`) is never touched; the agency row overrides it.
- The insert and the delete are two separate writes, not one transaction (the service carries a `REFACTOR` marker for this).
- `GET api/admin/reports/{reportId}/categories` and `.../categories/available` (`AdminCategoryService.GetReportCategoriesAsync` and `GetAvailableCategoriesForReportAsync`) return the report's linked categories and the active categories it is not linked to, in the agency-plus-global scope. Neither tab calls them.

## 4. Turning a report on or off

```text
Browser flips the "Active" switch (AdminReports.vue)
  └─ AdminReports.onToggleActive(row, value)
       ├─ row.isActive set optimistically
       └─ api.setReportActive(reportId, value)   → PATCH /api/admin/reports/{id}/active  { isActive }
            ├─ success → toast "Report is now active." or "inactive.", AdminReports.load()
            └─ failure → row.isActive restored, dialogs.handleNullSession(error), error toast
```

On the server:

```text
AdminReportsController.SetReportActiveAsync      [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ AdminReportsService.SetReportActiveAsync(id, request.IsActive)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ AdminReportsService.GetAgencyReportAsync(reportId, agencyId) null → NotFound → 404
       ├─ ganReport.IsActive = isActive
       ├─ IAgencyReportsRepository.UpdateReportAsync(report)
       ├─ AdminReportsService.WriteAuditLogAsync("Set report {id} active state to {isActive}")
       └─ ReportWriteResult.ActiveChanged(isActive) → 200 { success: true, errorMessage: null, isActive }
```

- Like the rename, this changes the shared `ganReport` row for every agency.
- `IsActive` controls which reports the Users tab can assign: `AdminGetUserUseCase` lists reports through `AgencyReportsRepository.GetSelectedAgencyReportsAsync`, which keeps only active ones.

## Part B - the Categories tab

## 5. Loading the categories grid

```text
Browser opens the "Categories" tab (AdminHome.vue)
  └─ AdminCategories.load()                   runs on mount and after every successful write
       └─ api.getManageableCategories()   → GET /api/admin/categories/manage
            ├─ success → grid rows; the selection is cleared when its row is gone
            └─ failure → dialogs.handleNullSession(error), error message in place of the grid
```

On the server:

```text
AdminCategoriesController.GetManageableCategoriesAsync    [Authorize(Roles = "Agency Admin,Super Admin,Company Admin")], [CustomAuthorizeAttribute], [CaseInsensitiveJson]
  └─ AdminCategoryService.GetManageableCategoriesAsync
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → empty list
       ├─ ICategoryRepository.GetOwnedByAgencyAsync(agencyId)
       │     ganCategory rows with AgencyID = agencyId, active and inactive, ordered by Name
       └─ AdminCategoryService.ToListItem(category, isSystemCategory: false)
  └─ 200, PascalCase CategoryListItemDto[]
```

- Global categories (`AgencyID = 0`) never appear in this grid; an agency admin cannot rename, toggle, or delete them.
- The grid has single selection. Rename, Activate or Deactivate, and Delete act on the selected row.
- The tab sends nothing to `/api/ClientLogger`; failures are shown only as toasts or the error message.

## 6. Creating a category

```text
Browser click "New Category", then "Add" or Enter (AdminCategories.vue)
  └─ AdminCategories.add()
       ├─ AdminCategories.validateCategoryName(trimmed name)
       │     blank → "Enter a category name."
       │     fails the name pattern → the allowed-characters message
       └─ api.createCategory(name)   → POST /api/admin/categories  { name, isSystemCategory: false }
            ├─ success: false in the body → AdminCategories.requireSuccess throws
            ├─ success → dialog closed, AdminCategories.load(), AdminCategories.notifyChanged(),
            │            toast "Category added."
            └─ failure → AdminCategories.notifyError(error, "Unable to add category.")
```

On the server:

```text
AdminCategoriesController.CreateCategoryAsync      [ValidateAntiForgeryToken], [CaseInsensitiveJson], [ApiController] model validation
  ├─ Name missing, empty, over 100, or fails CategoryNamePattern; IsSystemCategory missing → 400 validation problem
  └─ AdminCategoryService.CreateCategoryAsync(request.Name, request.IsSystemCategory)
       ├─ trimmed name blank → InvalidInput "Name is required." → 400
       ├─ AdminCategoryService.IsValidCategoryName false → InvalidInput → 400
       ├─ isSystemCategory true → SystemCategoryNotAllowed → 403
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ AdminCategoryService.NameTakenAsync(agencyId, name)
       │     ICategoryRepository.GetByAgencyScopeAsync   agency and global rows, active or not
       │     same name ignoring case → DuplicateName → 409
       ├─ UserInputSanitizer.ContainsScriptAttack(category) → InvalidInput → 400
       ├─ ICategoryRepository.CreateAsync   ganCategory with AgencyID = agencyId, IsActive true
       ├─ AdminCategoryService.WriteAuditLogAsync("Created category {name}")   Page "Admin/Categories"
       └─ CategoryWriteResult.Created(categoryId) → 201 { success: true, errorMessage: null, categoryId }
```

- An agency category cannot reuse the name of a global category or of any of the agency's own categories, inactive ones included.
- The 201 has no `Location` header.

## 7. Renaming a category

```text
Browser click "Rename", then "Save" or Enter (AdminCategories.vue)
  └─ AdminCategories.rename()
       ├─ AdminCategories.validateCategoryName(trimmed name)
       └─ api.renameCategory(categoryId, name)   → PATCH /api/admin/categories/{id}  { name }
            ├─ success → dialog closed, AdminCategories.load(), selection kept,
            │            AdminCategories.notifyChanged(), toast "Category renamed."
            └─ failure → AdminCategories.notifyError(error, "Unable to rename category.")
```

On the server:

```text
AdminCategoriesController.RenameCategoryAsync      [ValidateAntiForgeryToken], [CaseInsensitiveJson], [ApiController] model validation
  └─ AdminCategoryService.RenameCategoryAsync(id, request.Name)
       ├─ trimmed name blank or invalid → InvalidInput → 400
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ AdminCategoryService.GetOwnedCategoryAsync(categoryId, agencyId)
       │     missing, global, or another agency's → NotFound → 404
       ├─ same name, exact case → Ok, nothing written → 200
       ├─ AdminCategoryService.NameTakenAsync(agencyId, name, excluding this category) → DuplicateName → 409
       ├─ UserInputSanitizer.ContainsScriptAttack(category) → InvalidInput → 400
       ├─ ICategoryRepository.UpdateAsync(category)
       ├─ AdminCategoryService.WriteAuditLogAsync("Renamed category {id} to {name}")
       └─ CategoryWriteResult.Ok() → 200
```

A case-only change (for example "sales" to "Sales") passes, because the duplicate check skips the category itself.

## 8. Activating or deactivating a category

```text
Browser click "Activate" or "Deactivate" (AdminCategories.vue)
  └─ AdminCategories.toggleActive()
       └─ api.toggleCategoryActive(categoryId)   → PATCH /api/admin/categories/{id}/active   (no body)
            ├─ success → AdminCategories.load(), AdminCategories.notifyChanged(),
            │            toast "Category active state updated."
            └─ failure → AdminCategories.notifyError(error, "Unable to toggle category active state.")
```

On the server:

```text
AdminCategoriesController.ToggleCategoryActiveAsync     [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ AdminCategoryService.ToggleCategoryActiveAsync(id)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ AdminCategoryService.GetOwnedCategoryAsync(categoryId, agencyId) null → NotFound → 404
       ├─ flips ganCategory.IsActive
       ├─ ICategoryRepository.UpdateAsync(category)
       ├─ AdminCategoryService.WriteAuditLogAsync("Toggled category {id} active state to {isActive}")
       └─ CategoryWriteResult.Toggled(isActive) → 200 { success: true, errorMessage: null, isActive }
```

- The endpoint flips the stored value; it does not set a requested one.
- An inactive category drops out of the Report tab picker and cannot be assigned (`CategoryInactive`). Existing `ganReportCategory` links to it stay.

## 9. Deleting a category

```text
Browser click "Delete", then "Delete" in the confirm dialog (AdminCategories.vue)
  └─ AdminCategories.remove()
       └─ api.deleteCategory(categoryId)   → DELETE /api/admin/categories/{id}
            ├─ success → selection cleared, AdminCategories.load(), AdminCategories.notifyChanged(),
            │            toast "Category deleted."
            └─ failure → AdminCategories.notifyError(error, "Unable to delete category.")
```

On the server:

```text
AdminCategoriesController.DeleteCategoryAsync      [ValidateAntiForgeryToken], [CaseInsensitiveJson]
  └─ AdminCategoryService.DeleteCategoryAsync(id)
       ├─ AgenciesService.GetSelectedAgencyIdAsync() <= 0 → AgencyNotSelected → 400
       ├─ ICategoryRepository.GetAsync(categoryId) null → NotFound → 404
       ├─ AgencyID is 0 → SystemCategoryNotAllowed → 403
       ├─ AgencyID is another agency → NotFound → 404
       ├─ ICategoryRepository.DeleteAsync(categoryId, agencyId)        ganCategory row removed
       ├─ IReportCategoryRepository.DeleteForCategoryAndAgencyAsync(categoryId, agencyId)
       │     the agency's ganReportCategory rows for the category removed
       ├─ AdminCategoryService.WriteAuditLogAsync("Deleted category {name}")
       └─ 204 no body
```

- The delete is permanent, and the two deletes are separate writes.
- A report that loses its agency row falls back to its global row, if it has one. If it has none, it has no category for that agency.

### Keeping the Report tab in step

`AdminCategories.notifyChanged()` dispatches `admin:categories-changed` on `globalThis` after every successful create, rename, toggle, and delete. `AdminReports.vue` listens for it while mounted and calls `AdminReports.load()`, so its category picker and the grid's category column are refreshed.

## 10. Effect on the Home report list

Home builds its list from `ganUserReport`, `ganReportCategory`, `ganCategory`, and `ganReport`; see Home-ReportViewer-Plumbing.md §1. Against that path:

| Admin change | Effect on Home |
|---|---|
| Rename a report | The new `ReportName` shows for every user who has the report, in every agency that has it. |
| Assign a category | The report moves under that category for the agency's users; the agency row beats the global row. |
| Delete a category | Its reports lose the agency placement. They show under the global category when there is a global row, and are left out of the Home list when there is none. |
| Rename a category | The category heading changes for the agency's users. |
| Deactivate a category | No change. Home's `CategoryRepository.GetAsync` does not check `IsActive`. |
| Turn a report off | No change. Home checks `IsDeleted`, not `IsActive`. It only stops the Users tab from offering the report. |

Which users see a report at all is decided by `ganUserReport`, which neither tab changes.

## 11. Typed write results and errors

Both services return a result record instead of throwing. The controller maps the error code to a status and returns the same body shape on success and failure.

`ReportWriteResult` (`Success`, `ErrorMessage`, `IsActive`, `ErrorCode`), mapped by `AdminReportsController.ToErrorResult`:

| `ReportWriteErrorCode` | Status | `errorMessage` |
|---|---|---|
| `NotFound` | 404 | "Report not found for agency." |
| `InvalidInput` | 400 | "Name contains invalid content." |
| `AgencyNotSelected` | 400 | "No agency is currently selected." |

`CategoryWriteResult` (`Success`, `ErrorMessage`, `CategoryId`, `IsActive`, `ErrorCode`), mapped by `AdminCategoriesController.ToErrorResult`:

| `CategoryWriteErrorCode` | Status | `errorMessage` |
|---|---|---|
| `InvalidInput` | 400 | "Name is required.", "Category names can only include letters, numbers, spaces, underscores, dashes, and periods.", or "Name contains invalid content." |
| `DuplicateName` | 409 | "A category with that name already exists." |
| `CategoryInactive` | 409 | "Category is not active." |
| `SystemCategoryNotAllowed` | 403 | "System categories cannot be managed here." |
| `NotFound` | 404 | "Category not found.", "Report not found for agency.", or "Category is not accessible for agency." |
| `AgencyNotSelected` | 400 | "No agency is currently selected." |

- The response records (`ReportWriteResponse`, `CategoryWriteResponse`) are camelCase and omit `isActive` and `categoryId` when null.
- Model-validation failures from `[ApiController]` return a 400 validation problem with no `errorMessage`, so the client shows its fallback text.
- `http.errorMessage()` reads `errorMessage`, then `message`. `AdminReports.serverMessage()` also accepts a plain string body.
- `CategoryNamePattern` allows `& / ( ) ' ,`; the client message lists them, the server's `InvalidNameCharacters` message does not.

## 12. Validation summary

| Rule | Client | Contract | Service |
|---|---|---|---|
| Report name length | `maxlength="150"` | `RenameReportRequest.Name` `[StringLength(150)]` | none |
| Report name content | none | none | `UserInputSanitizer.ContainsScriptAttack` |
| Category name required | `AdminCategories.validateCategoryName` | `[Required(AllowEmptyStrings = false)]` | trimmed, `NameRequired` |
| Category name length | `maxlength="100"` | `[StringLength(100)]` | `IsValidCategoryName` (100) |
| Category name characters | `validCategoryName` regex | `[RegularExpression(CategoryNamePattern.Value)]` | `CategoryNameRegex()` (1000 ms timeout) |
| Category name unique | none | none | `NameTakenAsync`, case-insensitive, agency plus global |
| Category content | none | none | `UserInputSanitizer.ContainsScriptAttack` |
| Global category writes | not offered | none | `SystemCategoryNotAllowed` or `NotFound` |
| Report and category ownership | none | none | `IsReportInAgencyAsync`, `GetOwnedCategoryAsync`, `AgencyID` checks |

## 13. Logging and audit

- Neither controller nor service writes an `ILogger` line, so there are no EventIds on these paths (see `Logging-EventId-Registry.md`).
- Every successful write adds one `AuditLog` row through `ISystemEventLogRepository.CreateAuditLogAsync`, with `Page` "Admin/Reports" or "Admin/Categories", the user's `Login`, `UserID`, `RoleID`, the `AgencyID`, `Success` true, and a `Reason` such as "Renamed report 12 to Sales". Failed writes are not audited.
- A Report tab load failure goes through `clientLogger.logToServer("Reports", ...)` → `POST /api/ClientLogger` → `ClientLoggerController.LogClientErrorAsync` → `IAnalyticsLogger.LogError` "Client[{ModuleName}] Exception {Message}. ErrorID={ErrorId}". The ErrorID is shown in the error dialog.

## 14. Endpoints

All calls go through `shared/http.ts`. Every write sends the `RequestVerificationToken` header from the page's `__RequestVerificationToken` field and is checked by `[ValidateAntiForgeryToken]`. Every endpoint needs one of the roles Agency Admin, Super Admin, or Company Admin and a live app session (`CustomAuthorizeAttribute`: 401 when it has expired, 460 on a duplicate login).

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `GET /api/admin/reports` | `AdminReportsController.GetReportsAsync → AdminReportsService.GetAgencyReportsAsync` | - | camelCase `ReportListItemResponse[]`: `reportId`, `powerBiReportId`, `name`, `powerBiName`, `categoryId`, `datasetId`, `tmcOnly`, `isActive`, `isDeleted` | 200; 401; 460 |
| `PATCH /api/admin/reports/{id}` | `AdminReportsController.RenameReportAsync → AdminReportsService.RenameReportAsync` | `{ name }` (`RenameReportRequest`) | `{ success, errorMessage }` | 200; 400; 404; 401; 460 |
| `PATCH /api/admin/reports/{id}/active` | `AdminReportsController.SetReportActiveAsync → AdminReportsService.SetReportActiveAsync` | `{ isActive }` (`SetReportActiveRequest`) | `{ success, errorMessage, isActive }` | 200; 400; 404; 401; 460 |
| `GET /api/admin/categories?isSystemCategory=` | `AdminCategoriesController.GetCategoriesAsync → AdminCategoryService.GetCategoriesAsync` | query, default `false` | PascalCase `CategoryListItemDto[]`: `Id`, `Name`, `IsActive`, `IsSystemCategory`, `Label` (active only) | 200; 401; 460 |
| `GET /api/admin/categories/manage` | `AdminCategoriesController.GetManageableCategoriesAsync → AdminCategoryService.GetManageableCategoriesAsync` | - | PascalCase `CategoryListItemDto[]`, the agency's own, active and inactive | 200; 401; 460 |
| `GET /api/admin/reports/{reportId}/categories` | `AdminCategoriesController.GetReportCategoriesAsync → AdminCategoryService.GetReportCategoriesAsync` | route | PascalCase `ReportCategoryDto[]`: `CategoryId`, `Name`, `IsSystemCategory` | 200; 401; 460 |
| `GET /api/admin/reports/{reportId}/categories/available` | `AdminCategoriesController.GetAvailableReportCategoriesAsync → AdminCategoryService.GetAvailableCategoriesForReportAsync` | route | PascalCase `ReportCategoryDto[]` | 200; 401; 460 |
| `POST /api/admin/categories` | `AdminCategoriesController.CreateCategoryAsync → AdminCategoryService.CreateCategoryAsync` | `{ name, isSystemCategory }` (`CreateCategoryRequest`) | `{ success, errorMessage, categoryId }` | 201; 400; 403; 409; 401; 460 |
| `PATCH /api/admin/categories/{id}` | `AdminCategoriesController.RenameCategoryAsync → AdminCategoryService.RenameCategoryAsync` | `{ name }` (`RenameCategoryRequest`) | `{ success, errorMessage }` | 200; 400; 404; 409; 401; 460 |
| `PATCH /api/admin/categories/{id}/active` | `AdminCategoriesController.ToggleCategoryActiveAsync → AdminCategoryService.ToggleCategoryActiveAsync` | no body | `{ success, errorMessage, isActive }` | 200; 400; 404; 401; 460 |
| `POST /api/admin/reports/{reportId}/categories/{categoryId}` | `AdminCategoriesController.AssignCategoryToReportAsync → AdminCategoryService.AssignCategoryToReportAsync` | route | `{ success, errorMessage }` | 200; 400; 404; 409; 401; 460 |
| `DELETE /api/admin/categories/{id}` | `AdminCategoriesController.DeleteCategoryAsync → AdminCategoryService.DeleteCategoryAsync` | route | no body | 204; 400; 403; 404; 401; 460 |
| `POST /api/ClientLogger` | `ClientLoggerController.LogClientErrorAsync → IAnalyticsLogger.LogError` | `{ moduleName, message, errorId }` (`ClientErrorModel`) | `{ status: "success" }` | 200; 401 |
