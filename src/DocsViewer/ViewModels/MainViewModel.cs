using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using DocsViewer.Core;
using DocsViewer.Core.Model;
using DocsViewer.Infrastructure;

namespace DocsViewer.ViewModels;

/// <summary>Root view model: documents, Miller columns, breadcrumb, details, search, startup and file watching.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DocLibrary _library;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _reloadTimer;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    private string? _folderPath;
    private bool _isSample;
    private DocumentItemViewModel? _selectedDocument;
    private ViewMode _mode = ViewMode.Flows;
    private DetailsViewModel? _details;
    private string _searchText = string.Empty;
    private bool _isSearchOpen;
    private SearchResultViewModel? _selectedSearchResult;
    private string? _statusText;
    private string _errorsText = string.Empty;
    private bool _suppressSelectionHandling;
    private string? _startupWarning;   // "Path not found: ..." from the command line; sticky until another folder is opened
    private string? _loadError;        // the exception message of the last LoadFolder, if it failed
    private bool _focusOnRebuild = true; // false while a background (watcher) reload rebuilds the columns
    private string? _sourceRoot;        // the repository the doc paths are relative to
    private bool _sourceRootIsExplicit; // chosen by the user (saved) rather than detected
    private SourceLocator? _locator;

    public MainViewModel(DocLibrary library, AppSettings settings, Dispatcher dispatcher)
    {
        _library = library;
        _settings = settings;
        _dispatcher = dispatcher;

        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); ReloadChangedFiles(); };

        OpenFolderCommand = new RelayCommand(OpenFolder);
        ReloadCommand = new RelayCommand(Reload, () => !_isSample && !string.IsNullOrEmpty(FolderPath));
        SetModeCommand = new RelayCommand(p => { if (p is ViewMode m) Mode = m; else if (p is string s && Enum.TryParse<ViewMode>(s, out var pm)) Mode = pm; });
        BreadcrumbCommand = new RelayCommand(p => { if (p is BreadcrumbItemViewModel b) TruncateTo(b.ColumnIndex); });
        ChooseSearchResultCommand = new RelayCommand(p => ChooseSearchResult(p as SearchResultViewModel ?? SelectedSearchResult));
        ClearSearchCommand = new RelayCommand(() => { SearchText = string.Empty; IsSearchOpen = false; });
        NavigateToNodeCommand = new RelayCommand(p => { if (p is DocNode n) NavigateTo(n); });
        ChooseSourceRootCommand = new RelayCommand(ChooseSourceRoot, () => HasFolder && !_isSample);
        OpenRelatedDocCommand = new RelayCommand(p => { if (p is RelatedDocViewModel r) SelectedDocument = r.Document; });
    }

    // ----- view hooks -------------------------------------------------------------------------------------------

    /// <summary>Set by the view: shows a folder picker (initial folder) and returns the chosen folder or null.</summary>
    public Func<string?, string?>? FolderPicker { get; set; }

    /// <summary>Raised after a new column was appended so the view can scroll it into view.</summary>
    public event Action? ColumnAppended;

    /// <summary>Raised when keyboard focus should move to the given column.</summary>
    public event Action<int>? FocusColumnRequested;

    // ----- commands ---------------------------------------------------------------------------------------------

    public ICommand OpenFolderCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand BreadcrumbCommand { get; }
    public ICommand ChooseSearchResultCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand NavigateToNodeCommand { get; }
    public ICommand ChooseSourceRootCommand { get; }
    public ICommand OpenRelatedDocCommand { get; }

    // ----- state ------------------------------------------------------------------------------------------------

    public ObservableCollection<DocumentItemViewModel> Documents { get; } = new();
    public ObservableCollection<ColumnViewModel> Columns { get; } = new();
    public ObservableCollection<BreadcrumbItemViewModel> Breadcrumb { get; } = new();
    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();

    /// <summary>Parent, children and referenced documents of the selected one, shown as chips under the breadcrumb.</summary>
    public ObservableCollection<RelatedDocViewModel> RelatedDocs { get; } = new();
    public bool HasRelatedDocs => RelatedDocs.Count > 0;

    public string? FolderPath
    {
        get => _folderPath;
        private set
        {
            if (!SetProperty(ref _folderPath, value)) return;
            OnPropertyChanged(nameof(FolderDisplay));
            OnPropertyChanged(nameof(HasFolder));
            (ReloadCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ChooseSourceRootCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    // ----- source root ------------------------------------------------------------------------------------------

    /// <summary>The folder the paths in the docs are resolved against (the repository that holds the docs).</summary>
    public string? SourceRoot => _sourceRoot;
    public bool HasSourceRoot => _sourceRoot != null;
    public string SourceRootDisplay => _sourceRoot == null ? "No source root" : Path.GetFileName(_sourceRoot.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : _sourceRoot;
    public string SourceRootToolTip => _sourceRoot == null
        ? "No repository was found above the docs folder. Choose the folder the paths in the docs are relative to."
        : $"Source root: {_sourceRoot}{(_sourceRootIsExplicit ? " (chosen)" : " (detected)")}\nPaths on the cards open from here.";

    private void ApplySourceRoot(string? root, bool explicitChoice)
    {
        _sourceRoot = root;
        _sourceRootIsExplicit = explicitChoice;
        _locator = root != null && Directory.Exists(root) ? new SourceLocator(root) : null;
        _locator?.WarmUp();
        OnPropertyChanged(nameof(SourceRoot));
        OnPropertyChanged(nameof(HasSourceRoot));
        OnPropertyChanged(nameof(SourceRootDisplay));
        OnPropertyChanged(nameof(SourceRootToolTip));
        // The details pane shows Open/Reveal for the selected card; rebuild it against the new root.
        if (Details != null) ShowDetails(Details.Node);
    }

    private void ChooseSourceRoot()
    {
        if (_isSample || string.IsNullOrEmpty(FolderPath)) return;
        var chosen = FolderPicker?.Invoke(_sourceRoot ?? FolderPath);
        if (string.IsNullOrWhiteSpace(chosen) || !Directory.Exists(chosen)) return;
        _settings.SetSourceRoot(FolderPath, chosen);
        _settings.Save();
        ApplySourceRoot(chosen, explicitChoice: true);
        StatusText = $"Source root: {chosen}";
    }

    public string FolderDisplay => _isSample ? "Sample data (--sample)" : (string.IsNullOrEmpty(FolderPath) ? "No folder open" : FolderPath!);
    public bool HasFolder => !string.IsNullOrEmpty(FolderPath) || _isSample;

    public bool HasDocuments => Documents.Count > 0;
    public bool ShowEmptyState => !HasDocuments;

    public string EmptyStateTitle => !HasFolder ? "No folder open" : "No markdown documents here";
    public string EmptyStateHint => !HasFolder
        ? "Open a folder that holds AI-generated plumbing docs (*.md). You can also pass a folder or file on the command line."
        : $"No *.md files were found under {FolderPath}. Choose another folder or add documents.";

    public DocumentItemViewModel? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (!SetProperty(ref _selectedDocument, value)) return;
            OnPropertyChanged(nameof(Graph));
            OnPropertyChanged(nameof(HasGraph));
            if (value != null && !_isSample)
            {
                _settings.LastDocument = value.FilePath;
                _settings.Save();
            }
            UpdateRelatedDocs();
            ResetColumns();
            SearchText = string.Empty;
        }
    }

    private void UpdateRelatedDocs()
    {
        RelatedDocs.Clear();
        var graph = Graph;
        if (graph != null && !_isSample)
        {
            var parent = _library.ParentOf(graph);
            var children = _library.ChildrenOf(graph);
            if (parent != null) Add("Part of", parent);
            foreach (var child in children) Add("Includes", child);
            foreach (var name in graph.ReferencedDocs)
            {
                var doc = _library.FindDocument(name);
                if (doc == null || ReferenceEquals(doc, parent) || children.Contains(doc)) continue;
                Add("See also", doc);
            }
        }
        OnPropertyChanged(nameof(HasRelatedDocs));

        void Add(string relation, DocGraph doc)
        {
            var item = Documents.FirstOrDefault(d => ReferenceEquals(d.Graph, doc))
                ?? Documents.FirstOrDefault(d => string.Equals(d.FilePath, doc.FilePath, StringComparison.OrdinalIgnoreCase));
            if (item != null && RelatedDocs.All(r => !ReferenceEquals(r.Document, item)))
                RelatedDocs.Add(new RelatedDocViewModel(relation, item, OpenRelatedDocCommand));
        }
    }

    /// <summary>Opens another document by file name ("Home-Plumbing.md"), at the section whose heading starts with
    /// <paramref name="section"/> ("3", "6a") when given. Used by "see Other-Doc.md §3" links.</summary>
    public void OpenDocument(string docName, string? section)
    {
        var graph = _library.FindDocument(docName);
        var item = graph == null ? null : Documents.FirstOrDefault(d => ReferenceEquals(d.Graph, graph));
        if (item == null)
        {
            StatusText = $"Document not found in this folder: {docName}";
            return;
        }
        if (!ReferenceEquals(item, SelectedDocument)) SelectedDocument = item;
        if (section == null || Graph == null) return;

        var target = Graph.Sections.FirstOrDefault(s => s.Title.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase)
                                                        || s.Title.StartsWith(section + " ", StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(s.Title, section, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            StatusText = $"Section {section} was not found in {docName}";
            return;
        }
        _mode = ViewMode.Sections;
        RaiseModeProperties();
        ApplyPath(new List<DocNode> { target });
    }

    private bool DocumentExists(string docName) => _library.FindDocument(docName) != null;

    public DocGraph? Graph => SelectedDocument?.Graph;
    public bool HasGraph => Graph != null;

    public ViewMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            RaiseModeProperties();
            ResetColumns();
        }
    }

    public bool IsFlowsMode { get => _mode == ViewMode.Flows; set { if (value) Mode = ViewMode.Flows; } }
    public bool IsComponentsMode { get => _mode == ViewMode.Components; set { if (value) Mode = ViewMode.Components; } }
    public bool IsSectionsMode { get => _mode == ViewMode.Sections; set { if (value) Mode = ViewMode.Sections; } }

    public DetailsViewModel? Details
    {
        get => _details;
        private set
        {
            if (!SetProperty(ref _details, value)) return;
            OnPropertyChanged(nameof(HasDetails));
        }
    }

    public bool HasDetails => Details != null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
            RunSearch();
        }
    }

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public bool HasSearchResults => SearchResults.Count > 0;

    public SearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => SetProperty(ref _selectedSearchResult, value);
    }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Files that could not be read or parsed.</summary>
    public string ErrorsText
    {
        get => _errorsText;
        private set
        {
            if (!SetProperty(ref _errorsText, value)) return;
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    public bool HasErrors => !string.IsNullOrEmpty(ErrorsText);

    // ----- startup / loading ------------------------------------------------------------------------------------

    /// <summary>Applies the startup rules: explicit path, last folder, a docs folder above the exe, else empty state.</summary>
    public void Startup(string? explicitPath)
    {
        string? folder = null;
        string? file = null;
        string? notFound = null;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = SafeFullPath(explicitPath);
            if (full != null && Directory.Exists(full)) folder = full;
            else if (full != null && File.Exists(full))
            {
                file = full;
                folder = Path.GetDirectoryName(full);
            }
            else notFound = explicitPath;
        }

        if (folder == null && !string.IsNullOrWhiteSpace(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
        {
            folder = _settings.LastFolder;
            file = _settings.LastDocument;
        }

        folder ??= FindDocsFolderAbove(AppContext.BaseDirectory);

        // The warning must survive the fallback load: it goes into the red error block and the status line.
        if (notFound != null)
        {
            _startupWarning = $"Path not found: {notFound}";
            StatusText = _startupWarning;
            UpdateErrors();
        }

        if (folder != null)
        {
            LoadFolder(folder, file);
            if (notFound != null && _loadError == null) StatusText = $"Path not found: {notFound} – showing {folder}";
        }
        else RaiseCollectionProperties();
    }

    /// <summary>Loads the hand-built sample graph (the --sample switch).</summary>
    public void LoadSample(DocGraph graph, string? initialNodeId = null)
    {
        _isSample = true;
        DisposeWatcher();
        FolderPath = null;
        ApplySourceRoot(null, explicitChoice: false);
        OnPropertyChanged(nameof(FolderDisplay));
        OnPropertyChanged(nameof(HasFolder));
        _library.Documents.Clear();
        _library.Documents.Add(graph);
        Documents.Clear();
        Documents.Add(new DocumentItemViewModel(graph));
        RaiseCollectionProperties();
        SelectedDocument = Documents[0];
        if (initialNodeId != null)
        {
            var node = graph.Find(initialNodeId);
            if (node != null) NavigateTo(node);
        }
    }

    public void LoadFolder(string folder, string? fileToSelect = null)
    {
        _isSample = false;
        _loadError = null;
        try
        {
            _library.LoadFolder(folder);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Loading folder {folder}");
            _loadError = $"Could not load {folder}: {ex.Message}";
        }

        FolderPath = folder;
        _settings.LastFolder = folder;
        _settings.Save();

        var savedRoot = _settings.SourceRootFor(folder);
        if (savedRoot != null && Directory.Exists(savedRoot)) ApplySourceRoot(savedRoot, explicitChoice: true);
        else ApplySourceRoot(SourceLocator.FindRepoRoot(folder), explicitChoice: false);

        Documents.Clear();
        foreach (var (g, depth) in _library.OrderedWithDepth())
        {
            var parentTitle = depth > 0 ? _library.ParentOf(g)?.Title : null;
            Documents.Add(new DocumentItemViewModel(g, depth, parentTitle));
        }
        UpdateErrors();
        RaiseCollectionProperties();

        var toSelect = fileToSelect == null
            ? null
            : Documents.FirstOrDefault(d => string.Equals(d.FilePath, fileToSelect, StringComparison.OrdinalIgnoreCase));
        SelectedDocument = toSelect ?? Documents.FirstOrDefault();
        if (SelectedDocument == null) ResetColumns();

        StartWatcher(folder);
        // A load failure must not be replaced by the plain document count (or null, which shows a bare empty state).
        StatusText = _loadError ?? (Documents.Count == 0 ? null : $"{Documents.Count} document{(Documents.Count == 1 ? "" : "s")} loaded");
    }

    private void OpenFolder()
    {
        var chosen = FolderPicker?.Invoke(FolderPath);
        if (string.IsNullOrWhiteSpace(chosen) || !Directory.Exists(chosen)) return;
        _startupWarning = null; // the user chose a folder explicitly; the command-line warning is no longer relevant
        LoadFolder(chosen);
    }

    private void Reload()
    {
        if (_isSample || string.IsNullOrEmpty(FolderPath)) return;
        var state = CaptureNavigationState();
        LoadFolder(FolderPath, SelectedDocument?.FilePath);
        RestoreNavigationState(state);
    }

    private void UpdateErrors()
    {
        var lines = new List<string>();
        if (_startupWarning != null) lines.Add(_startupWarning);
        if (_loadError != null) lines.Add(_loadError);
        lines.AddRange(_library.Errors.Select(kv => $"{Path.GetFileName(kv.Key)}: {kv.Value}"));
        ErrorsText = string.Join(Environment.NewLine, lines);
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim('"')); }
        catch { return null; }
    }

    /// <summary>Walks up from <paramref name="start"/> looking for a "docs" folder that contains markdown.</summary>
    public static string? FindDocsFolderAbove(string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "docs");
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.md", SearchOption.AllDirectories).Any())
                    return candidate;
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Looking for a docs folder");
        }
        return null;
    }

    // ----- file watching ----------------------------------------------------------------------------------------

    private void StartWatcher(string folder)
    {
        DisposeWatcher();
        try
        {
            _watcher = new FileSystemWatcher(folder, "*.md")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += (_, e) => ErrorLog.Write(e.GetException(), "FileSystemWatcher");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"Watching {folder}");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Watcher events arrive on a thread-pool thread; marshal to the UI thread and debounce.
        _dispatcher.BeginInvoke(() =>
        {
            _pendingChanges.Add(e.FullPath);
            if (e is RenamedEventArgs r) _pendingChanges.Add(r.OldFullPath);
            _reloadTimer.Stop();
            _reloadTimer.Start();
        });
    }

    private void ReloadChangedFiles()
    {
        if (_pendingChanges.Count == 0 || string.IsNullOrEmpty(FolderPath)) return;
        var changed = _pendingChanges.ToList();
        _pendingChanges.Clear();

        var state = CaptureNavigationState();
        var structural = changed.Any(p => !File.Exists(p)) ||
                         changed.Any(p => !_library.Documents.Any(d => string.Equals(d.FilePath, p, StringComparison.OrdinalIgnoreCase)));

        // A background reload must not steal the keyboard from the search box or the document list.
        _focusOnRebuild = false;
        try
        {
            if (structural)
            {
                LoadFolder(FolderPath, SelectedDocument?.FilePath);
            }
            else
            {
                foreach (var path in changed)
                {
                    var graph = _library.LoadFile(path);
                    var item = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (graph != null && item != null)
                    {
                        item.Replace(graph);
                        if (ReferenceEquals(item, SelectedDocument))
                        {
                            OnPropertyChanged(nameof(Graph));
                            ResetColumns();
                        }
                    }
                }
                UpdateErrors();
            }
            RestoreNavigationState(state);
        }
        finally
        {
            _focusOnRebuild = true;
        }

        // Search results hold DocNode instances; rebuild them from the new graph without popping the list open.
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var wasOpen = IsSearchOpen;
            RunSearch();
            IsSearchOpen = wasOpen;
        }
        StatusText = _loadError ?? $"Reloaded at {DateTime.Now:HH:mm:ss}";
    }

    private sealed record NavigationState(string? FilePath, ViewMode Mode, List<string> NodeIds);

    private NavigationState CaptureNavigationState()
    {
        var ids = Columns.Where(c => c.SelectedCard != null).Select(c => c.SelectedCard!.Node.Id).ToList();
        return new NavigationState(SelectedDocument?.FilePath, Mode, ids);
    }

    private void RestoreNavigationState(NavigationState state)
    {
        if (Graph == null || state.NodeIds.Count == 0) return;
        if (!string.Equals(Graph.FilePath, state.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        _mode = state.Mode;
        RaiseModeProperties();
        var path = state.NodeIds.Select(id => Graph.Find(id)).TakeWhile(n => n != null).Select(n => n!).ToList();
        if (path.Count > 0) ApplyPath(path);
        else ResetColumns();
    }

    // ----- columns ----------------------------------------------------------------------------------------------

    private void RaiseModeProperties()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsFlowsMode));
        OnPropertyChanged(nameof(IsComponentsMode));
        OnPropertyChanged(nameof(IsSectionsMode));
    }

    private void RaiseCollectionProperties()
    {
        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateHint));
    }

    /// <summary>Rebuilds the first column for the current document and mode.</summary>
    private void ResetColumns()
    {
        _suppressSelectionHandling = true;
        try
        {
            Columns.Clear();
            if (Graph != null) Columns.Add(BuildRootColumn());
        }
        finally
        {
            _suppressSelectionHandling = false;
        }
        Details = null;
        UpdateBreadcrumb();
        // Give the keyboard to the first column so Up/Down/Right work right after a document loads.
        if (_focusOnRebuild && Columns.Count > 0) FocusColumnRequested?.Invoke(0);
    }

    private IReadOnlyList<DocNode> RootsFor(ViewMode mode)
    {
        if (Graph == null) return Array.Empty<DocNode>();
        return mode switch
        {
            ViewMode.Flows => Graph.EntryPoints,
            ViewMode.Components => Graph.Components,
            ViewMode.Sections => Graph.Sections,
            _ => Array.Empty<DocNode>(),
        };
    }

    private ColumnViewModel BuildRootColumn()
    {
        var roots = RootsFor(Mode);
        IEnumerable<CardViewModel> cards = Mode switch
        {
            // Components: the pieces-table role as subtitle with the path underneath.
            ViewMode.Components => roots.Select(n => new CardViewModel(n, null, KindPalette.GroupLabel(n.Kind), n.Subtitle ?? n.Path, n.Subtitle != null ? n.Path : null)),
            // Flows: the section the flow item belongs to.
            ViewMode.Flows => roots.Select(n => new CardViewModel(n, null, string.Empty, n.Section?.Title ?? n.Subtitle)),
            _ => roots.Select(n => new CardViewModel(n, null, string.Empty)),
        };
        var header = Mode switch
        {
            ViewMode.Flows => "Entry points",
            ViewMode.Components => "Components",
            _ => "Sections",
        };
        var empty = Mode switch
        {
            ViewMode.Flows => "No entry points were found in this document. Try the Components or Sections view.",
            ViewMode.Components => "No components were found in this document.",
            _ => "No sections were found in this document.",
        };
        return new ColumnViewModel(0, header, Graph?.Root, cards.ToList(), empty, OnCardSelected);
    }

    private ColumnViewModel BuildChildColumn(DocNode parent, int index)
    {
        var cards = parent.ChildrenInOrder()
            .Where(e => e.Kind != EdgeKind.Mentions)
            .Select(e => new CardViewModel(e.To, e, e.Kind.ToString()))
            .ToList();
        return new ColumnViewModel(index, parent.Title, parent, cards, "Nothing below this card.", OnCardSelected);
    }

    private void OnCardSelected(ColumnViewModel column, CardViewModel? card)
    {
        if (_suppressSelectionHandling) return;
        if (!Columns.Contains(column)) return;

        // Truncate deeper columns.
        while (Columns.Count > column.Index + 1) Columns.RemoveAt(Columns.Count - 1);

        if (card != null && card.HasChildren)
        {
            Columns.Add(BuildChildColumn(card.Node, column.Index + 1));
            ColumnAppended?.Invoke();
        }

        var shown = card ?? (column.Index > 0 ? Columns[column.Index - 1].SelectedCard : null);
        ShowDetails(shown?.Node, shown?.Edge);
        UpdateBreadcrumb();
    }

    /// <summary>Keyboard focus moved to <paramref name="index"/> (Left/Backspace, or a click on an already selected
    /// card): show that column's selected card in the details pane and mark its crumb, without truncating deeper columns.</summary>
    public void SetCurrentColumn(int index)
    {
        if (index < 0 || index >= Columns.Count) return;
        var column = Columns[index];
        var card = column.SelectedCard;
        var shown = card ?? (index > 0 ? Columns[index - 1].SelectedCard : null);
        if (shown == null) return;
        if (!ReferenceEquals(Details?.Node, shown.Node)) ShowDetails(shown.Node, shown.Edge);
        UpdateBreadcrumb(card != null ? index : index - 1);
    }

    private void ShowDetails(DocNode? node, DocEdge? via = null)
    {
        if (node == null || Graph == null)
        {
            Details = null;
            return;
        }
        var services = new DetailsServices(NavigateTo, ShowInComponents, OpenDocument, ChooseSourceRoot, DocumentExists, _locator);
        Details = new DetailsViewModel(Graph, node, services, via);
    }

    /// <summary>Rebuilds the crumbs. <paramref name="currentColumn"/> marks the crumb the details pane shows (default:
    /// the deepest selection; -1 is the document). Long paths collapse their middle crumbs into one "…" crumb.</summary>
    private void UpdateBreadcrumb(int? currentColumn = null)
    {
        Breadcrumb.Clear();
        if (Graph == null) return;
        var selected = Columns.Where(c => c.SelectedCard != null).ToList();
        var current = currentColumn ?? (selected.Count == 0 ? -1 : selected[^1].Index);
        Breadcrumb.Add(new BreadcrumbItemViewModel(Graph.Title, -1, true, current == -1, BreadcrumbCommand));

        const int maxCrumbs = 5;
        if (selected.Count <= maxCrumbs)
        {
            foreach (var col in selected) Breadcrumb.Add(Crumb(col));
            return;
        }

        // first › … › second-to-last › last
        var hidden = selected.Skip(1).Take(selected.Count - 3).ToList();
        Breadcrumb.Add(Crumb(selected[0]));
        var hiddenIsCurrent = hidden.Any(c => c.Index == current);
        Breadcrumb.Add(new BreadcrumbItemViewModel("…", hidden[^1].Index, false, hiddenIsCurrent, BreadcrumbCommand,
            string.Join(" › ", hidden.Select(c => c.SelectedCard!.Title))));
        foreach (var col in selected.Skip(selected.Count - 2)) Breadcrumb.Add(Crumb(col));

        BreadcrumbItemViewModel Crumb(ColumnViewModel col)
            => new(col.SelectedCard!.Title, col.Index, false, col.Index == current, BreadcrumbCommand);
    }

    /// <summary>Breadcrumb click: keep the column of that crumb (and its children column), clear what is deeper.</summary>
    private void TruncateTo(int columnIndex)
    {
        if (columnIndex < 0)
        {
            if (Columns.Count > 0) Columns[0].SelectedCard = null;
            return;
        }
        if (columnIndex + 1 < Columns.Count)
        {
            var child = Columns[columnIndex + 1];
            child.SelectedCard = null; // truncates deeper columns via OnCardSelected
        }
        FocusColumnRequested?.Invoke(columnIndex);
    }

    /// <summary>Keyboard Right/Enter on a column: select the first card, or move into the next column.</summary>
    public bool DrillInto(ColumnViewModel column)
    {
        if (column.SelectedCard == null)
        {
            if (column.Cards.Count == 0) return false;
            column.SelectedCard = column.Cards[0];
            FocusColumnRequested?.Invoke(column.Index);
            return true;
        }
        if (Columns.Count > column.Index + 1 && Columns[column.Index + 1].Cards.Count > 0)
        {
            var next = Columns[column.Index + 1];
            next.SelectedCard ??= next.Cards[0];
            SetCurrentColumn(next.Index); // details follow the keyboard when the next column already had a selection
            FocusColumnRequested?.Invoke(next.Index);
            return true;
        }
        return false;
    }

    // ----- navigation -------------------------------------------------------------------------------------------

    /// <summary>Shows a node: finds a path from a root of the current (then any other) mode by BFS over incoming
    /// edges and rebuilds the columns; falls back to a single column with just the node.</summary>
    public void NavigateTo(DocNode node)
    {
        if (Graph == null) return;
        IsSearchOpen = false;
        // Re-home stale references (search results or Details links captured before a reload) to the current graph.
        node = Graph.Find(node.Id) ?? node;

        foreach (var mode in ModesStartingWith(Mode))
        {
            var path = FindPath(node, RootsFor(mode));
            if (path == null) continue;
            if (mode != Mode)
            {
                _mode = mode;
                RaiseModeProperties();
            }
            ApplyPath(path);
            return;
        }

        // No path from any root: show the node on its own.
        _suppressSelectionHandling = true;
        try
        {
            Columns.Clear();
            var card = new CardViewModel(node, null, string.Empty);
            Columns.Add(new ColumnViewModel(0, "Search result", null, new[] { card }, string.Empty, OnCardSelected));
        }
        finally
        {
            _suppressSelectionHandling = false;
        }
        Columns[0].SelectedCard = Columns[0].Cards[0];
        FocusColumnRequested?.Invoke(0);
    }

    private void ShowInComponents(DocNode node)
    {
        if (Graph == null) return;
        var path = FindPath(node, RootsFor(ViewMode.Components));
        if (path == null)
        {
            // Try the owner chain explicitly: owner → node when the graph has no Contains edge.
            var owner = node.Owner;
            if (owner != null) path = FindPath(owner, RootsFor(ViewMode.Components));
            if (path == null) { NavigateTo(node); return; }
        }
        _mode = ViewMode.Components;
        RaiseModeProperties();
        ApplyPath(path);
    }

    private static IEnumerable<ViewMode> ModesStartingWith(ViewMode first)
    {
        yield return first;
        foreach (var m in new[] { ViewMode.Flows, ViewMode.Components, ViewMode.Sections })
            if (m != first) yield return m;
    }

    /// <summary>BFS from <paramref name="target"/> over incoming non-Mentions edges until a root is reached.</summary>
    public static List<DocNode>? FindPath(DocNode target, IReadOnlyList<DocNode> roots)
    {
        if (roots.Count == 0) return null;
        var rootSet = new HashSet<DocNode>(roots);
        if (rootSet.Contains(target)) return new List<DocNode> { target };

        var parent = new Dictionary<DocNode, DocNode>(); // node → the node one step closer to the target
        var visited = new HashSet<DocNode> { target };
        var queue = new Queue<DocNode>();
        queue.Enqueue(target);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in current.Incoming)
            {
                if (edge.Kind == EdgeKind.Mentions) continue;
                var from = edge.From;
                if (!visited.Add(from)) continue;
                parent[from] = current;
                if (rootSet.Contains(from))
                {
                    var path = new List<DocNode> { from };
                    var n = from;
                    while (!ReferenceEquals(n, target))
                    {
                        n = parent[n];
                        path.Add(n);
                    }
                    return path;
                }
                queue.Enqueue(from);
            }
        }
        return null;
    }

    /// <summary>Rebuilds the columns so that each node of <paramref name="path"/> is selected in turn.</summary>
    private void ApplyPath(IReadOnlyList<DocNode> path)
    {
        _suppressSelectionHandling = true;
        try
        {
            Columns.Clear();
            if (Graph != null) Columns.Add(BuildRootColumn());
        }
        finally
        {
            _suppressSelectionHandling = false;
        }
        Details = null;

        var lastIndex = -1;
        for (var i = 0; i < path.Count; i++)
        {
            if (i >= Columns.Count) break;
            var card = Columns[i].FindCard(path[i]);
            if (card == null) break;
            Columns[i].SelectedCard = card; // appends the child column when the card has children
            lastIndex = i;
        }
        UpdateBreadcrumb();
        if (lastIndex >= 0)
        {
            ColumnAppended?.Invoke();
            if (_focusOnRebuild) FocusColumnRequested?.Invoke(lastIndex);
        }
    }

    // ----- search -----------------------------------------------------------------------------------------------

    private void RunSearch()
    {
        SearchResults.Clear();
        SelectedSearchResult = null;
        if (Graph != null && !string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var n in Graph.Search(SearchText).Take(60)) SearchResults.Add(new SearchResultViewModel(n));
        }
        OnPropertyChanged(nameof(HasSearchResults));
        IsSearchOpen = !string.IsNullOrWhiteSpace(SearchText);
    }

    public void ChooseSearchResult(SearchResultViewModel? result)
    {
        if (result == null) return;
        IsSearchOpen = false;
        NavigateTo(result.Node);
    }

    // ----- cleanup ----------------------------------------------------------------------------------------------

    private void DisposeWatcher()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch
        {
            // ignore
        }
        _watcher = null;
    }

    public void Dispose()
    {
        _reloadTimer.Stop();
        DisposeWatcher();
    }
}
