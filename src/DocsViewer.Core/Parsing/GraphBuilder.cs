using DocsViewer.Core.Model;

namespace DocsViewer.Core.Parsing;

/// <summary>Where a reference was found; changes how an ambiguous word is interpreted.</summary>
internal enum RefPosition
{
    /// <summary>A line (or the left side of "X → Y") inside a ```text call tree.</summary>
    TreeNode,
    /// <summary>The right side of "→" inside a call tree: outcomes, destinations, redirects.</summary>
    TreeResult,
    /// <summary>Inline code in a paragraph or bullet.</summary>
    Prose,
    /// <summary>A table cell.</summary>
    TableCell,
}

internal sealed class ResolveOptions
{
    public DocNode? OwnerContext { get; init; }
    public RefPosition Position { get; init; } = RefPosition.Prose;
    /// <summary>When set, owner-less functions become members of this component.</summary>
    public DocNode? MemberOf { get; init; }
    /// <summary>Create nodes that do not exist yet. Prose usually only resolves existing ones.</summary>
    public bool Create { get; init; } = true;
    /// <summary>Allow creating a type node from a bare PascalCase word (table cells, tree lines).</summary>
    public bool CreateTypes { get; init; } = true;
    /// <summary>Allow creating a function node from a bare lower-case word (after "calls").</summary>
    public bool CallHint { get; init; }
}

/// <summary>Owns the graph while it is being built: resolution of references to nodes, flow items, sources,
/// components and the final entry point computation.</summary>
internal sealed class GraphBuilder
{
    private readonly List<DocNode> _flowItems = new();
    private readonly Dictionary<DocNode, List<DocNode>> _flowItemsBySection = new();
    private readonly List<DocNode> _pieceComponents = new();
    private readonly List<DocNode> _implicitComponents = new();
    private readonly List<(DocNode From, string SectionRef, string? Label, int Line)> _pendingSectionLinks = new();

    public GraphBuilder(DocGraph graph, DocNode root)
    {
        Graph = graph;
        Root = root;
    }

    public DocGraph Graph { get; }
    public DocNode Root { get; }
    public DocNode? CurrentSection { get; private set; }
    public DocNode? CurrentFlowItem { get; private set; }
    public string SectionTitle => CurrentSection?.Title ?? Graph.Title;

    // ------------------------------------------------------------------ sections and flow items

    public void EnterSection(DocNode section)
    {
        CurrentSection = section;
        CurrentFlowItem = null;
        if (!_flowItemsBySection.ContainsKey(section)) _flowItemsBySection[section] = new List<DocNode>();
    }

    public IReadOnlyList<DocNode> FlowItemsOf(DocNode section)
        => _flowItemsBySection.TryGetValue(section, out var l) ? l : Array.Empty<DocNode>();

    /// <summary>Makes <paramref name="node"/> a flow item of the current section: Contains child of the section,
    /// Then-chained from the previous flow item when nothing else leads to it, and the current flow item.</summary>
    public void RegisterFlowItem(DocNode node, int line, bool chain = true)
    {
        if (node.Kind is NodeKind.Section or NodeKind.Document) return;
        var section = CurrentSection;
        if (section != null)
        {
            var list = _flowItemsBySection[section];
            var previous = list.LastOrDefault();
            if (!list.Contains(node))
            {
                Link(section, node, EdgeKind.Contains, null, line);
                list.Add(node);
            }
            node.Section ??= section;
            if (chain && previous != null && previous != node && !HasStrongIncoming(node))
                Link(previous, node, EdgeKind.Then, null, line);
        }
        if (!_flowItems.Contains(node)) _flowItems.Add(node);
        CurrentFlowItem = node;
    }

    public void SetCurrentFlowItem(DocNode? node) => CurrentFlowItem = node;

    public static bool IsStrong(EdgeKind kind)
        => kind is EdgeKind.Calls or EdgeKind.Then or EdgeKind.Uses or EdgeKind.Mounts or EdgeKind.Redirects or EdgeKind.Returns;

    public static bool HasStrongIncoming(DocNode node) => node.Incoming.Any(e => IsStrong(e.Kind));

    /// <summary>Remembers "(section 6a)" so the node can be linked to that section's first flow item at the end.</summary>
    public void DeferSectionLink(DocNode from, string sectionRef, string? label, int line)
        => _pendingSectionLinks.Add((from, sectionRef, label, line));

    // ------------------------------------------------------------------ edges, notes, sources

    public DocEdge? Link(DocNode from, DocNode to, EdgeKind kind, string? label = null, int line = 0)
    {
        if (ReferenceEquals(from, to)) return null;
        var source = line > 0 ? new SourceRef(SectionTitle, line, LineText(line)) : null;
        return Graph.Link(from, to, kind, label, source);
    }

    /// <summary>Links from the current flow item (or section) with a weak Mentions edge.</summary>
    public void Mention(DocNode node, int line)
    {
        var from = CurrentFlowItem ?? CurrentSection;
        if (from == null || from == node) return;
        Link(from, node, EdgeKind.Mentions, null, line);
    }

    public void Note(DocNode? node, string? text)
    {
        if (node == null || string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (!node.Notes.Contains(t) && !string.Equals(node.Description, t, StringComparison.Ordinal)) node.Notes.Add(t);
    }

    public void Describe(DocNode? node, string? text)
    {
        if (node == null || string.IsNullOrWhiteSpace(text)) return;
        var t = text.Trim();
        if (node.Description == null) node.Description = t;
        else Note(node, t);
    }

    public void AddSource(DocNode node, int line, string? text = null)
    {
        if (line <= 0) return;
        if (node.Sources.Any(s => s.Line == line)) return;
        node.Sources.Add(new SourceRef(SectionTitle, line, (text ?? LineText(line)).Trim()));
    }

    public string LineText(int line)
        => line >= 1 && line <= Graph.Lines.Count ? Graph.Lines[line - 1] : string.Empty;

    // ------------------------------------------------------------------ components

    public void MarkPieceComponent(DocNode node)
    {
        _implicitComponents.Remove(node);
        if (!_pieceComponents.Contains(node)) _pieceComponents.Add(node);
    }

    private void MarkImplicitComponent(DocNode node)
    {
        if (!IsComponentKind(node.Kind)) return;
        if (_pieceComponents.Contains(node) || _implicitComponents.Contains(node)) return;
        _implicitComponents.Add(node);
    }

    public static bool IsComponentKind(NodeKind kind)
        => kind is NodeKind.VueComponent or NodeKind.TypeScript or NodeKind.Html or NodeKind.Controller or NodeKind.Middleware
            or NodeKind.UseCase or NodeKind.Service or NodeKind.Repository or NodeKind.Client or NodeKind.Store or NodeKind.Mapper
            or NodeKind.Interface or NodeKind.File or NodeKind.Concept or NodeKind.Setting;

    // ------------------------------------------------------------------ resolution

    public DocNode? Resolve(string? text, ResolveOptions options, int line)
    {
        var r = Identifiers.Parse(text);
        return r == null ? null : Resolve(r, options, line);
    }

    /// <summary>Turns a parsed reference into a graph node (creating it when allowed), adds alias and source.</summary>
    public DocNode? Resolve(Ref r, ResolveOptions o, int line)
    {
        DocNode? node = r.Shape switch
        {
            RefShape.Browser => ResolveBrowser(r, o, line),
            RefShape.Endpoint => ResolveEndpoint(r, o, line),
            RefShape.Route => ResolveRoute(r, o, line),
            RefShape.FilePath => ResolveFile(r, o, line),
            RefShape.Setting => GetOrCreate("setting:" + r.Name.ToLowerInvariant(), r.Name, NodeKind.Setting, o.Create),
            RefShape.QualifiedMethod => ResolveQualified(r, o, line),
            RefShape.BareCall => ResolveBareCall(r, o, line),
            RefShape.Word => ResolveWord(r, o, line),
            _ => null,
        };
        if (node == null) return null;
        if (!string.Equals(node.Title, r.Text, StringComparison.Ordinal)) node.Aliases.Add(r.Text);
        AddSource(node, line);
        node.Section ??= CurrentSection;
        return node;
    }

    private DocNode? ResolveBrowser(Ref r, ResolveOptions o, int line)
    {
        var id = $"browser:{r.Verb!.ToLowerInvariant()} {r.Path!.ToLowerInvariant()}";
        var node = GetOrCreate(id, r.Text, NodeKind.Browser, o.Create);
        if (node == null) return null;
        var route = GetOrCreate("route:" + r.Path.ToLowerInvariant(), r.Path, NodeKind.Route, true)!;
        Link(node, route, EdgeKind.Uses, null, line);
        if (r.Trailing != null) Note(node, r.Trailing);
        return node;
    }

    private DocNode? ResolveEndpoint(Ref r, ResolveOptions o, int line)
    {
        var id = $"endpoint:{r.Verb!.ToLowerInvariant()} {r.Path!.ToLowerInvariant()}";
        var node = GetOrCreate(id, $"{r.Verb} {r.Path}", NodeKind.Endpoint, o.Create);
        if (node == null) return null;
        if (r.Trailing != null) Note(node, r.Trailing.Trim('(', ')', ' '));
        if (r.Query != null) Note(node, "query: " + r.Query);
        return node;
    }

    private DocNode? ResolveRoute(Ref r, ResolveOptions o, int line)
    {
        var path = r.Path!;
        if (path.Length > 1) path = path.TrimEnd('/');
        var node = GetOrCreate("route:" + path.ToLowerInvariant(), path, NodeKind.Route, o.Create);
        if (node != null && r.Query != null) Note(node, "query: " + r.Query);
        return node;
    }

    private DocNode? ResolveFile(Ref r, ResolveOptions o, int line)
    {
        var path = r.Path!.Replace('\\', '/');
        var bare = !path.Contains('/');
        if (bare)
        {
            // "main.ts" / "LoginPage.vue": the unique component whose path ends with it.
            var suffix = "/" + path;
            var matches = Graph.Nodes.Where(n => n.Kind != NodeKind.Method && n.Path != null && n.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
        }
        var key = Identifiers.TypeKey(r.Name, bare ? null : path, r.Extension);
        var kind = Identifiers.TypeKindFromName(r.Name, r.Extension);
        var title = r.Extension == "cs" ? r.Name : $"{r.Name}.{r.Extension}";
        var node = GetOrCreate("type:" + key, title, kind, o.Create);
        if (node == null) return null;
        if (!bare && node.Path == null) node.Path = r.Path;
        MarkImplicitComponent(node);
        return node;
    }

    /// <summary>Gets or creates the type node for a (possibly I-prefixed) type name.</summary>
    public DocNode? ResolveType(string typeText, bool create, int line, NodeKind? kindHint = null)
    {
        var stripped = Identifiers.StripInterfacePrefix(typeText);
        var kind = kindHint ?? Identifiers.TypeKindFromName(stripped);
        var id = "type:" + stripped.ToLowerInvariant();
        var existing = Graph.Find(id);
        if (existing == null)
        {
            // A destination / outcome / setting known under its own prefix wins over a new type node.
            existing = Graph.Find("destination:" + stripped.ToLowerInvariant()) ?? Graph.Find("outcome:" + stripped.ToLowerInvariant());
            if (existing != null) return existing;
            if (!create) return null;
            if (kind == NodeKind.Destination) return GetOrCreate("destination:" + stripped.ToLowerInvariant(), stripped, kind, true);
        }
        var node = GetOrCreate(id, stripped, kind, true)!;
        if (!string.Equals(node.Title, typeText, StringComparison.Ordinal)) node.Aliases.Add(typeText);
        AddSource(node, line);
        node.Section ??= CurrentSection;
        MarkImplicitComponent(node);
        return node;
    }

    private DocNode? ResolveQualified(Ref r, ResolveOptions o, int line)
    {
        var ownerText = r.OwnerText!;
        if (r.OwnerIsType)
        {
            var owner = ResolveType(ownerText, o.Create, line);
            if (owner == null) return null;
            return GetOrCreateMethod(owner, null, r, o.Create, line);
        }
        // request.ToCommand(x): a call on a local variable. Keep it (no owner node); lower-case members are not nodes.
        if (char.IsUpper(r.Name[0]) && r.HasArgs)
            return GetOrCreateMethod(null, ownerText, r, o.Create, line);
        return null;
    }

    private DocNode? ResolveBareCall(Ref r, ResolveOptions o, int line)
    {
        var name = r.Name;
        if (char.IsUpper(name[0]))
        {
            if (name.EndsWith("Destination", StringComparison.Ordinal))
            {
                var d = GetOrCreate("destination:" + name.ToLowerInvariant(), name, NodeKind.Destination, o.Create);
                if (d != null && r.Args != null) Note(d, r.Text);
                return d;
            }
            if (o.Position == RefPosition.TreeResult && !name.EndsWith("Async", StringComparison.Ordinal))
            {
                var oc = GetOrCreate("outcome:" + name.ToLowerInvariant(), name, NodeKind.Outcome, o.Create);
                if (oc != null && r.Args != null) Note(oc, r.Text);
                return oc;
            }
        }
        return ResolveFunction(r, o, line);
    }

    private DocNode? ResolveWord(Ref r, ResolveOptions o, int line)
    {
        var name = r.Name;
        if (char.IsUpper(name[0]))
        {
            var stripped = Identifiers.StripInterfacePrefix(name);
            var lower = stripped.ToLowerInvariant();
            // Already known under some prefix? Reuse it.
            var known = Graph.Find("type:" + lower) ?? Graph.Find("destination:" + lower) ?? Graph.Find("outcome:" + lower) ?? Graph.Find("setting:" + lower);
            if (known != null)
            {
                if (!string.Equals(known.Title, name, StringComparison.Ordinal)) known.Aliases.Add(name);
                return known;
            }
            if (Identifiers.HasTypeSuffix(stripped)) return ResolveType(name, o.Create && o.CreateTypes, line);
            if (o.Position == RefPosition.TreeResult)
            {
                if (name.EndsWith("Async", StringComparison.Ordinal)) return ResolveFunction(r, o, line);
                return GetOrCreate("outcome:" + lower, name, NodeKind.Outcome, o.Create);
            }
            if (o.Position == RefPosition.TreeNode)
            {
                // A bare PascalCase step inside a method tree is a method of the tree's owner (SignInAsync, StartSessionAsync).
                if (o.OwnerContext != null || name.EndsWith("Async", StringComparison.Ordinal) || !o.CreateTypes)
                    return ResolveFunction(r, o, line);
                return ResolveFunction(r, o, line);
            }
            if (o.Position == RefPosition.TableCell && o.CreateTypes) return ResolveType(name, o.Create, line);
            // Prose: a known method by simple name?
            var byName = FindMethodsByName(name);
            if (byName.Count == 1) return byName[0];
            return null;
        }
        // lower-case word: a node only as a tree line ("child") or after "calls"; otherwise a known function by name.
        if (Identifiers.IsBranchWord(name)) return null;
        if (o.Position == RefPosition.TreeNode || o.CallHint)
            return ResolveFunction(r, o, line);
        var fn = FindMethodsByName(name);
        return fn.Count == 1 ? fn[0] : null;
    }

    /// <summary>name(...) / name: member of MemberOf, else an existing method with that simple name, else the owner context, else owner-less.</summary>
    private DocNode? ResolveFunction(Ref r, ResolveOptions o, int line)
    {
        var name = r.Name;
        if (o.MemberOf != null) return GetOrCreateMethod(o.MemberOf, null, r, o.Create, line);
        // Inside a tree with an owner context, bare steps (SignInAsync, StartSessionAsync) belong to that owner.
        if (o.OwnerContext != null) return GetOrCreateMethod(o.OwnerContext, null, r, o.Create, line);
        var existing = FindMethodsByName(name);
        if (existing.Count == 1) { Alias(existing[0], r.Text); return existing[0]; }
        if (!o.Create) return null;
        var node = GetOrCreate("method:" + name.ToLowerInvariant(), r.Shape == RefShape.BareCall ? name + "()" : name, NodeKind.Method, true)!;
        if (r.Args != null) node.Subtitle ??= r.Text;
        return node;
    }

    public List<DocNode> FindMethodsByName(string name)
    {
        var suffix = "." + name.ToLowerInvariant();
        var bare = "method:" + name.ToLowerInvariant();
        return Graph.Nodes.Where(n => n.Kind == NodeKind.Method &&
                (n.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || n.Id.Equals(bare, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static string OwnerKey(DocNode owner) => owner.Id.StartsWith("type:", StringComparison.Ordinal) ? owner.Id[5..] : owner.Id;

    /// <summary>Gets or creates a method node. When an owner-less node with the same simple name exists it is re-homed (merged).</summary>
    public DocNode? GetOrCreateMethod(DocNode? owner, string? variableOwner, Ref r, bool create, int line)
    {
        var name = r.Name;
        var lower = name.ToLowerInvariant();
        string id = owner != null ? $"method:{OwnerKey(owner)}.{lower}"
            : variableOwner != null ? $"method:{variableOwner.ToLowerInvariant()}.{lower}"
            : $"method:{lower}";
        var node = Graph.Find(id);
        if (node == null)
        {
            if (!create) return null;
            node = Graph.GetOrAdd(id, MethodTitle(owner, variableOwner, name), NodeKind.Method);
            node.Section ??= CurrentSection;
            if (owner != null)
            {
                node.Owner = owner;
                node.Path = owner.Path;
                var orphan = Graph.Find("method:" + lower);
                if (orphan != null && orphan != node && orphan.Owner == null) Merge(orphan, node);
            }
        }
        if (node.Kind == NodeKind.Concept) node.Kind = NodeKind.Method;
        if (owner != null)
        {
            node.Owner ??= owner;
            Link(owner, node, EdgeKind.Contains, null, line);
        }
        if (r.Args != null && r.Args != "()") node.Subtitle ??= r.Text;
        Alias(node, r.Text);
        return node;
    }

    private static string MethodTitle(DocNode? owner, string? variableOwner, string name)
    {
        var parens = char.IsLower(name[0]) ? "()" : "";
        if (owner != null)
        {
            if (owner.Kind == NodeKind.TypeScript) return name + "()";
            var ownerName = owner.Title;
            var dot = ownerName.LastIndexOf('.');
            if (dot > 0 && owner.Kind is NodeKind.VueComponent or NodeKind.Html) ownerName = ownerName[..dot];
            return $"{ownerName}.{name}{parens}";
        }
        if (variableOwner != null) return $"{variableOwner}.{name}()";
        return name + parens;
    }

    private static void Alias(DocNode node, string text)
    {
        if (!string.Equals(node.Title, text, StringComparison.Ordinal)) node.Aliases.Add(text);
    }

    public DocNode? GetOrCreate(string id, string title, NodeKind kind, bool create)
    {
        var existing = Graph.Find(id);
        if (existing != null) return existing;
        if (!create) return null;
        var node = Graph.GetOrAdd(id, title, kind);
        node.Section ??= CurrentSection;
        return node;
    }

    /// <summary>Moves everything from <paramref name="from"/> into <paramref name="into"/> and removes the former.</summary>
    public void Merge(DocNode from, DocNode into)
    {
        if (ReferenceEquals(from, into)) return;
        foreach (var e in from.Outgoing.ToList()) Graph.Link(into, e.To, e.Kind, e.Label, e.Source);
        foreach (var e in from.Incoming.ToList()) Graph.Link(e.From, into, e.Kind, e.Label, e.Source);
        into.Aliases.Add(from.Title);
        foreach (var a in from.Aliases) into.Aliases.Add(a);
        foreach (var n in from.Notes) Note(into, n);
        if (into.Description == null) into.Description = from.Description; else Note(into, from.Description);
        foreach (var s in from.Sources) if (into.Sources.All(x => x.Line != s.Line)) into.Sources.Add(s);
        into.Subtitle ??= from.Subtitle;
        into.Section ??= from.Section;
        ReplaceInList(_flowItems, from, into);
        foreach (var list in _flowItemsBySection.Values) ReplaceInList(list, from, into);
        ReplaceInList(_pieceComponents, from, into);
        ReplaceInList(_implicitComponents, from, into);
        for (var i = 0; i < _pendingSectionLinks.Count; i++)
            if (_pendingSectionLinks[i].From == from) _pendingSectionLinks[i] = (into, _pendingSectionLinks[i].SectionRef, _pendingSectionLinks[i].Label, _pendingSectionLinks[i].Line);
        if (CurrentFlowItem == from) CurrentFlowItem = into;
        foreach (var n in Graph.Nodes) if (n.Owner == from) n.Owner = into;
        Graph.Remove(from);
    }

    private static void ReplaceInList(List<DocNode> list, DocNode from, DocNode into)
    {
        var i = list.IndexOf(from);
        if (i < 0) return;
        if (list.Contains(into)) list.RemoveAt(i); else list[i] = into;
    }

    // ------------------------------------------------------------------ finishing

    public void Finish()
    {
        ResolvePendingSectionLinks();

        foreach (var n in Graph.Nodes)
        {
            if (n.Kind == NodeKind.Method && n.Owner != null)
            {
                n.Path ??= n.Owner.Path;
                n.Subtitle ??= n.Path ?? n.Aliases.FirstOrDefault(a => a.Contains('('));
            }
        }

        Graph.Components.Clear();
        foreach (var c in _pieceComponents) if (!Graph.Components.Contains(c)) Graph.Components.Add(c);
        foreach (var c in _implicitComponents) if (!Graph.Components.Contains(c) && IsComponentKind(c.Kind)) Graph.Components.Add(c);

        Graph.EntryPoints.Clear();
        foreach (var f in _flowItems)
        {
            if (Graph.Find(f.Id) == null) continue;
            if (HasStrongIncoming(f)) continue;
            Graph.EntryPoints.Add(f);
            if (f.Section != null) f.Subtitle = f.Section.Title;
        }
    }

    private void ResolvePendingSectionLinks()
    {
        foreach (var (from, sectionRef, label, line) in _pendingSectionLinks)
        {
            var section = Graph.Sections.FirstOrDefault(s =>
                s.Title.StartsWith(sectionRef + ".", StringComparison.OrdinalIgnoreCase) ||
                s.Title.StartsWith(sectionRef + " ", StringComparison.OrdinalIgnoreCase));
            if (section == null) continue;
            var target = FlowItemsOf(section).FirstOrDefault();
            if (target == null || target == from) continue;
            if (from.Outgoing.Any(e => e.To == target && IsStrong(e.Kind))) continue;
            var kind = target.Kind switch
            {
                NodeKind.Route => EdgeKind.Redirects,
                NodeKind.Method or NodeKind.Endpoint => EdgeKind.Calls,
                _ => EdgeKind.Uses,
            };
            var source = new SourceRef(section.Title, line, LineText(line));
            Graph.Link(from, target, kind, label ?? $"section {sectionRef}", source);
        }
    }
}
