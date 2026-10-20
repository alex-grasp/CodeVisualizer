using System.Text.RegularExpressions;
using DocsViewer.Core.Model;
using Markdig.Syntax;

namespace DocsViewer.Core.Parsing;

/// <summary>One line of a ```text call tree after pass 1 (structure only).</summary>
internal sealed class TreeLine
{
    public string Text { get; set; } = "";
    public string Raw { get; init; } = "";
    public int Column { get; init; }
    public int Line { get; init; }
    public bool IsConnector { get; init; }
    public bool IsRoot { get; init; }
    public List<TreeLine> Children { get; } = new();
}

/// <summary>Parses box-drawing call trees (and connector-less pipeline trees) from ```text fences into graph nodes.</summary>
internal sealed class TreeParser
{
    private static readonly Regex ConnectorRx = new(@"(├─+|└─+|\|--+|\+--+|`--+|\\--+)\s*", RegexOptions.Compiled);
    private static readonly Regex AnnotationSplitRx = new(@"\s{3,}", RegexOptions.Compiled);
    private static readonly Regex LabelThenCallRx = new(@"^(.*?):\s+(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex TrailingParenRx = new(@"^(.*?\S)\s+\(([^()]*)\)$", RegexOptions.Compiled);
    private static readonly Regex TrailingWordsRx = new(@"^(\S+(?:\(.*\))?)\s+([a-z][\w\s'-]*)$", RegexOptions.Compiled);

    private readonly GraphBuilder _b;
    private DocNode? _ownerContext;

    public TreeParser(GraphBuilder builder) => _b = builder;

    /// <summary>Lines of the fence as (1-based line, text) pairs.</summary>
    public static List<(int Line, string Text)> FenceLines(FencedCodeBlock fence)
    {
        var list = new List<(int, string)>();
        var lines = fence.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var sl = lines.Lines[i];
            var text = sl.Slice.ToString();
            var line = sl.Line > 0 ? sl.Line + 1 : fence.Line + 2 + i;
            list.Add((line, text));
        }
        return list;
    }

    /// <summary>True when the fence looks like a call tree: box-drawing connectors, or a root line followed by indented lines.</summary>
    public static bool LooksLikeTree(FencedCodeBlock fence)
    {
        var lines = FenceLines(fence).Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (lines.Count == 0) return false;
        if (lines.Any(l => ConnectorRx.IsMatch(l.Text))) return true;
        var first = lines[0].Text;
        if (first.Length == 0 || char.IsWhiteSpace(first[0])) return false;
        return Identifiers.Parse(HeadOf(first)) != null;
    }

    private static string HeadOf(string line)
    {
        var text = Identifiers.StripSectionLabel(line.Trim(), out _);
        var parts = AnnotationSplitRx.Split(text, 2);
        return Identifiers.SplitArrows(parts[0]).FirstOrDefault() ?? text;
    }

    // ------------------------------------------------------------------ pass 1: structure

    public static List<TreeLine> BuildStructure(FencedCodeBlock fence)
    {
        var roots = new List<TreeLine>();
        var stack = new List<TreeLine>();
        foreach (var (lineNo, rawLine) in FenceLines(fence))
        {
            var raw = rawLine.Replace("\t", "    ").TrimEnd();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var m = ConnectorRx.Match(raw);
            if (m.Success)
            {
                var col = m.Index;
                var text = raw[(m.Index + m.Length)..].Trim();
                var node = new TreeLine { Raw = raw, Text = text, Column = col, Line = lineNo, IsConnector = true };
                while (stack.Count > 0 && stack[^1].Column >= col) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) roots.Add(node); else stack[^1].Children.Add(node);
                stack.Add(node);
                continue;
            }

            var textCol = 0;
            while (textCol < raw.Length && (raw[textCol] == ' ' || raw[textCol] == '│' || raw[textCol] == '|')) textCol++;
            var body = raw[textCol..].Trim();
            if (body.Length == 0) continue;

            if (textCol == 0)
            {
                if (roots.Count > 0 && !IsRootWorthy(body))
                {
                    // "RequestVerificationToken: ..." under "POST /api/sign-in": continuation of the current root.
                    var root = roots[^1];
                    AttachContinuation(root, new TreeLine { Raw = raw, Text = body, Column = 1, Line = lineNo }, stack, 1);
                    continue;
                }
                var node = new TreeLine { Raw = raw, Text = body, Column = 0, Line = lineNo, IsRoot = true };
                roots.Add(node);
                stack.Clear();
                stack.Add(node);
                continue;
            }

            while (stack.Count > 1 && stack[^1].Column >= textCol) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
            {
                var node = new TreeLine { Raw = raw, Text = body, Column = textCol, Line = lineNo, IsRoot = true };
                roots.Add(node);
                stack.Add(node);
                continue;
            }
            var cont = new TreeLine { Raw = raw, Text = body, Column = textCol, Line = lineNo };
            AttachContinuation(stack[^1], cont, stack, textCol);
        }
        return roots;
    }

    private static void AttachContinuation(TreeLine parent, TreeLine cont, List<TreeLine> stack, int column)
    {
        var last = parent.Children.LastOrDefault();
        if (!cont.Text.StartsWith('→') && !cont.Text.StartsWith("->", StringComparison.Ordinal) && !IsRootWorthy(cont.Text))
        {
            // A wrapped note: the previous continuation at the same column is plain text that looks unfinished,
            // or both lines are plain prose (no identifier, no arrow) and this one starts in lower case.
            if (last != null && !last.IsConnector && last.Column == column && last.Children.Count == 0 &&
                (LooksUnfinished(last.Text) ||
                 (!IsRootWorthy(last.Text) && !Identifiers.ContainsArrow(last.Text) && !Identifiers.ContainsArrow(cont.Text) && char.IsLower(cont.Text[0]))))
            {
                last.Text = last.Text + " " + cont.Text;
                return;
            }
            if (last == null && LooksUnfinished(parent.Text))
            {
                parent.Text = parent.Text + " " + cont.Text;
                return;
            }
        }
        parent.Children.Add(cont);
        stack.Add(cont);
    }

    private static bool LooksUnfinished(string text)
    {
        var t = text.TrimEnd();
        if (t.EndsWith(',') || t.EndsWith(" and", StringComparison.Ordinal) || t.EndsWith(" or", StringComparison.Ordinal)
            || t.EndsWith(" the", StringComparison.Ordinal) || t.EndsWith(" a", StringComparison.Ordinal) || t.EndsWith(" with", StringComparison.Ordinal)
            || t.EndsWith(" of", StringComparison.Ordinal) || t.EndsWith(" to", StringComparison.Ordinal) || t.EndsWith(':'))
            return true;
        return t.Count(c => c == '(') > t.Count(c => c == ')');
    }

    /// <summary>A column-0 line is a root when something in it is identifier-like.</summary>
    private static bool IsRootWorthy(string body)
    {
        var text = Identifiers.StripSectionLabel(body, out _);
        var main = AnnotationSplitRx.Split(text, 2)[0];
        foreach (var seg in Identifiers.SplitArrows(main))
        {
            var r = Identifiers.Parse(StripTrailingParen(seg, out _));
            if (r != null && IsNodeWorthy(r)) return true;
            if (Identifiers.FindRedirectRoute(seg) != null) return true;
        }
        return false;
    }

    private static bool IsNodeWorthy(Ref r)
        => r.Shape != RefShape.Word || !Identifiers.IsBranchWord(r.Name);

    private static string StripTrailingParen(string seg, out string? note)
    {
        var m = TrailingParenRx.Match(seg.Trim());
        if (m.Success && Identifiers.Parse(m.Groups[1].Value) != null)
        {
            note = m.Groups[2].Value;
            return m.Groups[1].Value;
        }
        note = null;
        return seg.Trim();
    }

    // ------------------------------------------------------------------ pass 2: emission

    /// <summary>Emits the trees of a fence. Returns the root nodes in order.</summary>
    public List<DocNode> Emit(FencedCodeBlock fence, DocNode? ownerContext)
    {
        var roots = new List<DocNode>();
        foreach (var rootLine in BuildStructure(fence))
        {
            _ownerContext = ownerContext;
            var node = EmitRoot(rootLine);
            if (node != null) roots.Add(node);
        }
        return roots;
    }

    private DocNode? EmitRoot(TreeLine line)
    {
        var (main, annotation, sectionRef) = Split(line.Text);
        var segments = Identifiers.SplitArrows(main);
        if (segments.Count == 0) return null;

        DocNode? node = null;
        string? headLabel = null;
        var (headNode, label) = HeadNode(segments[0], line.Line, null, _ownerContext);
        var rest = segments.Skip(1).ToList();
        if (headNode != null)
        {
            node = headNode;
            headLabel = label;
        }
        else
        {
            // "identity provider → /auth/signin?code&state": the first node-worthy segment is the root.
            while (rest.Count > 0 && node == null)
            {
                var seg = StripTrailingParen(rest[0], out var note);
                rest.RemoveAt(0);
                var r = Identifiers.Parse(seg);
                if (r != null && IsNodeWorthy(r))
                {
                    node = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TreeNode, OwnerContext = _ownerContext }, line.Line);
                    if (node != null) _b.Note(node, note);
                }
                else
                {
                    var route = Identifiers.FindRedirectRoute(seg);
                    if (route != null) node = _b.Resolve(route, new ResolveOptions { Position = RefPosition.TreeNode }, line.Line);
                }
            }
            if (node == null) return null;
            _b.Note(node, line.Text);
        }

        _ownerContext ??= node.Owner;
        _b.AddSource(node, line.Line);
        if (headLabel != null) _b.Note(node, headLabel);
        var last = EmitChain(node, rest, null, line.Line, _ownerContext) ?? node;
        HandleAnnotation(node, annotation, line.Line);
        if (sectionRef != null) _b.DeferSectionLink(last, sectionRef, null, line.Line);
        foreach (var child in line.Children) EmitChild(child, node, null, _ownerContext);
        return node;
    }

    /// <summary>
    /// The type whose body a connector child of <paramref name="node"/> describes: the owner of a method
    /// (children of "IExportReportUseCase.ExportReportAsync" are ExportReportUseCase members), or the type itself
    /// when the node is a component ("AiQueryPanel" → its functions). Null for endpoints, routes, branches, outcomes.
    /// </summary>
    private static DocNode? BodyOwner(DocNode node)
    {
        if (node.Kind == NodeKind.Method) return node.Owner;
        return node.Kind is NodeKind.VueComponent or NodeKind.TypeScript or NodeKind.Html or NodeKind.Controller
            or NodeKind.Middleware or NodeKind.UseCase or NodeKind.Service or NodeKind.Repository or NodeKind.Client
            or NodeKind.Store or NodeKind.Mapper or NodeKind.Interface
            ? node
            : null;
    }

    /// <summary>Emits a connector child or a continuation line under <paramref name="attachTo"/>.
    /// <paramref name="enclosingCtx"/> is the owner context in effect for the line that produced <paramref name="attachTo"/>;
    /// a connector child is the body of <paramref name="attachTo"/> and takes its owner, while a continuation
    /// ("none → RejectUnknownLoginAsync" under a repository call) still belongs to the enclosing method.</summary>
    private void EmitChild(TreeLine line, DocNode attachTo, string? inheritedLabel, DocNode? enclosingCtx)
    {
        var (main, annotation, sectionRef) = Split(line.Text);
        if (main.Length == 0) return;
        var ctx = line.IsConnector ? BodyOwner(attachTo) ?? enclosingCtx : enclosingCtx;

        // "→ Result" continuation: results of the node this line continues.
        if (main.StartsWith('→') || main.StartsWith("->", StringComparison.Ordinal))
        {
            var segs = Identifiers.SplitArrows(main);
            var lastNode = EmitChain(attachTo, segs, inheritedLabel, line.Line, ctx);
            if (sectionRef != null && lastNode != null) _b.DeferSectionLink(lastNode, sectionRef, null, line.Line);
            if (annotation != null) _b.Note(lastNode ?? attachTo, annotation);
            foreach (var child in line.Children) EmitChild(child, lastNode ?? attachTo, inheritedLabel, ctx);
            return;
        }

        var segments = Identifiers.SplitArrows(main);
        var rest = segments.Skip(1).ToList();
        var headRef = Identifiers.Parse(segments[0]);
        var isImplementationChain = !line.IsConnector && rest.Count > 0 && headRef is { Shape: RefShape.Word } && Identifiers.IsTypeLike(headRef.Name);
        // "check → 302 /Login": a lone lower-case word followed by an arrow is a condition, not a call.
        var isConditionWord = rest.Count > 0 && headRef is { Shape: RefShape.Word } && char.IsLower(headRef.Name[0]);
        var (node, headLabel) = isImplementationChain || isConditionWord ? (null, null) : HeadNode(segments[0], line.Line, attachTo, ctx);

        if (node == null)
        {
            // Implementation + chain: "HttpAuthenticatedSessionService → LoginService.LoginUser(...)".
            if (isImplementationChain && headRef != null)
            {
                var impl = _b.ResolveType(headRef.Name, true, line.Line);
                if (impl != null)
                {
                    if (attachTo.Owner != null && attachTo.Owner != impl) _b.Link(attachTo.Owner, impl, EdgeKind.Implements, null, line.Line);
                    else if (attachTo.Kind != NodeKind.Method) _b.Link(attachTo, impl, EdgeKind.Implements, null, line.Line);
                    else _b.Link(attachTo, impl, EdgeKind.Uses, null, line.Line);
                }
                var head = EmitChain(attachTo, rest, inheritedLabel, line.Line, ctx);
                if (sectionRef != null && head != null) _b.DeferSectionLink(head, sectionRef, null, line.Line);
                foreach (var child in line.Children) EmitChild(child, head ?? attachTo, inheritedLabel, ctx);
                return;
            }

            // Branch line: collapse into labelled edges from attachTo (the innermost branch text is the label).
            var branchText = segments[0];
            var label = branchText;
            if (annotation != null && !Identifiers.ContainsArrow(annotation)) label = $"{label} {annotation}".Trim();
            var produced = false;
            if (rest.Count > 0)
            {
                var before = _b.Graph.Edges.Count;
                var lastNode = EmitChain(attachTo, rest, label, line.Line, ctx);
                produced = _b.Graph.Edges.Count > before || lastNode != null;
                if (sectionRef != null && lastNode != null) _b.DeferSectionLink(lastNode, sectionRef, null, line.Line);
            }
            else
            {
                produced = EmitEmbedded(attachTo, branchText, label, line.Line, ctx);
            }
            _b.Note(attachTo, line.Text);
            if (annotation != null && Identifiers.ContainsArrow(annotation)) EmitChain(attachTo, Identifiers.SplitArrows(annotation), label, line.Line, ctx);
            // Children of a collapsed branch still describe attachTo's body, so they keep this line's context.
            foreach (var child in line.Children) EmitChild(child, attachTo, branchText, ctx);
            _ = produced;
            return;
        }

        // Identifier line: Calls/Uses/Returns child of attachTo. The enclosing branch ("otherwise") is the label.
        var edgeLabel = inheritedLabel ?? headLabel;
        _b.Link(attachTo, node, KindFor(node, chain: false), edgeLabel, line.Line);
        _b.AddSource(node, line.Line);
        if (headLabel != null && edgeLabel != headLabel) _b.Note(node, headLabel);
        var chainEnd = EmitChain(node, rest, null, line.Line, ctx) ?? node;
        HandleAnnotation(node, annotation, line.Line);
        if (sectionRef != null) _b.DeferSectionLink(chainEnd, sectionRef, null, line.Line);
        foreach (var child in line.Children) EmitChild(child, node, null, ctx);
    }

    /// <summary>Resolves the head segment of a line into a node, or null when it is a branch. Also returns a label
    /// such as "reCAPTCHA gate" (from "label: Call") or "fails" (from "Call fails").</summary>
    private (DocNode? Node, string? Label) HeadNode(string head, int line, DocNode? attachTo, DocNode? ctx)
    {
        var options = new ResolveOptions { Position = RefPosition.TreeNode, OwnerContext = ctx };
        string? parenNote = null;
        var r = Identifiers.Parse(head);
        if (r == null)
        {
            // "settings (ActiveDirectoryGroups, ...)" is prose; only a real identifier may lose its parenthetical.
            r = Identifiers.Parse(StripTrailingParen(head, out parenNote));
            if (r is { Shape: RefShape.Word } && !Identifiers.IsTypeLike(r.Name)) r = null;
        }
        if (r != null && IsNodeWorthy(r))
        {
            var n = _b.Resolve(r, options, line);
            if (n != null) { _b.Note(n, parenNote); return (n, null); }
        }
        // "reCAPTCHA gate: IFailedSignInStore.IsHumanVerificationRequired(...)"
        var m = LabelThenCallRx.Match(head);
        if (m.Success)
        {
            var r2 = Identifiers.Parse(m.Groups[2].Value);
            if (r2 != null && IsNodeWorthy(r2) && r2.Shape is RefShape.QualifiedMethod or RefShape.BareCall or RefShape.Endpoint or RefShape.Route)
            {
                var n = _b.Resolve(r2, options, line);
                if (n != null) return (n, m.Groups[1].Value.Trim());
            }
        }
        // "IHashingProvider.Equals(password, hash) fails", "IEncryptionService.Decrypt each Duo key"
        m = TrailingWordsRx.Match(head);
        if (m.Success)
        {
            var r3 = Identifiers.Parse(m.Groups[1].Value);
            if (r3 != null && IsNodeWorthy(r3) && r3.Shape is RefShape.QualifiedMethod or RefShape.BareCall)
            {
                var n = _b.Resolve(r3, options, line);
                if (n != null) return (n, m.Groups[2].Value.Trim());
            }
        }
        return (null, null);
    }

    /// <summary>Links "→ A → B" segments from <paramref name="from"/>. Returns the last node linked.</summary>
    private DocNode? EmitChain(DocNode from, List<string> segments, string? label, int line, DocNode? ctx)
    {
        var current = from;
        DocNode? last = null;
        foreach (var rawSeg in segments)
        {
            if (rawSeg.Length == 0) continue;
            string? note = null;
            var r = Identifiers.Parse(rawSeg);
            if (r == null) r = Identifiers.Parse(StripTrailingParen(rawSeg, out note));
            DocNode? target = null;
            if (r != null && IsNodeWorthy(r))
                target = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TreeResult, OwnerContext = ctx }, line);
            if (target == null)
            {
                var route = Identifiers.FindRedirectRoute(rawSeg);
                if (route != null)
                {
                    target = _b.Resolve(route, new ResolveOptions { Position = RefPosition.TreeResult }, line);
                    if (target != null)
                    {
                        _b.Link(current, target, EdgeKind.Redirects, label ?? rawSeg, line);
                        _b.Note(current, rawSeg);
                        last = target;
                        continue;
                    }
                }
                _b.Note(current, rawSeg);
                continue;
            }
            if (target == current) continue;
            _b.Link(current, target, KindFor(target, chain: true), label, line);
            _b.Note(target, note);
            label = null;
            current = target;
            last = target;
        }
        return last;
    }

    /// <summary>Calls / Returns edges for identifiers embedded in a branch line ("audit SignedIn, set x, IUserRepository.UpdateUserAsync").</summary>
    private bool EmitEmbedded(DocNode attachTo, string text, string? label, int line, DocNode? ctx)
    {
        var produced = false;
        var segments = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var seg in segments)
        {
            var r = Identifiers.Parse(seg);
            if (r != null && IsNodeWorthy(r) && r.Shape is RefShape.QualifiedMethod or RefShape.BareCall or RefShape.Endpoint)
            {
                var n = _b.Resolve(r, new ResolveOptions { Position = RefPosition.TreeNode, OwnerContext = ctx }, line);
                if (n != null) { _b.Link(attachTo, n, KindFor(n, false), label, line); produced = true; }
            }
        }
        foreach (var call in Identifiers.FindEmbeddedCalls(text))
        {
            if (!call.Contains('(') && !call.EndsWith("Async", StringComparison.Ordinal)) continue;
            var n = _b.Resolve(call, new ResolveOptions { Position = RefPosition.TreeNode, OwnerContext = ctx }, line);
            if (n != null && n != attachTo) { _b.Link(attachTo, n, EdgeKind.Calls, label, line); produced = true; }
        }
        foreach (Match m in Regex.Matches(text, @"\b[A-Z]\w*Destination\b"))
        {
            var n = _b.Resolve(m.Value, new ResolveOptions { Position = RefPosition.TreeResult }, line);
            if (n != null) { _b.Link(attachTo, n, EdgeKind.Returns, label, line); produced = true; }
        }
        return produced;
    }

    private void HandleAnnotation(DocNode node, string? annotation, int line)
    {
        if (string.IsNullOrWhiteSpace(annotation)) return;
        var r = Identifiers.Parse(annotation);
        if (r is { Shape: RefShape.Word } && Identifiers.IsTypeLike(r.Name))
        {
            var impl = _b.ResolveType(r.Name, true, line);
            if (impl != null)
            {
                var iface = node.Owner ?? (node.Kind != NodeKind.Method ? node : null);
                if (iface != null && iface != impl) { _b.Link(iface, impl, EdgeKind.Implements, null, line); return; }
            }
        }
        var segs = Identifiers.SplitArrows(annotation);
        var head = Identifiers.Parse(segs[0]);
        if (head != null && IsNodeWorthy(head) && (segs.Count > 1 || head.Shape is RefShape.QualifiedMethod or RefShape.BareCall or RefShape.Endpoint))
        {
            EmitChain(node, segs, null, line, BodyOwner(node) ?? _ownerContext);
            return;
        }
        _b.Note(node, annotation);
    }

    private static EdgeKind KindFor(DocNode target, bool chain) => target.Kind switch
    {
        NodeKind.Method or NodeKind.Endpoint => EdgeKind.Calls,
        NodeKind.Route => chain ? EdgeKind.Redirects : EdgeKind.Uses,
        NodeKind.Destination or NodeKind.Outcome => EdgeKind.Returns,
        NodeKind.Browser => EdgeKind.Then,
        _ => EdgeKind.Uses,
    };

    /// <summary>Splits a line into (main text, right-hand annotation separated by 3+ spaces, "(section 6a)" reference).</summary>
    private static (string Main, string? Annotation, string? SectionRef) Split(string text)
    {
        var t = Identifiers.StripSectionLabel(text.Trim(), out var sectionRef);
        var parts = AnnotationSplitRx.Split(t, 2);
        if (parts.Length == 2)
        {
            var second = parts[1].Trim();
            if (second.StartsWith('→') || second.StartsWith("->", StringComparison.Ordinal))
                return ($"{parts[0].Trim()} {second}", null, sectionRef);
            return (parts[0].Trim(), second.Length > 0 ? second : null, sectionRef);
        }
        return (t, null, sectionRef);
    }
}
