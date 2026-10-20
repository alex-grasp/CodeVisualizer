using System.Collections;
using System.Windows.Data;
using System.Windows.Input;
using DocsViewer.Core.Model;
using DocsViewer.Infrastructure;

namespace DocsViewer.ViewModels;

/// <summary>What the first Miller column shows.</summary>
public enum ViewMode
{
    Flows,
    Components,
    Sections,
}

/// <summary>One entry in the document list. Child documents (Home-Export-Plumbing.md under Home-Plumbing.md) are
/// indented by <see cref="Depth"/>.</summary>
public sealed class DocumentItemViewModel : ObservableObject
{
    public DocumentItemViewModel(DocGraph graph, int depth = 0, string? parentTitle = null)
    {
        Graph = graph;
        Depth = depth;
        ParentTitle = parentTitle;
    }

    public DocGraph Graph { get; private set; }

    public int Depth { get; }
    public bool IsChild => Depth > 0;
    public string? ParentTitle { get; }
    public System.Windows.Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string Title => Graph.Title;
    public string FileName => System.IO.Path.GetFileName(Graph.FilePath);
    public string FilePath => Graph.FilePath;
    public string? Summary => Graph.Summary;
    public string? Area => Graph.Area;
    public string ToolTipText => ParentTitle == null ? Summary ?? FileName : $"Part of {ParentTitle}\n{Summary ?? FileName}";

    /// <summary>Swaps the graph after a reload and refreshes the display properties.</summary>
    public void Replace(DocGraph graph)
    {
        Graph = graph;
        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Area));
        OnPropertyChanged(nameof(ToolTipText));
    }
}

/// <summary>A document related to the selected one, shown as a chip under the breadcrumb: its parent ("Part of"),
/// its children ("Includes") or a document it refers to ("See also").</summary>
public sealed class RelatedDocViewModel
{
    public RelatedDocViewModel(string relation, DocumentItemViewModel document, ICommand open)
    {
        Relation = relation;
        Document = document;
        OpenCommand = open;
    }

    public string Relation { get; }
    public DocumentItemViewModel Document { get; }
    public string Title => Document.Title;
    public string ToolTip => $"{Relation}: {Document.FileName}";
    public ICommand OpenCommand { get; }
}

/// <summary>"see AIPanel-Plumbing.md §3" found in a node's notes: opens that document, at that section when given.</summary>
public sealed class DocLinkViewModel
{
    public DocLinkViewModel(string docName, string? section, bool exists, ICommand open)
    {
        DocName = docName;
        Section = section;
        Exists = exists;
        OpenCommand = open;
    }

    public string DocName { get; }
    public string? Section { get; }
    public bool Exists { get; }
    public string Label => Section == null ? DocName : $"{DocName} §{Section}";
    public string ToolTip => Exists ? $"Open {Label}" : $"{DocName} is not in the open folder";
    public ICommand OpenCommand { get; }
}

/// <summary>One card in a column.</summary>
public sealed class CardViewModel : ObservableObject
{
    private bool _isSelected;

    /// <param name="subtitle">Overrides the default subtitle (e.g. the flow section for Flow root cards).</param>
    /// <param name="pathLine">An extra muted line under the subtitle (the path of a Components root card).</param>
    public CardViewModel(DocNode node, DocEdge? edge, string group, string? subtitle = null, string? pathLine = null)
    {
        Node = node;
        Edge = edge;
        Group = group;
        ChildCount = node.ChildrenInOrder().Count(e => e.Kind != EdgeKind.Mentions);
        Subtitle = subtitle ?? DefaultSubtitle(node);
        PathLine = string.Equals(pathLine, Subtitle, StringComparison.Ordinal) ? null : pathLine;
    }

    public DocNode Node { get; }
    public DocEdge? Edge { get; }

    /// <summary>Group header shown above the card (edge kind in child columns, node kind group in Components).</summary>
    public string Group { get; }

    public ColumnViewModel? Column { get; internal set; }

    public string Title => Node.Title;
    public string? Subtitle { get; }

    /// <summary>Optional second muted line (source path) shown under the subtitle.</summary>
    public string? PathLine { get; }

    /// <summary>Subtitle ?? Path, except that the parser's generic "Section"/"Subsection" subtitle (redundant under the
    /// Section badge) is replaced by the title of the section's first flow item.</summary>
    private static string? DefaultSubtitle(DocNode node)
    {
        if (node.Kind == NodeKind.Section &&
            (node.Subtitle == null || node.Subtitle.Equals("Section", StringComparison.OrdinalIgnoreCase) ||
             node.Subtitle.Equals("Subsection", StringComparison.OrdinalIgnoreCase)))
        {
            var first = node.ChildrenInOrder().FirstOrDefault(e => e.Kind == EdgeKind.Contains && e.To.Kind != NodeKind.Section);
            return first?.To.Title;
        }
        return node.Subtitle ?? node.Path;
    }
    public NodeKind Kind => Node.Kind;
    public string KindLabel => KindPalette.Label(Node.Kind);
    public string? EdgeLabel => Edge?.Label;
    public bool HasChildren => ChildCount > 0;
    public int ChildCount { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public override string ToString() => Title;
}

/// <summary>One Miller column.</summary>
public sealed class ColumnViewModel : ObservableObject
{
    private readonly Action<ColumnViewModel, CardViewModel?> _onSelected;
    private CardViewModel? _selectedCard;

    public ColumnViewModel(int index, string header, DocNode? parent, IReadOnlyList<CardViewModel> cards, string emptyText,
        Action<ColumnViewModel, CardViewModel?> onSelected)
    {
        Index = index;
        Header = header;
        Parent = parent;
        Cards = cards;
        EmptyText = emptyText;
        _onSelected = onSelected;
        foreach (var c in cards) c.Column = this;

        var list = new List<CardViewModel>(cards);
        View = new ListCollectionView(list);
        if (cards.Any(c => !string.IsNullOrEmpty(c.Group)))
            View.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(CardViewModel.Group)));
        View.MoveCurrentToPosition(-1);
    }

    public int Index { get; }
    public string Header { get; }
    public DocNode? Parent { get; }
    public IReadOnlyList<CardViewModel> Cards { get; }
    public ListCollectionView View { get; }
    public string EmptyText { get; }
    public bool IsEmpty => Cards.Count == 0;
    public string CountText => Cards.Count == 1 ? "1 card" : $"{Cards.Count} cards";

    public CardViewModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (ReferenceEquals(_selectedCard, value)) return;
            if (_selectedCard != null) _selectedCard.IsSelected = false;
            _selectedCard = value;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged();
            _onSelected(this, value);
        }
    }

    public CardViewModel? FindCard(DocNode node) => Cards.FirstOrDefault(c => ReferenceEquals(c.Node, node))
        ?? Cards.FirstOrDefault(c => string.Equals(c.Node.Id, node.Id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One crumb in the breadcrumb bar.</summary>
public sealed class BreadcrumbItemViewModel
{
    public BreadcrumbItemViewModel(string title, int columnIndex, bool isFirst, bool isLast, ICommand command, string? toolTip = null)
    {
        Title = title;
        ColumnIndex = columnIndex;
        IsFirst = isFirst;
        IsLast = isLast;
        Command = command;
        ToolTip = toolTip ?? title;
    }

    public string Title { get; }

    /// <summary>Full text for the tooltip (lists the hidden crumbs for the "…" crumb).</summary>
    public string ToolTip { get; }

    /// <summary>-1 for the document itself, otherwise the column whose selected card this crumb represents.</summary>
    public int ColumnIndex { get; }

    public bool IsFirst { get; }
    public bool IsLast { get; }
    public ICommand Command { get; }
}

/// <summary>A search hit.</summary>
public sealed class SearchResultViewModel
{
    public SearchResultViewModel(DocNode node)
    {
        Node = node;
    }

    public DocNode Node { get; }
    public string Title => Node.Title;
    public NodeKind Kind => Node.Kind;
    public string KindLabel => KindPalette.Label(Node.Kind);
    public string? Subtitle => Node.Subtitle ?? Node.Path ?? Node.Owner?.Title ?? Node.Section?.Title;
}

/// <summary>An incoming edge in the "Called by" list.</summary>
public sealed class IncomingEdgeViewModel
{
    public IncomingEdgeViewModel(DocEdge edge, ICommand navigate)
    {
        Edge = edge;
        NavigateCommand = navigate;
    }

    public DocEdge Edge { get; }
    public DocNode Node => Edge.From;
    public string Title => Edge.From.Title;
    public NodeKind Kind => Edge.From.Kind;
    public string KindLabel => KindPalette.Label(Edge.From.Kind);
    public string Relation => Edge.Label == null ? Edge.Kind.ToString() : $"{Edge.Kind} ({Edge.Label})";
    public ICommand NavigateCommand { get; }
}

/// <summary>A place in the markdown where the node appears.</summary>
public sealed class SourceRefViewModel
{
    public SourceRefViewModel(SourceRef source)
    {
        Source = source;
    }

    public SourceRef Source { get; }
    public string SectionTitle => string.IsNullOrWhiteSpace(Source.SectionTitle) ? "(document)" : Source.SectionTitle;
    public int Line => Source.Line;
    public string LineText => $"line {Source.Line}";
    public string Text => Source.Text;
}

/// <summary>One line of the markdown excerpt.</summary>
public sealed class ExcerptLineViewModel
{
    public ExcerptLineViewModel(int number, string text, bool isHighlighted)
    {
        Number = number;
        Text = text;
        IsHighlighted = isHighlighted;
    }

    public int Number { get; }
    public string Text { get; }
    public bool IsHighlighted { get; }
}
