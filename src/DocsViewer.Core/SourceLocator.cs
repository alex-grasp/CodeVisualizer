using System.Collections.Concurrent;

namespace DocsViewer.Core;

/// <summary>
/// Maps the repo-relative paths written in the docs ("Controllers/Api/SignInController.cs",
/// ".../SignIn/Services/StaffSignInService.cs", "ClientApp/src/public/login/main.ts") to real files under a source
/// root. The root is normally the repository that holds the docs folder (see <see cref="FindRepoRoot"/>). Paths in the
/// docs are often relative to a project folder rather than the repo root, so a file-name index is used as a fallback and
/// the candidate whose tail matches the most path segments wins.
/// </summary>
public sealed class SourceLocator
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "dist", "packages", "TestResults", ".angular", ".nuget",
    };

    private const int MaxIndexedFiles = 400_000;

    private readonly Lazy<Task<ILookup<string, string>>> _index;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SourceLocator(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        _index = new Lazy<Task<ILookup<string, string>>>(() => Task.Run(BuildIndex));
    }

    public string RootPath { get; }

    /// <summary>True once the file-name index has been built (the first lookup that needs it builds it).</summary>
    public bool IsIndexReady => _index.IsValueCreated && _index.Value.IsCompleted;

    /// <summary>Starts building the index in the background so the first lookup is fast.</summary>
    public void WarmUp() => _ = _index.Value;

    /// <summary>Walks up from <paramref name="startDirectory"/> to the first folder that holds a .git entry or a
    /// solution file. Null when there is none.</summary>
    public static string? FindRepoRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
            if (!dir.Exists) dir = dir.Parent;
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // Unreadable folders simply give no root.
        }
        return null;
    }

    /// <summary>Resolves a doc path to an existing file under the root, or null. Never throws.</summary>
    public async Task<string?> ResolveAsync(string? docPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(docPath)) return null;
        if (_cache.TryGetValue(docPath, out var cached)) return cached;

        string? result = null;
        try
        {
            var normalized = Normalize(docPath);
            if (normalized.Length == 0) return null;

            var direct = Path.Combine(RootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(direct))
            {
                result = Path.GetFullPath(direct);
            }
            else
            {
                var index = await _index.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                result = BestMatch(normalized, index);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            result = null;
        }
        _cache[docPath] = result;
        return result;
    }

    /// <summary>Synchronous variant for callers that already know the index is ready (tests, --dump).</summary>
    public string? Resolve(string? docPath) => ResolveAsync(docPath).GetAwaiter().GetResult();

    /// <summary>"`.../SignIn/Services/X.cs`" → "SignIn/Services/X.cs"; back-slashes → "/"; no leading "./" or "/".</summary>
    public static string Normalize(string docPath)
    {
        var p = docPath.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        while (p.StartsWith("...", StringComparison.Ordinal) || p.StartsWith("…", StringComparison.Ordinal))
            p = p.StartsWith("...", StringComparison.Ordinal) ? p[3..] : p[1..];
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.TrimStart('/');
        // "Controllers/LoginController.cs + Infrastructure/Vite/VitePageResolver.cs": only the first path.
        var plus = p.IndexOf(" + ", StringComparison.Ordinal);
        if (plus > 0) p = p[..plus];
        var arrow = p.IndexOf(" → ", StringComparison.Ordinal);
        if (arrow > 0) p = p[..arrow];
        return p.Trim();
    }

    private static string? BestMatch(string normalized, ILookup<string, string> index)
    {
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var fileName = segments[^1];
        var candidates = index[fileName].ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Score: number of trailing doc segments that match the candidate's trailing folders; ties → shortest path.
        return candidates
            .Select(c => (path: c, score: TailMatch(segments, c)))
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.path.Length)
            .ThenBy(t => t.path, StringComparer.OrdinalIgnoreCase)
            .First().path;
    }

    private static int TailMatch(string[] docSegments, string candidate)
    {
        var parts = candidate.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var score = 0;
        var i = docSegments.Length - 1;
        var j = parts.Length - 1;
        while (i >= 0 && j >= 0 && string.Equals(docSegments[i], parts[j], StringComparison.OrdinalIgnoreCase))
        {
            score++;
            i--;
            j--;
        }
        return score;
    }

    private ILookup<string, string> BuildIndex()
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(RootPath);
        while (pending.Count > 0 && files.Count < MaxIndexedFiles)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch
            {
                continue; // access denied, reparse points, etc.
            }
            foreach (var entry in entries)
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        var name = Path.GetFileName(entry);
                        if (SkippedDirectories.Contains(name)) continue;
                        var info = new DirectoryInfo(entry);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        pending.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                        if (files.Count >= MaxIndexedFiles) break;
                    }
                }
                catch
                {
                    // skip unreadable entries
                }
            }
        }
        return files.ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
    }
}
