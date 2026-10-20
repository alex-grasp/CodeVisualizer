namespace DocsViewer.Core.Model;

/// <summary>What a card represents. Inferred by the parser from naming conventions and context.</summary>
public enum NodeKind
{
    Document,
    Section,
    /// <summary>"Browser GET /Login" style entry.</summary>
    Browser,
    /// <summary>A URL path such as "/Login" or "/auth/start".</summary>
    Route,
    /// <summary>"GET /api/sign-in", "POST /api/sign-in".</summary>
    Endpoint,
    VueComponent,
    TypeScript,
    Html,
    Controller,
    Middleware,
    UseCase,
    Service,
    Repository,
    Client,
    Store,
    /// <summary>Mappers, resolvers, extension classes, contracts.</summary>
    Mapper,
    Interface,
    /// <summary>A C# or TS method/function, e.g. "SignInController.SignInAsync" or "loadLoginPage()".</summary>
    Method,
    /// <summary>*Destination types (where the browser goes next).</summary>
    Destination,
    /// <summary>An outcome / error code such as InvalidCredentials, or a status.</summary>
    Outcome,
    /// <summary>A branch or condition line inside a call tree, e.g. "no password set".</summary>
    Branch,
    Setting,
    File,
    /// <summary>Anything else that is identifier-like but could not be classified.</summary>
    Concept,
}
