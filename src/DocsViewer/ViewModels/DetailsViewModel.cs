using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DocsViewer.Core;
using DocsViewer.Core.Model;
using DocsViewer.Infrastructure;

namespace DocsViewer.ViewModels;

/// <summary>What the details pane needs from the main view model: navigation, the source locator and document lookup.</summary>
public sealed record DetailsServices(
    Action<DocNode> Navigate,
    Action<DocNode> ShowInComponents,
    Action<string, string?> OpenDocument,
    Action ChooseSourceRoot,
    Func<string, bool> DocumentExists,
    SourceLocator? Locator);

/// <summary>The right-hand details pane for one node.</summary>
public sealed class DetailsViewModel : ObservableObject
{
    private static readonly Regex DocLinkRx = new(
        @"(?<![\w/.-])(?<doc>[A-Za-z0-9_][\w.-]*\.md)(?:\s*(?:§|section)\s*(?<section>\d+[a-z]?|[A-Za-z][\w-]*))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly DocGraph _graph;
    private readonly DetailsServices _services;
    private SourceRefViewModel? _selectedSource;
    private string? _resolvedPath;
    private string? _sourceStatus;
    private bool _isResolvingSource;

    /// <param name="via">The edge the user drilled through to reach the node, if any; its source line picks the
    /// pre-selected "Appears in" entry so the excerpt shows the part of the markdown the user is looking at.</param>
    public DetailsViewModel(DocGraph graph, DocNode node, DetailsServices services, DocEdge? via = null)
    {
        _graph = graph;
        Node = node;
        _services = services;

        var navigateIncoming = new RelayCommand(p => { if (p is DocNode n) _services.Navigate(n); });
        CalledBy = node.Incoming
            .Where(e => e.Kind != EdgeKind.Mentions)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.From.Title, StringComparer.OrdinalIgnoreCase)
            .Select(e => new IncomingEdgeViewModel(e, navigateIncoming))
            .ToList();
        MentionedBy = node.Incoming
            .Where(e => e.Kind == EdgeKind.Mentions)
            .Select(e => new IncomingEdgeViewModel(e, navigateIncoming))
            .ToList();
        Sources = node.Sources.OrderBy(s => s.Line).Select(s => new SourceRefViewModel(s)).ToList();
        Aliases = node.Aliases
            .Where(a => !string.Equals(a, node.Title, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DocLinks = FindDocLinks(node);

        CopyPathCommand = new RelayCommand(CopyPath, () => HasPath);
        NavigateOwnerCommand = new RelayCommand(() => { if (node.Owner != null) _services.Navigate(node.Owner); }, () => HasOwner);
        NavigateSectionCommand = new RelayCommand(() => { if (node.Section != null) _services.Navigate(node.Section); }, () => node.Section != null);
        ShowInComponentsCommand = new RelayCommand(() => _services.ShowInComponents(node));
        OpenFileCommand = new RelayCommand(OpenFile, () => HasResolvedPath);
        RevealFileCommand = new RelayCommand(RevealFile, () => HasResolvedPath);
        ChooseSourceRootCommand = new RelayCommand(() => _services.ChooseSourceRoot());

        if (Sources.Count > 0) SelectedSource = PickInitialSource(Sources, via?.Source);
        if (HasPath) _ = ResolveSourceAsync();
    }

    /// <summary>The source closest to where the user came from: same section and nearest line, else the nearest line
    /// within a few lines (edge and node lines may differ by one), else the first source.</summary>
    private static SourceRefViewModel PickInitialSource(IReadOnlyList<SourceRefViewModel> sources, SourceRef? via)
    {
        if (via == null) return sources[0];
        var sameSection = sources
            .Where(s => string.Equals(s.Source.SectionTitle, via.SectionTitle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => Math.Abs(s.Line - via.Line))
            .FirstOrDefault();
        if (sameSection != null) return sameSection;
        var nearest = sources.OrderBy(s => Math.Abs(s.Line - via.Line)).First();
        return Math.Abs(nearest.Line - via.Line) <= 3 ? nearest : sources[0];
    }

    public DocNode Node { get; }

    public string Title => Node.Title;
    public string? Subtitle => Node.Subtitle;
    public NodeKind Kind => Node.Kind;
    public string KindLabel => KindPalette.Label(Node.Kind);
    public string? SectionTitle => Node.Section?.Title;
    public bool HasSection => Node.Section != null;

    public string? OwnerTitle => Node.Owner?.Title;
    public bool HasOwner => Node.Owner != null;

    public string? Path => Node.Path ?? Node.Owner?.Path;
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);
    public bool PathIsInherited => Node.Path == null && Node.Owner?.Path != null;

    // ----- source file ------------------------------------------------------------------------------------------

    /// <summary>The real file the path resolved to under the source root, or null.</summary>
    public string? ResolvedPath
    {
        get => _resolvedPath;
        private set
        {
            if (!SetProperty(ref _resolvedPath, value)) return;
            OnPropertyChanged(nameof(HasResolvedPath));
            OnPropertyChanged(nameof(ShowChooseSourceRoot));
            (OpenFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RevealFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasResolvedPath => ResolvedPath != null;

    /// <summary>"Looking for the file…", "Not found under C:\repo", or "No source root set".</summary>
    public string? SourceStatus
    {
        get => _sourceStatus;
        private set
        {
            if (!SetProperty(ref _sourceStatus, value)) return;
            OnPropertyChanged(nameof(ShowChooseSourceRoot));
        }
    }

    public bool IsResolvingSource
    {
        get => _isResolvingSource;
        private set => SetProperty(ref _isResolvingSource, value);
    }

    /// <summary>Offer the root picker when the file was not found (or no root is set), not while still looking.</summary>
    public bool ShowChooseSourceRoot => HasPath && !HasResolvedPath && !IsResolvingSource;

    private async Task ResolveSourceAsync()
    {
        var locator = _services.Locator;
        if (locator == null)
        {
            SourceStatus = "No source root set";
            return;
        }
        IsResolvingSource = true;
        SourceStatus = locator.IsIndexReady ? null : "Looking for the file…";
        try
        {
            var resolved = await locator.ResolveAsync(Path);
            ResolvedPath = resolved;
            SourceStatus = resolved == null ? $"Not found under {locator.RootPath}" : null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Resolving {Path}");
            SourceStatus = "Could not search the source root";
        }
        finally
        {
            IsResolvingSource = false;
            OnPropertyChanged(nameof(ShowChooseSourceRoot));
        }
    }

    private void OpenFile()
    {
        if (ResolvedPath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(ResolvedPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Opening {ResolvedPath}");
            SourceStatus = $"Could not open: {ex.Message}";
        }
    }

    private void RevealFile()
    {
        if (ResolvedPath == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ResolvedPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Revealing {ResolvedPath}");
        }
    }

    // ----- links to other documents -----------------------------------------------------------------------------

    /// <summary>"see AIPanel-Plumbing.md §3" style references in the node's description, notes and source lines.</summary>
    public IReadOnlyList<DocLinkViewModel> DocLinks { get; }
    public bool HasDocLinks => DocLinks.Count > 0;

    private IReadOnlyList<DocLinkViewModel> FindDocLinks(DocNode node)
    {
        var texts = new List<string>();
        if (node.Description != null) texts.Add(node.Description);
        texts.AddRange(node.Notes);
        texts.AddRange(node.Sources.Select(s => s.Text));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<DocLinkViewModel>();
        foreach (var text in texts)
        {
            foreach (Match m in DocLinkRx.Matches(text))
            {
                var doc = m.Groups["doc"].Value;
                if (string.Equals(doc, _graph.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                var section = m.Groups["section"].Success ? m.Groups["section"].Value : null;
                if (!seen.Add($"{doc}|{section}")) continue;
                var exists = _services.DocumentExists(doc);
                var command = new RelayCommand(() => _services.OpenDocument(doc, section), () => exists);
                links.Add(new DocLinkViewModel(doc, section, exists, command));
            }
        }
        return links;
    }

    // ----- the rest ---------------------------------------------------------------------------------------------

    public string? Description => Node.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Node.Description);

    public IReadOnlyList<string> Notes => Node.Notes;
    public bool HasNotes => Node.Notes.Count > 0;

    public IReadOnlyList<string> Aliases { get; }
    public bool HasAliases => Aliases.Count > 0;
    public string AliasesText => string.Join("  ·  ", Aliases);

    public IReadOnlyList<IncomingEdgeViewModel> CalledBy { get; }
    public bool HasCalledBy => CalledBy.Count > 0;

    public IReadOnlyList<IncomingEdgeViewModel> MentionedBy { get; }
    public bool HasMentionedBy => MentionedBy.Count > 0;

    public IReadOnlyList<SourceRefViewModel> Sources { get; }
    public bool HasSources => Sources.Count > 0;

    public bool IsMethod => Node.Kind == NodeKind.Method;

    public ObservableCollection<ExcerptLineViewModel> Excerpt { get; } = new();
    public bool HasExcerpt => Excerpt.Count > 0;

    public SourceRefViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            BuildExcerpt(value);
        }
    }

    public ICommand CopyPathCommand { get; }
    public ICommand NavigateOwnerCommand { get; }
    public ICommand NavigateSectionCommand { get; }
    public ICommand ShowInComponentsCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand ChooseSourceRootCommand { get; }

    private void BuildExcerpt(SourceRefViewModel? source)
    {
        Excerpt.Clear();
        if (source != null)
        {
            const int context = 4;
            var lines = _graph.Lines;
            var index = source.Line - 1; // SourceRef.Line is 1-based
            if (index >= 0 && index < lines.Count)
            {
                var start = Math.Max(0, index - context);
                var end = Math.Min(lines.Count - 1, index + context);
                for (var i = start; i <= end; i++)
                    Excerpt.Add(new ExcerptLineViewModel(i + 1, lines[i].Replace("\t", "    "), i == index));
            }
            else if (!string.IsNullOrWhiteSpace(source.Text))
            {
                Excerpt.Add(new ExcerptLineViewModel(source.Line, source.Text, true));
            }
        }
        OnPropertyChanged(nameof(HasExcerpt));
    }

    private void CopyPath()
    {
        if (!HasPath) return;
        try
        {
            Clipboard.SetText(Path!);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Copy path to clipboard");
        }
    }
}
