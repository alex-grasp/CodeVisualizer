using System.Text.RegularExpressions;
using DocsViewer.Core.Model;

namespace DocsViewer.Core.Parsing;

/// <summary>Syntactic shape of a back-ticked / tree-line reference, before it is resolved against the graph.</summary>
internal enum RefShape
{
    /// <summary>"Browser GET /Login (or /)".</summary>
    Browser,
    /// <summary>"GET /api/sign-in", optionally followed by a parenthetical.</summary>
    Endpoint,
    /// <summary>"/Login", "/auth/start?tenant=...".</summary>
    Route,
    /// <summary>"Controllers/LoginController.cs", "main.ts", "LoginPage.vue".</summary>
    FilePath,
    /// <summary>"Type.Method(args)", "IType.Method", "request.ToCommand(x)".</summary>
    QualifiedMethod,
    /// <summary>"name(args)" or "Name(args)".</summary>
    BareCall,
    /// <summary>A single identifier word: "SignInAsync", "InvalidCredentials", "child".</summary>
    Word,
    /// <summary>ALL_CAPS_WITH_UNDERSCORES.</summary>
    Setting,
}

/// <summary>A parsed reference. Pure syntax; <see cref="GraphBuilder"/> decides kind, id and node.</summary>
internal sealed class Ref
{
    public RefShape Shape { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Owner as written: "ISignInUseCase", "request", or null.</summary>
    public string? OwnerText { get; init; }
    /// <summary>Simple name: method/function/type/file name without extension.</summary>
    public string Name { get; init; } = "";
    /// <summary>Argument list including parentheses, or null.</summary>
    public string? Args { get; init; }
    public string? Verb { get; init; }
    /// <summary>Route path without query, or file path as written.</summary>
    public string? Path { get; init; }
    public string? Query { get; init; }
    /// <summary>Trailing parenthetical such as "(or /)" or "(page state)".</summary>
    public string? Trailing { get; init; }
    public string? Extension { get; init; }

    public bool OwnerIsType => OwnerText != null && Identifiers.IsTypeLike(OwnerText);
    public bool HasArgs => Args != null;
}

internal static class Identifiers
{
    private static readonly Regex BrowserRx = new(@"^Browser\s+(GET|POST|PUT|PATCH|DELETE)\s+(/[^\s?]*)(\?\S*)?(\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndpointRx = new(@"^(GET|POST|PUT|PATCH|DELETE)\s+(/[^\s?]*)(\?\S*)?(\s*\(.*\))?$", RegexOptions.Compiled);
    private static readonly Regex RouteRx = new(@"^(/[^\s?]*)(\?.*)?$", RegexOptions.Compiled);
    private static readonly Regex FileRx = new(@"^(?:\.\.\./)?([\w./\\-]*?)([\w-]+)\.(cs|ts|js|vue|html|cshtml)$", RegexOptions.Compiled);
    private static readonly Regex QualifiedRx = new(@"^([A-Za-z_]\w*)\.([A-Za-z_]\w*)(\(.*\))?$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CallRx = new(@"^([A-Za-z_]\w*)(\(.*\))$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WordRx = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);
    private static readonly Regex SettingRx = new(@"^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$", RegexOptions.Compiled);

    private static readonly string[] GenericFileNames = { "main", "index", "api", "shared", "utils", "types" };

    /// <summary>Words that look like identifiers but are branch conditions inside a call tree.</summary>
    private static readonly HashSet<string> BranchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "otherwise", "else", "failure", "success", "fails", "yes", "no", "default", "and", "or", "then", "if",
    };

    public static bool IsBranchWord(string word) => BranchWords.Contains(word);

    public static bool IsGenericFileName(string name) => GenericFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the syntactic shape of a reference. Returns null when the text is not identifier-like.</summary>
    public static Ref? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().TrimEnd(':', ',', ';');
        if (text.Length == 0) return null;
        // Markdown emphasis / html ids / attributes are never identifiers.
        if (text[0] is '#' or '[' or '<' or '*' or '_' or '{' or '"' or '\'' or '?') return null;

        var m = BrowserRx.Match(text);
        if (m.Success)
            return new Ref
            {
                Shape = RefShape.Browser, Text = text, Verb = m.Groups[1].Value.ToUpperInvariant(),
                Path = m.Groups[2].Value, Query = NullIfEmpty(m.Groups[3].Value), Trailing = NullIfEmpty(m.Groups[4].Value.Trim()),
                Name = m.Groups[2].Value,
            };

        m = EndpointRx.Match(text);
        if (m.Success)
            return new Ref
            {
                Shape = RefShape.Endpoint, Text = text, Verb = m.Groups[1].Value, Path = m.Groups[2].Value,
                Query = NullIfEmpty(m.Groups[3].Value), Trailing = NullIfEmpty(m.Groups[4].Value.Trim()), Name = m.Groups[2].Value,
            };

        m = RouteRx.Match(text);
        if (m.Success && text.Length > 0)
            return new Ref { Shape = RefShape.Route, Text = text, Path = m.Groups[1].Value, Query = NullIfEmpty(m.Groups[2].Value), Name = m.Groups[1].Value };

        m = FileRx.Match(text);
        if (m.Success)
            return new Ref
            {
                Shape = RefShape.FilePath, Text = text, Path = text, Name = m.Groups[2].Value, Extension = m.Groups[3].Value.ToLowerInvariant(),
            };

        if (SettingRx.IsMatch(text))
            return new Ref { Shape = RefShape.Setting, Text = text, Name = text };

        m = QualifiedRx.Match(text);
        if (m.Success)
            return new Ref
            {
                Shape = RefShape.QualifiedMethod, Text = text, OwnerText = m.Groups[1].Value, Name = m.Groups[2].Value,
                Args = NullIfEmpty(m.Groups[3].Value),
            };

        m = CallRx.Match(text);
        if (m.Success)
            return new Ref { Shape = RefShape.BareCall, Text = text, Name = m.Groups[1].Value, Args = m.Groups[2].Value };

        if (WordRx.IsMatch(text))
            return new Ref { Shape = RefShape.Word, Text = text, Name = text };

        return null;
    }

    /// <summary>True for PascalCase names (optionally I-prefixed interfaces).</summary>
    public static bool IsTypeLike(string name)
        => name.Length > 0 && char.IsUpper(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_') && !SettingRx.IsMatch(name);

    public static bool IsPascalCase(string name) => IsTypeLike(name);

    /// <summary>ISignInUseCase → SignInUseCase. Leaves other names alone.</summary>
    public static string StripInterfacePrefix(string name)
        => name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]) ? name[1..] : name;

    /// <summary>Kind of a type from its (stripped) simple name.</summary>
    public static NodeKind TypeKindFromName(string name, string? extension = null)
    {
        if (extension != null)
        {
            switch (extension.ToLowerInvariant())
            {
                case "vue": return NodeKind.VueComponent;
                case "ts":
                case "js": return NodeKind.TypeScript;
                case "html":
                case "cshtml": return NodeKind.Html;
            }
        }
        var n = StripInterfacePrefix(name);
        if (Ends(n, "Controller")) return NodeKind.Controller;
        if (Ends(n, "Middleware")) return NodeKind.Middleware;
        if (Ends(n, "UseCase")) return NodeKind.UseCase;
        if (Ends(n, "Service")) return NodeKind.Service;
        if (Ends(n, "Repository")) return NodeKind.Repository;
        if (Ends(n, "Client")) return NodeKind.Client;
        if (Ends(n, "Store")) return NodeKind.Store;
        if (Ends(n, "Mapper") || Ends(n, "Resolver") || Ends(n, "Extensions") || Ends(n, "Contracts") || Ends(n, "Provider")) return NodeKind.Mapper;
        if (Ends(n, "Destination")) return NodeKind.Destination;
        if (SettingRx.IsMatch(n) || n.Contains("Setting", StringComparison.Ordinal)) return NodeKind.Setting;
        return NodeKind.Concept;
    }

    /// <summary>True when a name carries one of the well-known type suffixes (so a bare word is a type, not a method).</summary>
    public static bool HasTypeSuffix(string name)
        => TypeKindFromName(name) is not (NodeKind.Concept or NodeKind.Setting);

    private static bool Ends(string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length;

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Canonical key of a type/file from its path or simple name.</summary>
    public static string TypeKey(string simpleName, string? path, string? extension)
    {
        var name = StripInterfacePrefix(simpleName);
        if (extension is "ts" or "js" or "html" or "cshtml" or "vue" && IsGenericFileName(name) && path != null)
        {
            var parent = ParentFolder(path);
            if (parent != null) return $"{parent.ToLowerInvariant()}/{name.ToLowerInvariant()}";
        }
        return name.ToLowerInvariant();
    }

    public static string? ParentFolder(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        var slash = p.LastIndexOf('/');
        if (slash <= 0) return null;
        var head = p[..slash];
        var slash2 = head.LastIndexOf('/');
        var folder = slash2 >= 0 ? head[(slash2 + 1)..] : head;
        return string.IsNullOrEmpty(folder) || folder == "..." ? null : folder;
    }

    /// <summary>Strips a trailing "(section 6a)" style label. Returns the label ("6a") or null.</summary>
    public static string StripSectionLabel(string text, out string? sectionRef)
    {
        var m = Regex.Match(text, @"\(\s*section\s+([\w.]+)\s*\)\s*$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            sectionRef = m.Groups[1].Value;
            return text[..m.Index].TrimEnd();
        }
        sectionRef = null;
        return text;
    }

    /// <summary>Splits "A → B → C" (also "->") into trimmed segments.</summary>
    public static List<string> SplitArrows(string text)
        => Regex.Split(text, @"\s*(?:→|->)\s*").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    public static bool ContainsArrow(string text) => text.Contains('→') || text.Contains("->", StringComparison.Ordinal);

    /// <summary>Finds a "302 to /path", "302 /path" or "goes to /path" redirect target inside free text.</summary>
    public static string? FindRedirectRoute(string text)
    {
        var m = Regex.Match(text, @"(?:\b30[1278]\b\s*(?:to\s+)?|goes to\s+|redirects? to\s+)(/[^\s,;)]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Finds "Type.Method(...)" / "IType.Method" tokens embedded in free text.</summary>
    public static IEnumerable<string> FindEmbeddedCalls(string text)
    {
        foreach (Match m in Regex.Matches(text, @"(?<![\w.])(I?[A-Z]\w*)\.([A-Z]\w*)(\([^()]*(?:\([^()]*\)[^()]*)*\))?(?![\w.\[])"))
            yield return m.Value;
    }
}
