using DocsViewer.Core.Model;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace DocsViewer.Core.Parsing;

/// <summary>Step lists ("`X.method()`:" + list), prose paragraphs and prose bullets.</summary>
internal sealed class ProseParser
{
    private readonly GraphBuilder _b;
    private readonly TreeParser _trees;
    private readonly TableParser _tables;

    public ProseParser(GraphBuilder builder, TreeParser trees, TableParser tables)
    {
        _b = builder;
        _trees = trees;
        _tables = tables;
    }

    /// <summary>Tries to treat <paramref name="para"/> + <paramref name="next"/> as a step list. Returns true when
    /// <paramref name="next"/> was consumed.</summary>
    public bool TryStepList(ParagraphBlock para, Block? next, DocNode? ownerContext)
    {
        var text = InlineText.Plain(para);
        if (!text.EndsWith(':')) return false;
        if (next is not (ListBlock or Table)) return false;

        var spans = InlineText.CodeSpans(para);
        var line = para.Line + 1;
        DocNode? owner = null;
        CodeSpan? subjectSpan = null;
        foreach (var span in spans)
        {
            var r = Identifiers.Parse(span.Code);
            if (r == null) continue;
            if (subjectSpan == null && IsSubjectShape(r)) { subjectSpan = span; continue; }
            if (subjectSpan != null && span.PrecedingWord == "in" && r.Shape is RefShape.FilePath or RefShape.Word)
                owner = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
        }
        if (subjectSpan == null) return false;

        var subject = _b.Resolve(subjectSpan.Code, new ResolveOptions { Position = RefPosition.Prose, MemberOf = owner, OwnerContext = ownerContext }, line);
        if (subject == null) return false;

        _b.RegisterFlowItem(subject, line);
        var intro = text.TrimEnd(':').Trim();
        if (intro.Length > subjectSpan.Code.Length + 3) _b.Note(subject, intro);

        if (next is ListBlock list)
        {
            foreach (var item in list.OfType<ListItemBlock>()) StepItem(item, subject, ownerContext);
        }
        else if (next is Table table)
        {
            _tables.Emit(table, subject);
        }
        return true;
    }

    private static bool IsSubjectShape(Ref r)
        => r.Shape is RefShape.QualifiedMethod or RefShape.BareCall or RefShape.FilePath or RefShape.Endpoint or RefShape.Route
           || (r.Shape == RefShape.Word && Identifiers.IsTypeLike(r.Name));

    /// <summary>One list item of a step list: first call-like identifier → Calls child; further ones → Uses of that child;
    /// "mounts X" → Mounts; nested tables/trees hang off the child.</summary>
    private void StepItem(ListItemBlock item, DocNode subject, DocNode? ownerContext)
    {
        var line = item.Line + 1;
        var para = item.OfType<ParagraphBlock>().FirstOrDefault();
        var text = InlineText.Plain(para);
        DocNode? child = null;
        if (para != null)
        {
            foreach (var span in InlineText.CodeSpans(para))
            {
                var r = Identifiers.Parse(span.Code);
                if (r == null) continue;
                var word = span.PrecedingWord;
                if (word == "mounts" && r.Shape is RefShape.FilePath or RefShape.Word)
                {
                    var target = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose }, line);
                    if (target != null) _b.Link(child ?? subject, target, EdgeKind.Mounts, null, line);
                    continue;
                }
                if (child == null && IsCallLike(r, word))
                {
                    child = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, OwnerContext = ownerContext, CallHint = word == "calls" }, line);
                    if (child != null)
                    {
                        _b.Link(subject, child, EdgeKind.Calls, null, line);
                        _b.Describe(child, text);
                    }
                    continue;
                }
                if (child != null && (IsCallLike(r, word) || (r.Shape == RefShape.Word && Identifiers.HasTypeSuffix(r.Name))))
                {
                    var target = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, CallHint = true }, line);
                    if (target != null && target != child) _b.Link(child, target, EdgeKind.Uses, null, line);
                    continue;
                }
                var existing = _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, Create = false }, line);
                if (existing != null) _b.Mention(existing, line);
            }
        }
        if (child == null && text.Length > 0) _b.Note(subject, text);

        var anchor = child ?? subject;
        foreach (var block in item)
        {
            switch (block)
            {
                case ParagraphBlock p when p != para:
                    Prose(p, anchor);
                    break;
                case Table table:
                    _tables.Emit(table, anchor);
                    break;
                case FencedCodeBlock fence when TreeParser.LooksLikeTree(fence):
                    foreach (var root in _trees.Emit(fence, ownerContext))
                    {
                        _b.Link(anchor, root, root.Kind is NodeKind.Destination or NodeKind.Outcome ? EdgeKind.Returns : EdgeKind.Calls, null, fence.Line + 1);
                        _b.RegisterFlowItem(root, fence.Line + 1, chain: false);
                    }
                    break;
                case FencedCodeBlock fence:
                    foreach (var (_, t) in TreeParser.FenceLines(fence)) _b.Note(anchor, t);
                    break;
                case ListBlock nested:
                    foreach (var sub in nested.OfType<ListItemBlock>()) StepItem(sub, anchor, ownerContext);
                    break;
            }
        }
    }

    private static bool IsCallLike(Ref r, string? precedingWord)
    {
        switch (r.Shape)
        {
            case RefShape.BareCall:
            case RefShape.Endpoint:
                return true;
            case RefShape.QualifiedMethod:
                return r.OwnerIsType || (r.HasArgs && char.IsUpper(r.Name[0]));
            case RefShape.Word:
                return precedingWord is "calls" or "runs" or "invokes" && char.IsLower(r.Name[0]);
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ prose

    /// <summary>A paragraph (or bullet paragraph) that is not a step-list intro. A paragraph starting with a back-ticked
    /// identifier describes that node; otherwise it is a note on the anchor, and inline code adds Mentions.</summary>
    public void Prose(ParagraphBlock? para, DocNode? anchor = null)
    {
        if (para == null) return;
        var line = para.Line + 1;
        var text = InlineText.Plain(para);
        if (text.Length == 0) return;
        anchor ??= _b.CurrentFlowItem ?? _b.CurrentSection;

        var spans = InlineText.CodeSpans(para);
        DocNode? subject = null;
        DocNode? lastRoute = null;
        var resolved = new List<DocNode>();
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var r = Identifiers.Parse(span.Code);
            if (r == null) continue;
            var node = ResolveProse(r, line);
            if (node == null) continue;
            resolved.Add(node);
            if (i == 0 && span.Before.Trim().Length == 0) subject = node;

            // "/Duo", "DuoController.Index": the route is served by that controller / view.
            if (node.Kind == NodeKind.Route) lastRoute = node;
            else if (lastRoute != null && !span.Before.Contains('.') &&
                     (node.Kind == NodeKind.Controller || node.Kind == NodeKind.Html || (node.Kind == NodeKind.Method && node.Owner?.Kind == NodeKind.Controller)))
            {
                _b.Link(lastRoute, node, EdgeKind.Uses, null, line);
                lastRoute = null;
            }
        }

        if (subject != null)
        {
            _b.Describe(subject, text);
            foreach (var n in resolved.Where(n => n != subject)) _b.Link(subject, n, EdgeKind.Mentions, null, line);
        }
        else
        {
            _b.Note(anchor, text);
            foreach (var n in resolved) if (anchor != null && n != anchor) _b.Link(anchor, n, EdgeKind.Mentions, null, line);
        }
    }

    /// <summary>Prose resolution: existing nodes, plus new nodes for things that are unambiguous (paths, Type.Method, routes).</summary>
    private DocNode? ResolveProse(Ref r, int line)
    {
        var options = new ResolveOptions { Position = RefPosition.Prose, Create = false };
        switch (r.Shape)
        {
            case RefShape.FilePath when r.Path!.Contains('/') && r.Extension is "cs" or "ts" or "vue" or "cshtml" or "js":
            case RefShape.FilePath when !r.Path!.Contains('/') && r.Extension is "ts" or "vue" or "js":
            case RefShape.QualifiedMethod when r.OwnerIsType:
            case RefShape.Route:
            case RefShape.Endpoint:
            case RefShape.Browser:
                return _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, Create = true }, line);
            case RefShape.Word when Identifiers.IsTypeLike(r.Name):
                return _b.Resolve(r, new ResolveOptions { Position = RefPosition.Prose, Create = Identifiers.HasTypeSuffix(Identifiers.StripInterfacePrefix(r.Name)), CreateTypes = true }, line);
            default:
                return _b.Resolve(r, options, line);
        }
    }

    /// <summary>Prose bullets (not a step list): each bullet is handled like a paragraph; nested lists recurse.</summary>
    public void Bullets(ListBlock list, DocNode? anchor = null)
    {
        foreach (var item in list.OfType<ListItemBlock>())
        {
            foreach (var block in item)
            {
                switch (block)
                {
                    case ParagraphBlock p:
                        Prose(p, anchor);
                        break;
                    case ListBlock nested:
                        Bullets(nested, anchor);
                        break;
                    case Table table:
                        _tables.Emit(table, anchor ?? _b.CurrentFlowItem);
                        break;
                    case FencedCodeBlock fence when TreeParser.LooksLikeTree(fence):
                        foreach (var root in _trees.Emit(fence, null)) _b.RegisterFlowItem(root, fence.Line + 1);
                        break;
                }
            }
        }
    }
}
