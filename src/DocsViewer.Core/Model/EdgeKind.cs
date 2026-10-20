namespace DocsViewer.Core.Model;

/// <summary>How two nodes relate. Order of the members is the display priority (lower = shown first).</summary>
public enum EdgeKind
{
    /// <summary>Parent calls / invokes child (a child line in a call tree, or "X calls Y").</summary>
    Calls,
    /// <summary>Sequential step: after the parent, the child happens (e.g. "Then the browser runs main.ts").</summary>
    Then,
    /// <summary>Parent is served/handled by or depends on child (endpoint → controller, controller → services).</summary>
    Uses,
    /// <summary>main.ts mounts LoginPage.vue.</summary>
    Mounts,
    /// <summary>Parent leads to a URL / destination / redirect.</summary>
    Redirects,
    /// <summary>Parent produces child as result or outcome (→ InvalidCredentials).</summary>
    Returns,
    /// <summary>Structural: section → items, file/type → members, doc → sections.</summary>
    Contains,
    /// <summary>Interface → implementation, or abstraction → concrete named after it.</summary>
    Implements,
    /// <summary>Weak link: child is mentioned in text attached to parent.</summary>
    Mentions,
}
