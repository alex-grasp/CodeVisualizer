---
summary: Server-side plumbing of the AI Query panel - the agent loop in FoundryQueryUseCase, the Foundry wire protocol and prompt, tool-call authorization, DAX building, XMLA execution, metadata cache, usage accounting, CSV export cache, and log EventIds; read before debugging or changing anything behind POST /api/FoundryAi/query.
area: architecture
---

# AI Panel — Backend Plumbing

This document explains how the AI Panel works end to end on the server side, from a question landing in the use case to a DAX query being run against Power BI and an answer being streamed back. The browser client, the controllers, the SSE wire format, and the endpoint statuses are covered in `Home-AIQuery-Plumbing.md`; tenant isolation in depth is in `AIPanel-DataSourceID-Security.md`.

## The pieces at a glance

| Piece | Where | What it does |
|---|---|---|
| Use case | `GraspANALYTICS.Core/Application/UseCases/FoundryQueryUseCase.cs` + `IFoundryQueryUseCase.cs` | `ExecuteAsync()`: setup, then the agentic loop (`StreamAsync`). |
| Context builder | `GraspANALYTICS.Core/Application/Foundry/FoundryQueryContextBuilder.cs` + `IFoundryQueryContextBuilder.cs` | `ResolveSettingsAsync()`, `BuildAsync()`. |
| Table resolver | `GraspANALYTICS.Core/Application/UseCases/DefaultTableResolver.cs` | `Resolve()`: the `ReportData` table, else the first `ReportData*` table. |
| Data scope | `GraspANALYTICS.Core/Application/Authorization/UserDataScopeResolver.cs` | `ResolveAsync()`: data source ids and account ids for the user. |
| Data sources | `GraspANALYTICS.Core/Application/Services/DataSourceService.cs` | `GetUserConsolidatedIDsAsync()`. |
| Tool call | `GraspANALYTICS.Core/Application/Foundry/FoundryToolCallService.cs` | `RunAsync()`: cap, authorize, build DAX, execute, save the CSV export. |
| Authorizer | `GraspANALYTICS.Core/Application/Authorization/ToolSpecAuthorizer.cs` | `Intersect()`. |
| DAX builder | `GraspANALYTICS.Core/Infrastructure/PowerBI/DaxQuerySpecBuilder.cs` | `Build()`. |
| Dataset service | `GraspANALYTICS.Core/Infrastructure/PowerBI/PowerBiDatasetService.cs` + `IPowerBiDatasetService.cs` | `ExecuteSpecAsync()`, `FetchForQueryAsync()`. |
| XMLA | `GraspANALYTICS.Core/Infrastructure/PowerBI/XmlaService.cs` + `IXmlaService.cs` | `ExecuteDaxAsync()`, `ExecuteTabularQueryAsync()`. |
| Metadata | `GraspANALYTICS.Core/Infrastructure/PowerBI/DatasetMetadataService.cs` + `IDatasetMetadataService.cs` | `GetAsync()`, `GetTableFields()`, `RefreshDataTablesMetaDataAsync()`, `DatasetExistsAsync()`, `GetTableFieldDefinitionsCsvAsync()`. |
| Foundry client | `GraspANALYTICS.Core/Infrastructure/Foundry/FoundryAiService.cs` + `IFoundryAiService.cs` | `QueryStreamWithToolsAsync()`, `SubmitToolResultsAsync()`, `CsvExportGuidance`. |
| Foundry stream events | `GraspANALYTICS.Core/Infrastructure/Foundry/FoundryStreamEvent.cs` + `FoundryDelta.cs`, `FoundryResponseCreated.cs`, `FoundryToolCallRequested.cs`, `FoundryDone.cs` | The records `FoundryAiService` yields to the loop. |
| Foundry result | `GraspANALYTICS.Core/Infrastructure/Foundry/FoundryApiResult.cs` | Answer text and token counts of a finished turn. |
| Foundry exceptions | `GraspANALYTICS.Core/Infrastructure/Foundry/Exceptions/` | `FoundryDisabledException`, `FoundryRequestFailedException`, `FoundryResponseException`, `UnknownFoundryFieldFilterException`. |
| Entra token | `GraspANALYTICS.Core/Infrastructure/EntraIdService/EntraIdService.cs` | `GetAccessTokenAsync()`. |
| HTTP clients | `GraspANALYTICS/CompositionRoot/GraspHttpClientsExtensions.cs` | Registers the named `foundry` (5-minute timeout) and `powerbi` (30-second timeout) clients. |
| Payload estimator | `GraspANALYTICS.Core/Application/AiUsage/FoundryPayloadEstimator.cs` | `EstimateTokens()`, `EstimateInitialPayload()`, `EstimateToolResults()`. |
| Usage | `GraspANALYTICS.Core/Application/AiUsage/AiUsageService.cs` | `GetAsync()`, `RegisterUsageAsync()`, `SetLimitAsync()`, `ResetAsync()`, `GetForSelectedAgencyAsync()`. |
| Prompt history | `GraspANALYTICS.Core/Application/AiUserPrompts/AiUserPromptService.cs` + `GraspANALYTICS.Domain/Services/IAiUserPromptService.cs` | `SaveAsync()`, `GetByAgencyAsync()`. |
| Agency settings | `GraspANALYTICS.Core/Application/FoundrySettings/FoundryAgencySettingsService.cs` + `GraspANALYTICS.Domain/Services/IFoundryAgencySettingsService.cs` | `GetAsync()`. |
| Metadata refresh | `GraspANALYTICS/Controllers/Api/Admin/FoundryMetadataAdminController.cs` → `GraspANALYTICS.Core/Application/FoundrySettings/FoundryMetadataAdminService.cs` | `POST api/admin/foundry-metadata/refresh`, `POST api/admin/foundry-metadata/reports/{reportId}/refresh`. |
| CSV store | `GraspANALYTICS.Core/Application/Foundry/FoundryCsvExportStore.cs` | `Save()`, `Get()`. |
| CSV use case | `GraspANALYTICS.Core/Application/UseCases/FoundryCsvExportUseCase.cs` | `Execute()`. |
| CSV builder | `GraspANALYTICS.Core/Application/Foundry/CsvContentBuilder.cs` | `Build()`. |
| Settings | `GraspANALYTICS.Domain/Repositories/ISettingsRepository.cs` | `GetAsync()`: the refusal messages and limits (section 14). |
| Domain models | `GraspANALYTICS.Domain/Models/Foundry/` + `GraspANALYTICS.Domain/Models/Auth/` | Request, spec, scope, context, results, and events (section 2). |

## 1. High-level shape

The AI Panel is an **agentic** loop: the user asks a question, an LLM agent (hosted in Azure AI Foundry) decides what data it needs, calls a function tool (`query_table`), we translate that tool call into DAX, execute it against the Power BI XMLA endpoint, hand the rows back to the agent, and the agent answers the user. It may call the tool several times per question.

The tool definition lives on the Foundry agent, not in our request: the request body names the agent (`agent_reference`) and never sends a `tools` array. The code does not check the function name either — every `function_call` the agent emits is parsed as a `FoundryToolQuerySpec`.

```diagram
FoundryAiController.Query
    │
    ▼
FoundryQueryUseCase.ExecuteAsync
    ├─ FoundryQueryContextBuilder.ResolveSettingsAsync   (agency settings, else FoundryDisabledException)
    ├─ IAiUsageService.GetAsync                           (token budget gate)
    └─ FoundryQueryContextBuilder.BuildAsync              (dataset, metadata, table, data scope, schema)
    │
    ▼
FoundryQueryUseCase.StreamAsync  ◄────────►  FoundryAiService  ◄────►  Azure AI Foundry (SSE)
    │ (tool call: FoundryToolCall → FoundryToolQuerySpec)
    ▼
FoundryToolCallService.RunAsync
    ├─ ToolSpecAuthorizer.Intersect   (intersect with AuthorizationScope; force tenant filters)
    ├─ DaxQuerySpecBuilder.Build      →  DAX string
    ├─ PowerBiDatasetService.ExecuteSpecAsync  →  XmlaService  →  Power BI XMLA endpoint
    └─ FoundryCsvExportStore.Save     (rows kept for the CSV download)
    │ (rows)
    ▼
FoundryAiService.SubmitToolResultsAsync  →  back to the agent
    │
    ▼
Final answer streamed as delta events, then usage + done
```

## 2. Key types (domain models)

Foundry models are in `GraspANALYTICS.Domain/Models/Foundry`; the scope types are in `GraspANALYTICS.Domain/Models/Auth`.

- `FoundryQueryRequest` (`GraspANALYTICS.Domain/Models/Foundry/FoundryQueryRequest.cs`) — the incoming request: `Query`, `DatasetId`, `AccountIds`, `DateFrom`/`DateTo`, `FieldFilter`, `ReportContext`, `MaxRows`, `ConversationHistory`, `UseGlobalDataset`. `AccountIds` is not used by the agent path; the account scope comes from the server (section 3).
- `FoundryToolQuerySpec` (`GraspANALYTICS.Domain/Models/Foundry/FoundryToolQuerySpec.cs`) — the structured request the **agent** emits when it calls the tool. It is a SQL-shaped record: `Table`, `Select`, `Where`, `GroupBy`, `Aggregations`, `OrderBy`, `Limit`. `Table` is parsed but not used; the query always runs against the resolved table. Enums arrive as camelCase strings.
- `FoundryWhereClause` (`GraspANALYTICS.Domain/Models/Foundry/FoundryWhereClause.cs`) — `Field`, `Op`, `Value` as a `JsonNode`.
- `FoundryWhereOp` (`GraspANALYTICS.Domain/Models/Foundry/FoundryWhereOp.cs`) — `Eq`, `Neq`, `In`, `NotIn`, `Gt`, `Gte`, `Lt`, `Lte`, `Between`, `IsNull`, `IsNotNull`, `Contains`.
- `FoundryAggregation` (`GraspANALYTICS.Domain/Models/Foundry/FoundryAggregation.cs`) — `Func`, optional `Field`, `Alias`.
- `FoundryAggFunc` (`GraspANALYTICS.Domain/Models/Foundry/FoundryAggFunc.cs`) — `Count`, `CountDistinct`, `Sum`, `Avg`, `Min`, `Max`.
- `FoundryOrderBy` (`GraspANALYTICS.Domain/Models/Foundry/FoundryOrderBy.cs`) — `FieldOrAlias`, `Desc`.
- `FoundryToolCall` (`GraspANALYTICS.Domain/Models/Foundry/FoundryToolCall.cs`) — `CallId`, `Name`, `Spec`, one per agent function call.
- `FoundryToolResult` (`GraspANALYTICS.Domain/Models/Foundry/FoundryToolResult.cs`) — `CallId`, `Result` (rows JSON), `RowCount`, `Truncated`, `Error`; what goes back to the agent.
- `FoundryQueryExecutionResult` (`GraspANALYTICS.Domain/Models/Foundry/FoundryQueryExecutionResult.cs`) — wraps a `FoundryToolResult` with the `Dax`, `DurationMs`, and `ExportId` of the call.
- `FoundryQueryContext` (`GraspANALYTICS.Domain/Models/Foundry/FoundryQueryContext.cs`) — everything the loop needs once setup is done: the request, `TableName`, `Fields`, `Scope`, `SchemaPayload`, `WorkspaceName`, `DatasetName`, `GlobalDatasetUnavailable`.
- `AuthorizationScope` (`GraspANALYTICS.Domain/Models/Auth/AuthorizationScope.cs`) — the server-side guardrails the agent's spec is intersected with: `AccountIds`, `DateFrom`/`DateTo` (carried, not enforced), `AllowedFields`, `RowCeiling`, and `DataSourceIds` (tenant boundary).
- `AuthorizationResult` (`GraspANALYTICS.Domain/Models/Auth/AuthorizationResult.cs`) — what the authorizer returns: `Allowed(spec)` or `Denied(error)`.
- `FoundryAgencySettings` (`GraspANALYTICS.Domain/Models/Foundry/FoundryAgencySettings.cs`) — the per-agency row from `ganFoundryAgencySettings` (section 14). Fetched through `IFoundryAgencySettingsService` (`GraspANALYTICS.Domain/Services/IFoundryAgencySettingsService.cs`), which loads it via `IFoundryAgencySettingsRepository` (`GraspANALYTICS.Domain/Repositories/IFoundryAgencySettingsRepository.cs`) and returns `null` when the agency has no row or `FoundryEnabled = false`.
- `FoundryCsvExport` (`GraspANALYTICS.Domain/Models/Foundry/FoundryCsvExport.cs`) — the rows of one tool call kept for the CSV download (section 12).
- `FoundrySseFrame` (`GraspANALYTICS.Domain/Models/Foundry/FoundrySseFrame.cs`) — one Foundry SSE frame: event name and payload (section 4).
- `IFoundryQueryEvent` (`GraspANALYTICS.Domain/Models/Foundry/IFoundryQueryEvent.cs`) — the events the use case yields; `EventName` is the SSE `event:` name. The implementations are `FoundryQueryMetaEvent` (`meta`), `FoundryQueryUsageEvent` (`usage`), `FoundryQueryNoticeEvent` (`notice`), `FoundryQueryDeltaEvent` (`delta`), `FoundryQueryToolEvent` (`tool`), `FoundryQueryRefusalEvent` (`refusal`), and `FoundryQueryDoneEvent` (`done`, carrying a `FoundryQueryResponse`), each in its own file in `GraspANALYTICS.Domain/Models/Foundry/`. Their JSON payloads: see Home-AIQuery-Plumbing.md §3.

## 3. The orchestrator: `FoundryQueryUseCase`

`FoundryQueryUseCase` (`GraspANALYTICS.Core/Application/UseCases/FoundryQueryUseCase.cs`) is the heart of the AI Panel. The controller calls `FoundryQueryUseCase.ExecuteAsync(FoundryQueryRequest, UserContext, CancellationToken)` and gets back a `Task<IAsyncEnumerable<IFoundryQueryEvent>>`. Setup runs when the task is awaited, before the controller writes any SSE header, so setup exceptions become plain HTTP errors (mapping: see Home-AIQuery-Plumbing.md §3).

On the server:

```text
FoundryAiController.Query                                   see Home-AIQuery-Plumbing.md §3
  └─ IFoundryQueryUseCase.ExecuteAsync(request, userContext)   FoundryQueryUseCase
       ├─ IFoundryQueryContextBuilder.ResolveSettingsAsync(userContext)   FoundryQueryContextBuilder
       │    └─ IFoundryAgencySettingsService.GetAsync()   FoundryAgencySettingsService
       │          null → FoundryDisabledException
       ├─ IAiUsageService.GetAsync(userContext)   AiUsageService
       │     not Allowed → one budget refusal, then the stream ends
       ├─ IFoundryQueryContextBuilder.BuildAsync(request, settings, userContext)
       └─ FoundryQueryUseCase.StreamAsync(ctx, userContext, usage)
            ├─ FoundryPayloadEstimator.EstimateInitialPayload(schemaPayload, query, history)
            ├─ IFoundryAiService.QueryStreamWithToolsAsync(question, schema, history, dateFrom, dateTo)   FoundryAiService
            ├─ IFoundryToolCallService.RunAsync(call, toolCallsExecuted, ctx, userContext)   FoundryToolCallService
            ├─ FoundryPayloadEstimator.EstimateToolResults(results)
            ├─ IFoundryAiService.SubmitToolResultsAsync(responseId, results)
            ├─ FoundryQueryUseCase.SaveQueryAsync(ctx, answer, inputTokens, outputTokens, executedDax)
            │    └─ IAiUserPromptService.SaveAsync(prompt)
            └─ IAiUsageService.RegisterUsageAsync(userContext, tokens)
```

### `FoundryQueryUseCase.ExecuteAsync` — setup

`FoundryQueryUseCase.ExecuteAsync`:

1. Guards `request.DatasetId`.
2. `IFoundryQueryContextBuilder.ResolveSettingsAsync(userContext)` loads the agency settings through `IFoundryAgencySettingsService.GetAsync`; `null` throws `FoundryDisabledException(userContext.AgencyId)`.
3. `IAiUsageService.GetAsync(userContext)`. When the result is not `Allowed`, the use case returns a one-event stream: a `budget` refusal carrying `usage.Message` and the usage. The agent is never called.
4. `IFoundryQueryContextBuilder.BuildAsync(request, settings, userContext)` builds the `FoundryQueryContext` (below).
5. Hands off to `FoundryQueryUseCase.StreamAsync`.

### `FoundryQueryContextBuilder.BuildAsync` — the query context

`FoundryQueryContextBuilder.BuildAsync`:

1. **Dataset** — `FoundryQueryContextBuilder.ResolveDatasetId`: the request's `DatasetId`, or, when `UseGlobalDataset` is set, the agency's `DefaultDatasetId`. When that is unset (null or `Guid.Empty`) it logs EventId 7001, falls back to the request's dataset, and sets `GlobalDatasetUnavailable`.
2. **Metadata** — `IDatasetMetadataService.GetAsync(datasetId, userContext.WorkspaceId)` (section 8).
3. **Table** — `DefaultTableResolver.Resolve(metadata.Tables)`: the table named `ReportData`, else the first table whose name starts with `ReportData` (both case-insensitive); none throws `PowerBiTableNotFoundException("ReportData")`. Its fields come from `IDatasetMetadataService.GetTableFields(metadata, table)`.
4. **Field filter** — names in `request.FieldFilter` that are not in the table throw `UnknownFoundryFieldFilterException`.
5. **Data scope** — `IUserDataScopeResolver.ResolveAsync(userContext)` (`UserDataScopeResolver`): `DataSourceIds` from `IDataSourceService.GetUserConsolidatedIDsAsync(userContext.AgencyId)` (the agency's consolidated child data sources, or the agency id alone), and `AccountIds` from `IAccountsService.GetAccountIdsByUserIdAsync(userId)` for users who are not Admin or Super Admin (`null` for those roles).
6. **Row ceiling** — `Math.Clamp(request.MaxRows ?? DefaultMaxRows, 1, MaxRowsCeiling)`.
7. **Allowed fields** — columns (`Kind == "Column"`, so no measures) whose names do not start with `RowNumber`, narrowed to `FieldFilter` when one is given.
8. **Scope** — an `AuthorizationScope` of the account ids, `request.DateFrom`, `request.DateTo`, the allowed fields, the row ceiling, and the data source ids.
9. **Schema payload** — `{ "table": <name>, "fields": [ { name, dataType?, description? } ] }` for the allowed fields only. This is the only schema the agent sees.

### `FoundryQueryUseCase.StreamAsync` — the agentic loop

`FoundryQueryUseCase.StreamAsync`:

1. Yields `meta` (0 rows, workspace and dataset names) and `usage` (the status from setup). Yields a `notice` with reason `global-dataset-unavailable` and the message "Global dataset unavailable. This answer used the current report's dataset instead. I've switched off Global Dataset for now." when the global dataset fell back.
2. **Preflight** (section 11). Reads `AiMaxPreflightTokens` (default 20,000) and calls `FoundryPayloadEstimator.EstimateInitialPayload`. Over the limit → `oversized` refusal (`AiFoundryOversizedRequestMessage`); over `usage.Remaining` → `budget` refusal (`AiFoundryBudgetExceededMessage`). Either ends the stream.
3. First turn: `IFoundryAiService.QueryStreamWithToolsAsync` with the question, the schema payload, the conversation history, and the request's `DateFrom`/`DateTo` (section 4).
4. Drains the turn's `FoundryStreamEvent`s:
   - `FoundryResponseCreated` → remembers the `responseId` for tool-result continuation.
   - `FoundryDelta` → yields `FoundryQueryDeltaEvent`.
   - `FoundryToolCallRequested` → buffers the call until the turn ends.
   - `FoundryDone` → the final answer.

   A `FoundryRequestFailedException` or `FoundryResponseException` while reading logs EventId 4001 and ends the stream with an `error` refusal (`AiFoundryServiceErrorMessage`).
5. **Done** — `FoundryQueryUseCase.SaveQueryAsync` saves the prompt (a failure logs 4002 and is ignored), `IAiUsageService.RegisterUsageAsync` registers usage (section 11; a failure logs 4004 and keeps the earlier status), then the loop yields `usage` with the new count and `done` with a `FoundryQueryResponse` (answer, rows analyzed, input, cached, output, reasoning, and total tokens), and ends.
6. **Not done** — `FoundryQueryUseCase.ValidateTurn` requires at least one pending call and a `responseId`; otherwise it throws `FoundryResponseException`. That throw is outside the read `try`, so it is not turned into a refusal: it ends the stream with no `done`.
7. Runs each pending call through `IFoundryToolCallService.RunAsync` (below). After each call it yields `meta` with the running `rowsAnalyzed` / `rowsTruncated` (only calls without an error count) and a `tool` event (`CallId`, `Name`, `Dax`, rows, truncated, duration, `ExportId`).
8. **Result preflight** — `FoundryPayloadEstimator.EstimateToolResults`; over `AiMaxPreflightTokens` logs EventId 4003 and ends with an `oversized` refusal (`AiFoundryOversizedResultsMessage`).
9. Otherwise calls `IFoundryAiService.SubmitToolResultsAsync(responseId, results)` and loops back to step 4.

- Every refusal the loop yields carries the current usage status.
- Refusal message templates come from settings read through `ISettingsRepository`, are formatted with `string.Format` (`{0}` the estimate, `{1}` the limit or remaining tokens), and fall back to "There was an issue connecting to the AI engine" when the setting is empty or the template is malformed.
- The `tool` event always carries the DAX; the controller's serializer drops it when the user's `DaxQueriesEnabled` is off (see Home-AIQuery-Plumbing.md §4). The saved prompt keeps the DAX either way.

### `FoundryToolCallService.RunAsync` — one tool call

`FoundryToolCallService.RunAsync` (`GraspANALYTICS.Core/Application/Foundry/FoundryToolCallService.cs`) runs one call, given the running count of calls for the question:

1. **Cap** — when the count exceeds `MaxToolCallsPerTurn = 5`, returns an error result ("Tool call cap exceeded (5). Please summarize with the data already gathered.") without running anything. The count spans every turn of the question, so the sixth call of the whole question is the first one capped.
2. **Authorize** — `ToolSpecAuthorizer.Intersect(call.Spec, ctx.Scope)` (section 5). A denial becomes an error result with no DAX.
3. **Build DAX** — `DaxQuerySpecBuilder.Build(ctx.TableName, spec, ctx.Fields)` for the diagnostics. A build exception logs a warning and becomes the error "Failed to build DAX: …" with no DAX.
4. **Execute** — `IPowerBiDatasetService.ExecuteSpecAsync(workspaceName, datasetName, tableName, spec, fields)` (which builds the same DAX again and runs it). `RowCount` is the length of the `rows` array; `Truncated` is `RowCount >= RowCeiling`.
5. **CSV export** — `FoundryToolCallService.SaveCsvExport`: when the call returned rows, saves a `FoundryCsvExport` and returns its id (section 12).
6. An execution exception logs a warning and becomes an error result with the exception message; the DAX is kept for the diagnostics.

Errors go back to the agent as tool output; the agent decides what to tell the user.

## 4. Talking to the agent: `FoundryAiService`

`FoundryAiService` (`GraspANALYTICS.Core/Infrastructure/Foundry/FoundryAiService.cs`) is the only thing that knows how to speak Azure AI Foundry's wire protocol (the OpenAI-style Responses API). It is transport plus prompt construction; it has no business logic.

### `FoundryAiService.BuildRequestAsync` — endpoint and authentication

- Each call re-reads the agency settings through `IFoundryAgencySettingsService.GetAsync` (`null` throws `FoundryDisabledException`) and POSTs to `FoundryAgencySettings.EndpointUrl` with the named `foundry` `HttpClient` (5-minute timeout, `GraspHttpClientsExtensions`).
- `FoundryAiService.BuildRequestAsync` gets a bearer token from `IEntraIdService.GetAccessTokenAsync("https://ai.azure.com/.default")`; a failure logs EventId 1006 and throws `FoundryRequestFailedException("TokenAcquire", -1)`.

### `FoundryAiService.QueryStreamWithToolsAsync` — first turn

`FoundryAiService.QueryStreamWithToolsAsync`:

1. Guards the question and the schema.
2. Resolves the agency settings.
3. `FoundryAiService.BuildToolsRequestBody` builds the body below, with the message from `FoundryAiService.BuildToolsMessageBody`.
4. `FoundryAiService.StreamFoundryEventsAsync` sends it and yields the stream events.

```json
{
  "input": "<message>"  |  [ { "role": "...", "content": "..." }, ..., { "role": "user", "content": "<message>" } ],
  "agent_reference": { "name": "<AgentName>", "version": "<AgentVersion>", "type": "agent_reference" },
  "stream": true
}
```

`input` is the message string, or, when there is conversation history, the history turns followed by the message as the last user turn.

### `FoundryAiService.BuildToolsMessageBody` — prompt construction

The message is plain text, one part per line, in this order:

1. `Question:`
2. the user's question
3. the agency's AI context, rendered
4. `FoundryAiService.CsvExportGuidance`
5. `Schema:`
6. the schema payload JSON

- **AI context** — `FoundryAgencySettings.AiContext`, the agency's instruction text (edited through the Super Admin `FoundryAgencySettingsController` PATCH). `FoundryAiService.RenderAiContext` replaces the `{DateFrom}` and `{DateTo}` placeholders (ordinal, case-sensitive) with the panel's dates as `yyyy-MM-dd`, or `(no lower bound)` / `(no upper bound)` when a date is empty. An empty AI context adds nothing.
- **`FoundryAiService.CsvExportGuidance`** — the only instruction fixed in code: when the answer presents rows from the tool, end with one short sentence offering a CSV export that contains the word CSV; never offer it without rows, and never write the CSV content.
- **Schema** — the allowed fields only (section 3).

Everything else the agent does with dates, markers, and disclosure — whether it uses the default range, writes `DATE_RANGE_USED:`, writes the no-date-filter note, writes a `SUGGESTED_QUESTIONS:` line, or refuses to reveal DAX or column names — comes from the agency's AI context text and the Foundry agent's own instructions, not from code. The client markers: see Home-AIQuery-Plumbing.md §4.

### `FoundryAiService.StreamFoundryEventsAsync` — streaming

`FoundryAiService.StreamFoundryEventsAsync`:

1. `IHttpClientFactory.CreateClient("foundry")`.
2. `FoundryAiService.BuildRequestAsync` (above).
3. Sends the request with `HttpCompletionOption.ResponseHeadersRead`.
4. `FoundryAiService.EnsureStreamSuccessAsync` turns a non-success status into EventId 1001 (body truncated to 2,000 characters) and `FoundryRequestFailedException("Stream", status)`.
5. `FoundryAiService.ReadSseFramesAsync` reads lines: `:` comment lines are skipped, `event:` sets the name, `data:` lines are collected (joined with `\n`), and a blank line emits a `FoundrySseFrame` when it has data.
6. `FoundryAiService.DispatchFrame` turns each frame into `FoundryStreamEvent` records:
   - `response.output_text.delta` → `FoundryDelta(text)` (empty deltas dropped).
   - `response.completed` → `FoundryResponseCreated(responseId)` (when the response has an id), then one `FoundryToolCallRequested(call)` per function call from `FoundryAiService.ExtractToolCalls`; when there are none, `FoundryDone(result)` from `FoundryAiService.ParseResult`. A frame with no `response` object logs EventId 1002 and yields nothing.
   - `response.failed` / `error` → EventId 1003, then `FoundryResponseException`.
   - Any other event name is ignored.

`FoundryAiService.ExtractToolCalls` reads the `output` array of the completed response and takes the `type == "function_call"` items. An item missing `call_id`, `name`, or `arguments` logs EventId 1004 and is skipped. `arguments` is deserialized into a `FoundryToolQuerySpec` (camelCase names, camelCase enum strings); invalid JSON logs EventId 1005 and throws `FoundryResponseException`, as does a null spec.

`FoundryAiService.ParseResult` builds the `FoundryApiResult` (`GraspANALYTICS.Core/Infrastructure/Foundry/FoundryApiResult.cs`): the `output_text` blocks of the first `message` item concatenated (no message item throws `FoundryResponseException`), and the token counts from `usage`: `input_tokens`, `input_tokens_details.cached_tokens`, `output_tokens`, `output_tokens_details.reasoning_tokens`, `total_tokens`.

### `FoundryAiService.SubmitToolResultsAsync` — continuing the conversation

`FoundryAiService.SubmitToolResultsAsync` guards the response id and results, resolves the agency settings, builds the body below with `FoundryAiService.BuildToolResultsRequestBody`, and streams it through `FoundryAiService.StreamFoundryEventsAsync`.

```json
{
  "input": [ { "type": "function_call_output", "call_id": "<id>", "output": "<string>" }, ... ],
  "agent_reference": { ... },
  "previous_response_id": "<responseId>",
  "stream": true
}
```

`output` is the rows JSON as a string (`{"rows":[...]}`), `{"error":"..."}` for an error result, or `{"rows":[]}` when there is no result. The response is another SSE stream with the same parser and event types. The loop continues until the agent finishes without requesting more tools.

## 5. Authorization: `ToolSpecAuthorizer`

`ToolSpecAuthorizer.Intersect` (`GraspANALYTICS.Core/Application/Authorization/ToolSpecAuthorizer.cs`) is the gatekeeper between "what the agent asked for" and "what we'll actually run." It returns an `AuthorizationResult` that is either `Allowed(rewrittenSpec)` or `Denied(error)`.

Rewrites applied:

- `Select`, `GroupBy` → filtered to `AllowedFields`.
- `Aggregations` → kept when the field is allowed or empty (a field-less `Count` is `COUNTROWS`).
- `OrderBy` → kept when the name is an allowed field or a kept aggregation alias.
- `Where` → `ToolSpecAuthorizer.BuildIntersectedWhere`:
  - Agent-supplied clauses are kept only if their field is allowed.
  - **Any agent-supplied `DataSourceID` clause is dropped** (case-insensitive).
  - When `scope.AccountIds` is non-empty, an `AccountID IN (...)` clause is appended.
  - When `scope.DataSourceIds` is non-empty, a `DataSourceID IN (...)` clause is appended. `IDataSourceService.GetUserConsolidatedIDsAsync` always returns at least the agency id, so this clause is always present.
  - `DateFrom`/`DateTo` are **not** enforced; the dates reach the agent only through the AI context placeholders (section 4).
- `Limit` → `min(agent limit, RowCeiling)` for a positive agent limit, otherwise the ceiling.

If after rewriting there is nothing to select, group, or aggregate, the call is denied with "After authorization, no allowed fields remain to project or aggregate."

The full strip-and-force argument: see AIPanel-DataSourceID-Security.md.

## 6. DAX generation: `DaxQuerySpecBuilder`

`DaxQuerySpecBuilder.Build(tableName, spec, fields)` (`GraspANALYTICS.Core/Infrastructure/PowerBI/DaxQuerySpecBuilder.cs`) turns the authorized `FoundryToolQuerySpec` into a DAX query string. It is a pure static function — no DI, no I/O.

Two shapes depending on whether the spec aggregates:

- **Aggregating** (`GroupBy` or `Aggregations` present): `DaxQuerySpecBuilder.BuildSummarizeColumns` emits `SUMMARIZECOLUMNS(<group cols>, FILTER(table, <conditions>), "alias", <expr>, ...)`. `Count` is `COUNTROWS(table)` without a field and `COUNT(col)` with one; `CountDistinct`, `Sum`, `Avg`, `Min`, `Max` map to `DISTINCTCOUNT`, `SUM`, `AVERAGE`, `MIN`, `MAX` and require a field.
- **Row projection**: `DaxQuerySpecBuilder.BuildRowsExpression` emits `SELECTCOLUMNS(<source>, "name", table[name], ...)` for the visible columns named in `Select` (all visible columns when `Select` is empty; the bare source when none of the named columns is visible). `<source>` is the bare table or `FILTER(table, <conditions>)`.

Then `DaxQuerySpecBuilder.ApplyLimit` wraps the expression in `TOPN(limit, expr[, [col], 0|1, ...])` (0 is descending) when a limit is set, `DaxQuerySpecBuilder.ApplyOrder` appends `ORDER BY [col] ASC|DESC`, and the result is prefixed with `EVALUATE`.

Where clauses are compiled by `DaxQuerySpecBuilder.BuildCondition` and joined with `&&`: `=`, `<>`, `>`, `>=`, `<`, `<=`, `IN {...}`, `NOT(col IN {...})`, `Between` (a two-element array, as `>= && <=`), `ISBLANK` / `NOT(ISBLANK(...))`, and `CONTAINSSTRING`. `DaxQuerySpecBuilder.FormatScalar` renders `null` as `BLANK()`, booleans as `TRUE()`/`FALSE()`, numbers with the invariant culture, strings matching `yyyy-MM-dd` as `DATE(y, m, d)`, and other strings quoted with `"` doubled. Arrays or objects in a scalar position throw `ArgumentException`.

Identifier safety: `DaxQuerySpecBuilder.EscapeColumn` doubles any `]` inside a column name; `DaxQuerySpecBuilder.EscapeStringLiteral` doubles `"` inside aliases and string literals. Together with the `AllowedFields` gate in the authorizer, this is what keeps DAX injection off the table even though we're string-building. The table name is inserted as-is; it comes from the dataset metadata, never from the agent.

## 7. Executing DAX: `PowerBiDatasetService` + `XmlaService`

- `PowerBiDatasetService.ExecuteSpecAsync` (`GraspANALYTICS.Core/Infrastructure/PowerBI/PowerBiDatasetService.cs`) — builds the DAX with `DaxQuerySpecBuilder.Build` and calls `IXmlaService.ExecuteDaxAsync(dax, workspaceName, datasetName, customData: null)` (`GraspANALYTICS.Core/Infrastructure/PowerBI/IXmlaService.cs`). On a failure other than cancellation it logs the operation, workspace, dataset, table, elapsed time, and the DAX at Error level, then rethrows.
- `PowerBiDatasetService.FetchForQueryAsync` — builds DAX from a `FoundryQueryRequest`; nothing calls it.
- `XmlaService.ExecuteDaxAsync` (`GraspANALYTICS.Core/Infrastructure/PowerBI/XmlaService.cs`) — the only place that touches the XMLA endpoint. It connects with ADOMD.NET to `powerbi://api.powerbi.com/v1.0/myorg/<workspace name>` (the name URI-escaped), catalog `<dataset name>`, as the service principal from `IPowerBiAuthSettingsService` (`app:<ApplicationId>@<TenantId>` and the client secret), runs the query inside `Task.Run`, and returns `{ "rows": [ { "<column>": value, ... } ] }`.
- Column keys are the names ADOMD reports (`[alias]` or `Table[Column]`). There is no separate columns array.
- Errors other than cancellation are wrapped in `XmlaConnectionFailedException` with the password scrubbed from the message.
- That JSON is exactly what gets handed back to the agent and kept for the CSV export.

## 8. Dataset metadata: `DatasetMetadataService`

`DatasetMetadataService.GetAsync(datasetId, workspaceId)` (`GraspANALYTICS.Core/Infrastructure/PowerBI/DatasetMetadataService.cs`) returns a `PowerBiDatasetMetadataResponse` (`GraspANALYTICS.Domain/Models/PowerBI/PowerBiDatasetMetadataResponse.cs`: workspace, dataset, tables with fields):

1. **Cache** — `ICacheService.GetOrCreateAsync` keyed by `pbi-meta:<workspaceId>:<datasetId>`, with a TTL of `FoundryAgencySettings.MetadataCacheMinutes` (30 when there are no settings).
2. **Persisted copy** — on a cache miss `DatasetMetadataService.ReadPersistedMetadataAsync` reads the `DataTablesMetaData` JSON stored on the dataset's reports (`IReportRepository.GetDataTablesMetaDataByDatasetAsync`) and uses it when it parses. Unreadable JSON logs EventId 2005 and falls through.
3. **Live fetch** — otherwise `DatasetMetadataService.FetchFromPowerBiAsync` reads the workspace and dataset names from the Power BI REST API (EventIds 2001–2003 on failures), runs `INFO.VIEW.TABLES()`, `INFO.VIEW.COLUMNS()`, and `INFO.VIEW.MEASURES()` in parallel through `IXmlaService.ExecuteTabularQueryAsync` (EventId 2006 on an XMLA failure), skips hidden tables, and `DatasetMetadataService.WriteDataTablesMetaDataAsync` writes the result back to every report on that dataset (a failure logs EventId 2004 and is ignored).

- Columns get `Kind = "Column"` and measures `Kind = "Measure"`.
- Because the persisted copy is used whenever it exists, schema changes reach the agent only after a Super Admin refresh (`POST api/admin/foundry-metadata/refresh` or `POST api/admin/foundry-metadata/reports/{reportId}/refresh` → `IDatasetMetadataService.RefreshDataTablesMetaDataAsync`) and the in-memory cache entry expiring.
- `DatasetMetadataService.GetTableFields` is a synchronous helper the context builder uses to get the field list of the resolved table.

## 9. The tenant boundary in one diagram

The single most important invariant of the AI Panel is that **the agent cannot read data outside the user's tenant**. The pieces that enforce that:

```diagram
UserContext.AgencyId
    │
    ▼ (UserDataScopeResolver → IDataSourceService.GetUserConsolidatedIDsAsync)
AuthorizationScope.DataSourceIds = consolidated child data sources, or [AgencyId]
    │
    ▼ (ToolSpecAuthorizer.BuildIntersectedWhere: drop agent DataSourceID clauses, append)
DAX always includes: ReportData[DataSourceID] IN {<ids>}   (AND-combined)
    │
    ▼ (DaxQuerySpecBuilder, then XmlaService)
Power BI returns only rows for those data sources
```

The filter is added server-side, after the agent's request is parsed, and the agent cannot remove it. `DataSourceID` and `AccountID` are not removed from the allowed fields, so when they are visible columns of the table the agent sees them in the schema and can select or group by them — always within the forced filter. Details and attack paths: see AIPanel-DataSourceID-Security.md.

## 10. Conversation continuity

`FoundryQueryRequest.ConversationHistory` is a list of `ConversationTurn` (`GraspANALYTICS.Domain/Models/Foundry/ConversationTurn.cs`: `Role` + `Content`) kept client-side and replayed as input messages on every request. The agent on the Foundry side is otherwise stateless across requests — `previous_response_id` is only used within a single multi-turn tool-calling exchange, not across user questions.

## 11. Usage accounting and preflight

- `AiUsageService.GetAsync` (`GraspANALYTICS.Core/Application/AiUsage/AiUsageService.cs`) — the gate. Loads the user's AI usage limit row for the agency through `IAiUsageLimitRepository.GetAsync`, resets `TokensUsed` (and saves the row) when the row's `DateModified` is in an earlier month, and returns an `AiAccessStatus`. No row, access off, or `TokensUsed >= TokenLimit` means not `Allowed` (the user-facing statuses: see Home-AIQuery-Plumbing.md §2).
- `FoundryPayloadEstimator` (`GraspANALYTICS.Core/Application/AiUsage/FoundryPayloadEstimator.cs`) — the preflight. `FoundryPayloadEstimator.EstimateTokens` is characters / 4, rounded up. `FoundryPayloadEstimator.EstimateInitialPayload` counts the schema JSON, the question, and every history turn's role and content; it does not count the AI context or `CsvExportGuidance`. `FoundryPayloadEstimator.EstimateToolResults` counts the result JSON of each tool result (error results count 0).
- `AiUsageService.RegisterUsageAsync` — registration. When the answer completes, the use case calls `IAiUsageService.RegisterUsageAsync(userContext, InputTokens + OutputTokens)`, which adds the tokens to `TokensUsed` and returns the new status. The counts come from the `usage` of the final `response.completed` only; cached and reasoning tokens are reported in `done` but not added separately. A cancelled or refused question registers nothing.
- `FoundryQueryUseCase.SaveQueryAsync` — prompt history. Writes an `AiUserPrompt` through `IAiUserPromptService.SaveAsync`: the question, the answer, `ReportId` from `ReportContext`, `Dataset` from the request's `DatasetId`, the executed DAX joined by blank lines, `TokensSent` (input tokens), and `TokensReceived` (output tokens).

## 12. CSV export cache

- `FoundryToolCallService.SaveCsvExport` — stores a `FoundryCsvExport` (`ExportId` as a `Guid` "N" string, `UserId`, `AgencyId`, `DatasetName`, the rows JSON) for every call that returned at least one row.
- `FoundryCsvExportStore.Save` (`GraspANALYTICS.Core/Application/Foundry/FoundryCsvExportStore.cs`) — keeps it in `IMemoryCache` under `foundry-csv-export:<exportId>` with a 30-minute sliding expiration. It lives in the app process, so a restart or another server instance does not have it.
- `FoundryCsvExportUseCase.Execute` (`GraspANALYTICS.Core/Application/UseCases/FoundryCsvExportUseCase.cs`) — returns not-found unless `FoundryCsvExportStore.Get` finds the export and its `UserId` and `AgencyId` match the caller, then builds the file with `CsvContentBuilder.Build` and prefixes a UTF-8 BOM.
- `CsvContentBuilder.Build` (`GraspANALYTICS.Core/Application/Foundry/CsvContentBuilder.cs`) — headers are the union of row keys in first-seen order, shown as the text inside the last `[...]`; values are quoted when they contain a comma, quote, or line break; rows end with CRLF.

The endpoint: see Home-AIQuery-Plumbing.md §5.

## 13. Logging

Source-generated log messages use the EventId blocks in `Logging-EventId-Registry.md`:

| Block | Class | EventIds |
|---|---|---|
| `1000–1999` | `FoundryAiService` | 1001 request failed, 1002 completed frame without `response`, 1003 error frame, 1004 function call missing fields, 1005 tool arguments not parsable, 1006 token acquisition failed |
| `2000–2999` | `DatasetMetadataService` | 2001–2006 (section 8) |
| `4000–4999` | `FoundryQueryUseCase` | 4001 stream failed (refusal), 4002 prompt not saved, 4003 tool results over the preflight limit, 4004 usage not recorded |
| `7000–7999` | `FoundryQueryContextBuilder` | 7001 global dataset requested but not set |

`FoundryToolCallService` (DAX build failure, execution failure) and `PowerBiDatasetService` (DAX failure, with the DAX text) log through plain `ILogger.LogWarning` / `LogError` calls with no EventId.

## 14. Configuration knobs

### `FoundryAgencySettings`

Stored per agency in the `ganFoundryAgencySettings` table and fetched at request time by `FoundryAgencySettingsService.GetAsync` (`GraspANALYTICS.Core/Application/FoundrySettings/FoundryAgencySettingsService.cs`), which keys off the session's `AgencyID` and returns `null` when the agency id is not positive, the row is missing, or `FoundryEnabled = false` — that null is what `FoundryQueryContextBuilder.ResolveSettingsAsync` turns into `FoundryDisabledException`. The fields that matter for behavior:

- `FoundryEnabled` — master on/off switch for the agency.
- `AgentName`, `AgentVersion` (default `"3"`) — which Foundry agent to invoke.
- `FoundryBaseUrl`, `FoundryProject` — combined into the computed `EndpointUrl` (`{base}/api/projects/{project}/openai/v1/responses`).
- `DefaultDatasetId` — the dataset used when the panel asks for the global dataset.
- `DefaultMaxRows` (default 1000), `MaxRowsCeiling` (default 5000) — default tool-call row limit and the hard upper bound the authorizer never lets the agent exceed.
- `MetadataCacheMinutes` (default 15) — TTL for the dataset metadata cache.
- `AiContext` — the agency's instruction text, with `{DateFrom}`/`{DateTo}` placeholders (section 4).

### Settings read through `ISettingsRepository`

| Key | Used by | Purpose |
|---|---|---|
| `AiMaxPreflightTokens` | `FoundryQueryUseCase` | Preflight token limit (default 20,000) |
| `AiFoundryServiceErrorMessage` | `FoundryQueryUseCase` | `error` refusal text |
| `AiFoundryOversizedRequestMessage` | `FoundryQueryUseCase` | `oversized` refusal before the first turn |
| `AiFoundryBudgetExceededMessage` | `FoundryQueryUseCase` | `budget` refusal when the estimate exceeds the remaining tokens |
| `AiFoundryOversizedResultsMessage` | `FoundryQueryUseCase` | `oversized` refusal for large tool results |
| `AiAccessNotEnabledMessage` | `AiUsageService` | Status message when access is off |
| `AiUsageDepletedMessage`, `AiFairUseStatement` | `AiUsageService` | Status message when the limit is reached |
| `AiDefaultTokenLimit` | `AiUsageService` | Token limit when a Super Admin sets none (default 100,000) |

## 15. Where to start reading

If you're new to this code, read in this order:

1. `GraspANALYTICS.Core/Application/UseCases/FoundryQueryUseCase.cs` — the orchestrator; everything else hangs off of it.
2. `GraspANALYTICS.Core/Application/Foundry/FoundryQueryContextBuilder.cs` — dataset, schema, and scope.
3. `GraspANALYTICS.Core/Infrastructure/Foundry/FoundryAiService.cs` (`FoundryAiService.BuildToolsMessageBody`) — the message the agent receives; then the agency's `AiContext` text for the instructions themselves.
4. `GraspANALYTICS.Core/Application/Foundry/FoundryToolCallService.cs` and `GraspANALYTICS.Core/Application/Authorization/ToolSpecAuthorizer.cs` — one tool call and the security boundary.
5. `GraspANALYTICS.Core/Infrastructure/PowerBI/DaxQuerySpecBuilder.cs` — how DAX gets shaped.
6. `GraspANALYTICS.Core/Infrastructure/PowerBI/PowerBiDatasetService.cs`, `GraspANALYTICS.Core/Infrastructure/PowerBI/XmlaService.cs`, and `GraspANALYTICS.Core/Infrastructure/PowerBI/DatasetMetadataService.cs` — the Power BI side.

## 16. Endpoints

| Endpoint | Handler | Request | Response | Statuses |
|---|---|---|---|---|
| `POST /api/FoundryAi/query` | `FoundryAiController.Query → IFoundryQueryUseCase.ExecuteAsync` | `FoundryQueryRequest` | `text/event-stream` of the `IFoundryQueryEvent`s (section 3) | see Home-AIQuery-Plumbing.md §7 |
| `POST https://<FoundryBaseUrl>/api/projects/<FoundryProject>/openai/v1/responses` (Azure AI Foundry) | `FoundryAiService.QueryStreamWithToolsAsync → FoundryAiService.StreamFoundryEventsAsync`; `FoundryAiService.SubmitToolResultsAsync → FoundryAiService.StreamFoundryEventsAsync` | bearer token for `https://ai.azure.com/.default`; JSON body with `input`, `agent_reference`, `stream: true`, and `previous_response_id` on tool-result turns (section 4) | SSE: `response.output_text.delta`, `response.completed`, `response.failed`, `error` | success; non-success → EventId 1001 and `FoundryRequestFailedException("Stream", status)` |
| `powerbi://api.powerbi.com/v1.0/myorg/<workspace name>` (Power BI XMLA, ADOMD.NET) | `PowerBiDatasetService.ExecuteSpecAsync → IXmlaService.ExecuteDaxAsync` | catalog `<dataset name>`, service principal `app:<ApplicationId>@<TenantId>`, the DAX query | `{ "rows": [ ... ] }` | failure → `XmlaConnectionFailedException` (password scrubbed) |
| `powerbi://api.powerbi.com/v1.0/myorg/<workspace name>` (Power BI XMLA, ADOMD.NET) | `DatasetMetadataService.FetchFromPowerBiAsync → IXmlaService.ExecuteTabularQueryAsync` | `INFO.VIEW.TABLES()`, `INFO.VIEW.COLUMNS()`, `INFO.VIEW.MEASURES()` | rows of tables, columns, measures | failure → EventId 2006, `XmlaConnectionFailedException` |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}` (Power BI REST) | `DatasetMetadataService.FetchFromPowerBiAsync → DatasetMetadataService.GetJsonAsync` | bearer token for `https://analysis.windows.net/powerbi/api/.default`, `powerbi` `HttpClient` | workspace `name` | success; non-success → EventId 2003 and `PowerBiApiRequestFailedException`; empty name → EventId 2001 and `PowerBiMetadataResolutionException` |
| `GET https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets/{datasetId}` (Power BI REST) | `DatasetMetadataService.FetchFromPowerBiAsync → DatasetMetadataService.GetJsonAsync` | bearer token for `https://analysis.windows.net/powerbi/api/.default`, `powerbi` `HttpClient` | dataset `name` | success; non-success → EventId 2003 and `PowerBiApiRequestFailedException`; empty name → EventId 2002 and `PowerBiMetadataResolutionException` |
| `POST api/admin/foundry-metadata/refresh` | `FoundryMetadataAdminController.RefreshAllAsync → IFoundryMetadataAdminService.RefreshAllAsync` | none | `FoundryMetadataRefreshResult` | 200; Super Admin only (`[Authorize(Roles = "Super Admin")]`, `[CustomAuthorize]`) |
| `POST api/admin/foundry-metadata/reports/{reportId}/refresh` | `FoundryMetadataAdminController.RefreshReportAsync → IFoundryMetadataAdminService.RefreshReportAsync` | `reportId` | `FoundryMetadataRefreshResult` | 200; 404 "Report {reportId} was not found or has no dataset."; Super Admin only |
