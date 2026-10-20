using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocsViewer.Infrastructure;
using DocsViewer.ViewModels;
using Microsoft.Win32;

namespace DocsViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        FocusSearchCommand = new RelayCommand(() => { SearchBox.Focus(); SearchBox.SelectAll(); });
        InitializeComponent();
        DataContext = _vm;

        _vm.FolderPicker = PickFolder;
        _vm.ColumnAppended += () => Dispatcher.BeginInvoke(ScrollColumnsToEnd, DispatcherPriority.Loaded);
        _vm.FocusColumnRequested += index => Dispatcher.BeginInvoke(() => FocusColumn(index), DispatcherPriority.Loaded);
    }

    public ICommand FocusSearchCommand { get; }

    // ----- folder picker --------------------------------------------------------------------------------------

    private string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder with plumbing documents",
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    // ----- columns --------------------------------------------------------------------------------------------

    private void ScrollColumnsToEnd()
    {
        ColumnsHost.UpdateLayout();
        ColumnsScroller.ScrollToRightEnd();
    }

    private ListBox? GetColumnListBox(int index)
    {
        if (index < 0 || index >= ColumnsHost.Items.Count) return null;
        ColumnsHost.UpdateLayout();
        var container = ColumnsHost.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
        return container == null ? null : FindDescendant<ListBox>(container);
    }

    private void FocusColumn(int index)
    {
        var list = GetColumnListBox(index);
        if (list == null) return;
        if (list.SelectedItem == null)
        {
            list.Focus();
            return;
        }
        list.UpdateLayout();
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item) item.Focus();
        else list.Focus();
    }

    private void ColumnList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
    }

    private void ColumnList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || list.DataContext is not ColumnViewModel column) return;
        switch (e.Key)
        {
            case Key.Right:
            case Key.Enter:
                if (_vm.DrillInto(column)) e.Handled = true;
                break;
            case Key.Left:
            case Key.Back:
                if (column.Index > 0)
                {
                    FocusColumn(column.Index - 1);
                    e.Handled = true;
                }
                break;
        }
    }

    // ----- search ---------------------------------------------------------------------------------------------

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_vm.SearchText)) _vm.IsSearchOpen = true;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _vm.IsSearchOpen && SearchResultsList.Items.Count > 0:
                if (SearchResultsList.SelectedIndex < 0) SearchResultsList.SelectedIndex = 0;
                FocusListItem(SearchResultsList, SearchResultsList.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.ChooseSearchResult(_vm.SelectedSearchResult ?? _vm.SearchResults.FirstOrDefault());
                e.Handled = true;
                break;
            case Key.Escape:
                if (_vm.IsSearchOpen) _vm.IsSearchOpen = false;
                else _vm.SearchText = string.Empty;
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _vm.ChooseSearchResult(_vm.SelectedSearchResult);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.IsSearchOpen = false;
                SearchBox.Focus();
                e.Handled = true;
                break;
            case Key.Up when SearchResultsList.SelectedIndex <= 0:
                SearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is SearchResultViewModel result)
        {
            _vm.ChooseSearchResult(result);
            e.Handled = true;
        }
    }

    private static void FocusListItem(ListBox list, int index)
    {
        if (index < 0 || index >= list.Items.Count) return;
        list.UpdateLayout();
        list.ScrollIntoView(list.Items[index]);
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        else list.Focus();
    }

    // ----- visual tree helpers --------------------------------------------------------------------------------

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var inner = FindDescendant<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
