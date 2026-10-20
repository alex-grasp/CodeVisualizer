using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;

namespace DocsViewer.Core.Tests;

/// <summary>Endpoint tables in every header shape the plumbing docs use, and owner resolution of bare method
/// names inside call trees (nearest enclosing qualified method, not the tree root).</summary>
public class EndpointTableAndOwnerTests
{
    private static DocGraph Parse(string md) => new MarkdownDocParser().Parse(md, "t.md");

    private static IEnumerable<DocEdge> Out(DocNode n, EdgeKind kind) => n.Outgoing.Where(e => e.Kind == kind);

    // ------------------------------------------------------------------ endpoint tables

    private const string EndpointTables = """
        # T

        ## 8. Endpoints

        | Endpoint | Handler | Request | Response | Statuses |
        |---|---|---|---|---|
        | `POST /api/UserReport/ExportReport` | `UserReportController.ExportReportAsync → ExportReportUseCase.ExportReportAsync` | `reportId`, `exportType` | the file | 200; 400; 401 |

        | Method and path | Caller | Handler | Returns |
        |---|---|---|---|
        | `GET /api/Thing` | `thing/api.ts` | `ThingController.GetAsync` | a thing |

        | Call | Section | Server | AF |
        |---|---|---|---|
        | `POST /api/UserReport/SetDefaultReport` | Report viewer | `UserReportController.SetDefaultReportAsync` | yes |

        | Method + route | Agency DB | Request | Response (200) | Other statuses |
        |---|---|---|---|---|
        | `GET /api/DefaultFilters` | no | - | PascalCase `StartDate` | 401 |

        | Call | Server | Returns |
        |---|---|---|
        | `GET /api/public/settings` | `PublicSettingsController` → `GeneralSettings`, `IAppVersionService` | `applicationTitle`, `version` |
        """;

    [Fact]
    public void Endpoint_Handler_Request_Response_Statuses_header_is_recognised()
    {
        var g = Parse(EndpointTables);
        var ep = g.Find("endpoint:post /api/userreport/exportreport");
        Assert.NotNull(ep);
        var handler = Assert.Single(Out(ep!, EdgeKind.Uses)).To;
        Assert.Equal("method:userreportcontroller.exportreportasync", handler.Id);
        Assert.Contains(Out(handler, EdgeKind.Calls), e => e.To.Id == "method:exportreportusecase.exportreportasync");
        Assert.Contains(ep.Notes, n => n.StartsWith("Request:") && n.Contains("reportId"));
        Assert.Contains(ep.Notes, n => n == "Response: the file");
        Assert.Contains(ep.Notes, n => n == "Statuses: 200; 400; 401");
    }

    [Fact]
    public void Method_and_path_with_Caller_and_Handler_columns_links_both_sides()
    {
        var g = Parse(EndpointTables);
        var ep = g.Find("endpoint:get /api/thing");
        Assert.NotNull(ep);
        Assert.Contains(Out(ep!, EdgeKind.Uses), e => e.To.Id == "method:thingcontroller.getasync");
        var caller = g.Nodes.Single(n => n.Kind == NodeKind.TypeScript && n.Title == "api.ts");
        Assert.Contains(Out(caller, EdgeKind.Contains), e => e.To == ep);
        Assert.Contains(ep.Notes, n => n == "Returns: a thing");
    }

    [Fact]
    public void Call_Section_Server_AF_header_puts_the_server_in_the_third_column()
    {
        var g = Parse(EndpointTables);
        var ep = g.Find("endpoint:post /api/userreport/setdefaultreport");
        Assert.NotNull(ep);
        Assert.Contains(Out(ep!, EdgeKind.Uses), e => e.To.Id == "method:userreportcontroller.setdefaultreportasync");
        Assert.Contains(ep.Notes, n => n == "Section: Report viewer");
        Assert.Contains(ep.Notes, n => n == "AF: yes");
        Assert.DoesNotContain(ep.Notes, n => n.StartsWith("Server:"));
    }

    [Fact]
    public void Method_plus_route_table_without_a_handler_column_still_yields_endpoints_with_notes()
    {
        var g = Parse(EndpointTables);
        var ep = g.Find("endpoint:get /api/defaultfilters");
        Assert.NotNull(ep);
        Assert.Empty(Out(ep!, EdgeKind.Uses));
        Assert.Contains(ep.Notes, n => n == "Agency DB: no");
        Assert.Contains(ep.Notes, n => n.StartsWith("Response:") && n.Contains("StartDate"));
        Assert.Contains(ep.Notes, n => n == "Statuses: 401");
        Assert.DoesNotContain(ep.Notes, n => n.StartsWith("Request:"));
    }

    [Fact]
    public void Original_Call_Server_Returns_shape_still_works()
    {
        var g = Parse(EndpointTables);
        var ep = g.Find("endpoint:get /api/public/settings");
        Assert.NotNull(ep);
        var server = Assert.Single(Out(ep!, EdgeKind.Uses)).To;
        Assert.Equal(NodeKind.Controller, server.Kind);
        Assert.Equal(2, Out(server, EdgeKind.Uses).Count());
        Assert.Contains(ep.Notes, n => n.StartsWith("Returns:") && n.Contains("applicationTitle"));
    }

    [Fact]
    public void Endpoint_tables_register_their_rows_as_flow_items_of_the_section()
    {
        var g = Parse(EndpointTables);
        var section = g.Sections.Single(s => s.Title.StartsWith("8."));
        var contained = Out(section, EdgeKind.Contains).Select(e => e.To.Id).ToList();
        Assert.Contains("endpoint:post /api/userreport/exportreport", contained);
        Assert.Contains("endpoint:get /api/defaultfilters", contained);
    }

    [Fact]
    public void A_table_whose_first_header_is_Caller_is_not_an_endpoint_table()
    {
        Assert.False(TableParser.EndpointTableLayout.IsEndpointHeader("caller"));
        Assert.True(TableParser.EndpointTableLayout.IsEndpointHeader("call"));
        Assert.True(TableParser.EndpointTableLayout.IsEndpointHeader("endpoint"));
        Assert.True(TableParser.EndpointTableLayout.IsEndpointHeader("method and path"));
        Assert.True(TableParser.EndpointTableLayout.IsEndpointHeader("method + route"));
        Assert.False(TableParser.EndpointTableLayout.IsEndpointHeader("piece"));
        Assert.False(TableParser.EndpointTableLayout.IsEndpointHeader("method"));
    }

    // ------------------------------------------------------------------ owner from the nearest qualified ancestor

    private const string NestedOwners = """
        # T

        ## 3. On the server

        ```text
        UserReportController.ExportReportAsync              [ApiController]
          ├─ RequireUserContext: IUserContextResolver.GetAsync
          └─ IExportReportUseCase.ExportReportAsync(reportId, format)
               └─ ExportAsync
                   ├─ GetRefusalAsync
                   │   └─ IUserReportRepository.IsAssignedAsync(userId, reportId)
                   │         no → ReportNotAvailable (8010)
                   ├─ IUserRepository.FindAsync(id)
                   │     none → RejectUnknownAsync → InvalidCredentials
                   └─ Ok(stream)
        ```
        """;

    [Fact]
    public void Bare_method_under_a_qualified_call_belongs_to_that_calls_owner_not_the_tree_root()
    {
        var g = Parse(NestedOwners);
        var exportAsync = g.Find("method:exportreportusecase.exportasync");
        Assert.NotNull(exportAsync);
        Assert.Equal("ExportReportUseCase", exportAsync!.Owner?.Title);
        Assert.Null(g.Find("method:userreportcontroller.exportasync"));

        var useCaseMethod = g.Find("method:exportreportusecase.exportreportasync")!;
        Assert.Contains(Out(useCaseMethod, EdgeKind.Calls), e => e.To == exportAsync);
    }

    [Fact]
    public void Deeper_bare_methods_keep_the_nearest_owner()
    {
        var g = Parse(NestedOwners);
        Assert.NotNull(g.Find("method:exportreportusecase.getrefusalasync"));
        Assert.Null(g.Find("method:userreportcontroller.getrefusalasync"));
        var ok = g.Find("method:exportreportusecase.ok");
        Assert.NotNull(ok);
        Assert.Null(g.Find("method:userreportcontroller.ok"));
    }

    [Fact]
    public void A_result_continuation_under_a_repository_call_stays_in_the_enclosing_method()
    {
        var g = Parse(NestedOwners);
        // "none → RejectUnknownAsync" under IUserRepository.FindAsync is handled by ExportReportUseCase, not by the repository.
        var reject = g.Find("method:exportreportusecase.rejectunknownasync");
        Assert.NotNull(reject);
        Assert.Null(g.Find("method:userrepository.rejectunknownasync"));
        var find = g.Find("method:userrepository.findasync")!;
        Assert.Contains(Out(find, EdgeKind.Calls), e => e.To == reject && e.Label == "none");
        Assert.Contains(Out(reject!, EdgeKind.Returns), e => e.To.Id == "outcome:invalidcredentials");
    }

    [Fact]
    public void The_tree_root_still_owns_its_direct_bare_children()
    {
        var g = Parse("""
            # T

            ## 4. `SignInCompletionService.CompleteAsync`

            ```text
            CompleteAsync(user, attempt)
              ├─ IRoleService.GetRoleByIdAsync(user.RoleID)
              └─ StartSessionAsync
                  └─ IAuthenticatedSessionService.StartAsync(user, role)
            ```
            """);
        Assert.NotNull(g.Find("method:signincompletionservice.startsessionasync"));
        Assert.Null(g.Find("method:roleservice.startsessionasync"));
    }

    [Fact]
    public void Reference_doc_owners_are_unchanged_by_the_ancestor_rule()
    {
        var g = new ReferenceDoc().Graph;
        Assert.NotNull(g.Find("method:signinusecase.signinasync"));
        Assert.NotNull(g.Find("method:signinusecase.rejectunknownloginasync"));
        Assert.Null(g.Find("method:userrepository.rejectunknownloginasync"));
        Assert.NotNull(g.Find("method:signinusecase.rejectcredentialsasync"));
        Assert.Null(g.Find("method:hashingprovider.rejectcredentialsasync"));
        Assert.NotNull(g.Find("method:signincompletionservice.startsessionasync"));
    }
}
