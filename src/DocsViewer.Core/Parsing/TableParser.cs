using System.Text.RegularExpressions;
using DocsViewer.Core.Model;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace DocsViewer.Core.Parsing;

/// <summary>Pieces table, call tables, destination / status tables and generic tables.</summary>
internal sealed class TableParser
{
    private readonly GraphBuilder _b;

    public TableParser(GraphBuilder builder) => _b = builder;

    /// <summary>Emits a table. <paramref name="parent"/> is the step-list child or subject the table sits under, if any.</summary>
    public void Emit(Table table, DocNode? parent)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return;
        var header = rows[0].IsHeader ? rows[0] : null;
        var headerTexts = header == null ? new List<string>() : header.OfType<TableCell>().Select(c => InlineText.Plain(c).Trim()).ToList();
        var headerCells = headerTexts.Select(t => t.ToLowerInvariant()).ToList();
        var body = rows.Where(r => !r.IsHeader).ToList();

        if (headerCells.Count >= 3 && headerCells[0].Contains("piece") && headerCells[1].Contains("where") && headerCells[2].Contains("what"))
        {
            foreach (var row in body) PiecesRow(row);
            return;
        }
        if (headerCells.Count >= 2 && EndpointTableLayout.IsEndpointHeader(headerCells[0]))
        {
            var layout = EndpointTableLayout.From(headerCells, headerTexts);
            foreach (var row in body) EndpointRow(row, parent, layout);
            return;
        }
        if (headerCells.Count >= 2 && headerCells[0].Contains("destination") && headerCells[1].Replace(" ", "").Contains("nexturl"))
        {
            foreach (var row in body) DestinationRow(row, parent);
            return;
        }
        if (headerCells.Count >= 2 && headerCells[0].Contains("code") && headerCells[1].Contains("status"))
        {
            foreach (var row in body) StatusRow(row, parent);
            return;
        }
        foreach (var row in body) GenericRow(row, parent);
    }

    private static List<TableCell> Cells(TableRow row) => row.OfType<TableCell>().ToList();

    // ------------------------------------------------------------------ pieces

    private void PiecesRow(TableRow row)
    {
        var cells = Cells(row);
        if (cells.Count < 2) return;
        var line = row.Line + 1;
        var piece = InlineText.Plain(cells[0]);
        var whereSpans = InlineText.CodeSpans(cells[1]);
        if (whereSpans.Count == 0)
        {
            var plain = InlineText.Plain(cells[1]);
            if (plain.Length > 0) whereSpans.Add(new CodeSpan(plain, "", ""));
        }

        DocNode? primary = null;
        DocNode? previous = null;
        foreach (var span in whereSpans)
        {
            var r = Identifiers.Parse(span.Code);
            if (r == null) continue;
            var node = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TableCell }, line);
            if (node == null) continue;
            var component = node.Kind == NodeKind.Method ? node.Owner : node;
            if (component != null)
            {
                _b.MarkPieceComponent(component);
                if (_b.CurrentSection != null) _b.Link(_b.CurrentSection, component, EdgeKind.Contains, null, line);
                component.Section ??= _b.CurrentSection;
            }
            if (previous != null)
            {
                var kind = span.Before.Contains('→') || span.Before.Contains("->", StringComparison.Ordinal) ? EdgeKind.Calls : EdgeKind.Uses;
                _b.Link(previous, node, kind, null, line);
            }
            else
            {
                primary = node;
            }
            previous = node;
        }
        if (primary == null) return;

        if (!string.IsNullOrWhiteSpace(piece)) primary.Subtitle ??= piece;
        if (cells.Count >= 3)
        {
            var what = InlineText.Plain(cells[2]);
            _b.Describe(primary, what);
            foreach (var span in InlineText.CodeSpans(cells[2])) DescriptionSpan(primary, span, line);
        }
    }

    /// <summary>Back-ticked members in a "What it does" cell: functions become members, endpoints are served/contained,
    /// "calls x" is a call and "mounts X.vue" a mount.</summary>
    private void DescriptionSpan(DocNode component, CodeSpan span, int line)
    {
        var r = Identifiers.Parse(span.Code);
        if (r == null) return;
        var word = span.PrecedingWord;
        switch (r.Shape)
        {
            case RefShape.BareCall:
            case RefShape.Word when word == "calls" && char.IsLower(r.Name[0]):
                if (word == "calls")
                {
                    var callee = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, CallHint = true }, line);
                    if (callee != null) _b.Link(component, callee, EdgeKind.Calls, null, line);
                }
                else
                {
                    _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, MemberOf = component }, line);
                }
                break;
            case RefShape.Endpoint:
                var endpoint = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TableCell }, line);
                if (endpoint == null) break;
                if (component.Kind == NodeKind.Controller) _b.Link(endpoint, component, EdgeKind.Uses, null, line);
                else _b.Link(component, endpoint, EdgeKind.Contains, null, line);
                break;
            case RefShape.FilePath when word == "mounts":
                var target = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
                if (target != null) _b.Link(component, target, EdgeKind.Mounts, null, line);
                break;
            case RefShape.QualifiedMethod when r.OwnerIsType:
                var method = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
                if (method != null && method != component) _b.Link(component, method, EdgeKind.Calls, null, line);
                break;
        }
    }

    // ------------------------------------------------------------------ endpoint tables

    /// <summary>
    /// Column roles of an endpoint table. The first column names the endpoint; the others are found by header text, so
    /// "Call | Server | Returns", "Endpoint | Handler | Request | Response | Statuses", "Method and path | Caller | Handler | Returns",
    /// "Call | Section | Server | AF" and "Method + route | Agency DB | Request | Response (200) | Other statuses" all work.
    /// </summary>
    internal sealed class EndpointTableLayout
    {
        public int Handler { get; private init; } = -1;
        public int Caller { get; private init; } = -1;
        /// <summary>Lower-cased header cells.</summary>
        public List<string> Headers { get; private init; } = new();
        /// <summary>Header cells as written, for note labels.</summary>
        public List<string> HeaderTexts { get; private init; } = new();

        public static bool IsEndpointHeader(string h)
        {
            h = h.Trim();
            if (h.StartsWith("caller", StringComparison.Ordinal)) return false;
            return h.Contains("endpoint") || h.StartsWith("call") || h.Contains("route")
                || (h.Contains("method") && (h.Contains("path") || h.Contains("verb")));
        }

        public static EndpointTableLayout From(List<string> headerCells, List<string>? headerTexts = null)
        {
            var handler = -1;
            var caller = -1;
            for (var i = 1; i < headerCells.Count; i++)
            {
                var h = headerCells[i];
                if (handler < 0 && (h.Contains("handler") || h.Contains("server") || h.Contains("controller") || h.Contains("entry point"))) handler = i;
                else if (caller < 0 && h.Contains("caller")) caller = i;
            }
            return new EndpointTableLayout { Handler = handler, Caller = caller, Headers = headerCells, HeaderTexts = headerTexts ?? headerCells };
        }

        /// <summary>The note prefix for a non-handler, non-caller column: "Returns", "Request", "Response", "Statuses", or the header text.</summary>
        public string NoteLabel(int column)
        {
            var h = Headers[column];
            if (h.Contains("return")) return "Returns";
            if (h.Contains("request")) return "Request";
            if (h.Contains("response") || h.Contains("success")) return "Response";
            if (h.Contains("status")) return "Statuses";
            var text = column < HeaderTexts.Count ? HeaderTexts[column] : h;
            return text.Length == 0 ? "Note" : text;
        }
    }

    private void EndpointRow(TableRow row, DocNode? parent, EndpointTableLayout layout)
    {
        var cells = Cells(row);
        if (cells.Count < 2) return;
        var line = row.Line + 1;
        var callSpan = InlineText.CodeSpans(cells[0]).FirstOrDefault()?.Code ?? InlineText.Plain(cells[0]);
        var endpoint = _b.Resolve(callSpan, new ResolveOptions { Position = RefPosition.TableCell }, line);
        if (endpoint == null) return;
        if (parent != null) _b.Link(parent, endpoint, EdgeKind.Calls, null, line);
        else _b.RegisterFlowItem(endpoint, line);

        for (var i = 1; i < cells.Count && i < layout.Headers.Count; i++)
        {
            if (i == layout.Handler) HandlerCell(endpoint, cells[i], line);
            else if (i == layout.Caller) CallerCell(endpoint, cells[i], line);
            else
            {
                var text = InlineText.Plain(cells[i]);
                if (text.Length > 0 && text != "-") _b.Note(endpoint, $"{layout.NoteLabel(i)}: {text}");
            }
        }
    }

    /// <summary>"Controller.Method → UseCase.Method, IService": the first span serves the endpoint; the rest are what it calls or uses.</summary>
    private void HandlerCell(DocNode endpoint, TableCell cell, int line)
    {
        // "`A.M → B.N`" (one span with the arrow inside) and "`A.M` → `B.N`, `C`" (several spans) both list A.M then B.N.
        var parts = InlineText.CodeSpans(cell)
            .SelectMany(span => Identifiers.SplitArrows(span.Code))
            .SelectMany(SplitTopLevelCommas)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        DocNode? server = null;
        foreach (var part in parts)
        {
            var node = _b.Resolve(part, new ResolveOptions { Position = RefPosition.TableCell }, line);
            if (node == null) continue;
            if (server == null)
            {
                server = node;
                _b.Link(endpoint, server, EdgeKind.Uses, null, line);
            }
            else
            {
                _b.Link(server, node, node.Kind == NodeKind.Method ? EdgeKind.Calls : EdgeKind.Uses, null, line);
            }
        }
        if (server == null)
        {
            var text = InlineText.Plain(cell);
            if (text.Length > 0 && text != "-") _b.Note(endpoint, "Server: " + text);
        }
    }

    /// <summary>Splits "A.M(x, y), B.N" into "A.M(x, y)" and "B.N": commas inside parentheses or brackets are kept.</summary>
    private static IEnumerable<string> SplitTopLevelCommas(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>"export-report/api.ts" or "api.exportReport()": the client code that sends the request.</summary>
    private void CallerCell(DocNode endpoint, TableCell cell, int line)
    {
        var spans = InlineText.CodeSpans(cell);
        foreach (var span in spans)
        {
            var node = _b.Resolve(span.Code, new ResolveOptions { Position = RefPosition.TableCell, CallHint = true }, line);
            if (node == null || node == endpoint) continue;
            if (node.Kind == NodeKind.Method) _b.Link(node, endpoint, EdgeKind.Calls, null, line);
            else if (node.Kind == NodeKind.Controller) _b.Link(endpoint, node, EdgeKind.Uses, null, line);
            else _b.Link(node, endpoint, EdgeKind.Contains, null, line);
        }
        if (spans.Count == 0)
        {
            var text = InlineText.Plain(cell);
            if (text.Length > 0 && text != "-") _b.Note(endpoint, "Caller: " + text);
        }
    }

    // ------------------------------------------------------------------ Destination | nextUrl

    private void DestinationRow(TableRow row, DocNode? parent)
    {
        var cells = Cells(row);
        if (cells.Count < 2) return;
        var line = row.Line + 1;
        var first = InlineText.CodeSpans(cells[0]).FirstOrDefault()?.Code ?? InlineText.Plain(cells[0]);
        var destination = _b.Resolve(first, new ResolveOptions { Position = RefPosition.TableCell }, line);
        if (destination == null) return;
        if (parent != null) _b.Link(parent, destination, EdgeKind.Uses, null, line);

        var urlText = InlineText.Plain(cells[1]);
        var linked = false;
        foreach (var span in InlineText.CodeSpans(cells[1]))
        {
            var r = Identifiers.Parse(span.Code);
            if (r is not { Shape: RefShape.Route }) continue;
            var route = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TableCell }, line);
            if (route == null) continue;
            var label = urlText.Replace(r.Path!, "", StringComparison.OrdinalIgnoreCase).Trim(' ', ',', ';');
            _b.Link(destination, route, EdgeKind.Redirects, label.Length > 0 ? label : null, line);
            linked = true;
        }
        if (!linked && urlText.Length > 0) _b.Note(destination, "nextUrl: " + urlText);
        else if (urlText.Length > 0) _b.Note(destination, "nextUrl: " + urlText);
    }

    // ------------------------------------------------------------------ Error code | Status

    private void StatusRow(TableRow row, DocNode? parent)
    {
        var cells = Cells(row);
        if (cells.Count < 2) return;
        var line = row.Line + 1;
        var status = InlineText.Plain(cells[1]);
        var spans = InlineText.CodeSpans(cells[0]);
        if (spans.Count == 0)
        {
            _b.Note(parent ?? _b.CurrentFlowItem ?? _b.CurrentSection, $"{InlineText.Plain(cells[0])}: {status}");
            return;
        }
        foreach (var span in spans)
        {
            var outcome = _b.Resolve(span.Code, new ResolveOptions { Position = RefPosition.TreeResult }, line);
            if (outcome == null) continue;
            if (Regex.IsMatch(status, @"^\d{3}$")) _b.Note(outcome, "HTTP " + status);
            else _b.Note(outcome, "Status: " + status);
            if (parent != null) _b.Link(parent, outcome, EdgeKind.Returns, null, line);
        }
    }

    // ------------------------------------------------------------------ anything else

    private void GenericRow(TableRow row, DocNode? parent)
    {
        var cells = Cells(row);
        if (cells.Count == 0) return;
        var line = row.Line + 1;
        var first = InlineText.CodeSpans(cells[0]).FirstOrDefault()?.Code ?? InlineText.Plain(cells[0]);
        var node = _b.Resolve(first, new ResolveOptions { Position = RefPosition.TableCell }, line);
        var target = node ?? parent ?? _b.CurrentFlowItem ?? _b.CurrentSection;
        if (node != null && parent != null) _b.Link(parent, node, EdgeKind.Uses, null, line);
        for (var i = node != null ? 1 : 0; i < cells.Count; i++)
        {
            var text = InlineText.Plain(cells[i]);
            if (text.Length > 0) _b.Note(target, text);
        }
    }
}
