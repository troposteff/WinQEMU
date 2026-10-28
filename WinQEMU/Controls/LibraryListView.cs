using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.ViewModels;

namespace WinQEMU.Controls
{
    /// <summary>
    /// The library list: the folder tree flattened into a ListView, each line indented by its depth.
    /// </summary>
    /// <remarks>
    /// A ListView rather than WinUI's TreeView. The rest of the shell hangs on the list's selection (console,
    /// toolbar, summary), and the ListView's is the one that already worked: one bound SelectedItem, kept when
    /// lines are inserted or removed around it, keyboard navigation, virtualisation, the machine line's template
    /// unchanged. TreeView bound to data selects through its nodes, loses the selection when a branch is rebuilt,
    /// and cannot select an item a search has hidden, all of which the library does. What a tree gives that a list
    /// does not, opening and closing a line, is added here: the expand/collapse automation pattern and the level of
    /// each line for screen readers; the keys and the chevron are handled by MainWindow.
    /// </remarks>
    public sealed class LibraryListView : ListView
    {
        /// <summary>Opens (true) or closes (false) a folder: what a screen reader's expand and collapse do.</summary>
        public Action<FolderRow, bool>? SetExpanded { get; set; }

        protected override DependencyObject GetContainerForItemOverride() => new LibraryListViewItem(this);

        protected override bool IsItemItsOwnContainerOverride(object item) => item is LibraryListViewItem;

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is LibraryListViewItem container)
                container.Bind(item as LibraryRow);
        }

        protected override void ClearContainerForItemOverride(DependencyObject element, object item)
        {
            base.ClearContainerForItemOverride(element, item);
            if (element is LibraryListViewItem container)
                container.Bind(null);
        }
    }

    /// <summary>A line of the library list, which knows the row it shows for its automation peer.</summary>
    public sealed class LibraryListViewItem : ListViewItem
    {
        internal LibraryListViewItem(LibraryListView owner) => Owner = owner;

        internal LibraryListView Owner { get; }

        public LibraryRow? Row { get; private set; }

        internal void Bind(LibraryRow? row)
        {
            if (Row is not null)
                Row.PropertyChanged -= OnRowPropertyChanged;
            Row = row;
            _expanded = (row as FolderRow)?.IsExpanded;
            if (Row is not null)
                Row.PropertyChanged += OnRowPropertyChanged;
            UpdateLevel();
        }

        private void UpdateLevel() =>
            AutomationProperties.SetLevel(this, (Row?.Depth ?? 0) + 1);

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LibraryRow.Depth))
                UpdateLevel();
            else if (e.PropertyName == nameof(FolderRow.IsExpanded) && sender is FolderRow folder)
            {
                // The row raises IsExpanded on every refresh; only a real change is announced.
                var was = _expanded;
                _expanded = folder.IsExpanded;
                if (was is not null && was != _expanded
                    && FrameworkElementAutomationPeer.FromElement(this) is LibraryListViewItemPeer peer)
                    peer.RaiseExpandCollapseChanged(folder.IsExpanded);
            }
        }

        private bool? _expanded;

        protected override AutomationPeer OnCreateAutomationPeer() => new LibraryListViewItemPeer(this);
    }

    /// <summary>
    /// A folder line says it can be opened and closed, and whether it is, as a tree item would; Narrator then reads
    /// "collapsed" or "expanded" and its own commands open and close it.
    /// </summary>
    public sealed class LibraryListViewItemPeer : ListViewItemAutomationPeer, IExpandCollapseProvider
    {
        private readonly LibraryListViewItem _item;

        public LibraryListViewItemPeer(LibraryListViewItem item) : base(item) => _item = item;

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse && _item.Row is FolderRow
                ? this
                : base.GetPatternCore(patternInterface);

        protected override int GetLevelCore() => (_item.Row?.Depth ?? 0) + 1;

        public ExpandCollapseState ExpandCollapseState => _item.Row switch
        {
            FolderRow { IsExpanded: true } => ExpandCollapseState.Expanded,
            FolderRow => ExpandCollapseState.Collapsed,
            _ => ExpandCollapseState.LeafNode
        };

        public void Expand()
        {
            if (_item.Row is FolderRow folder)
                _item.Owner.SetExpanded?.Invoke(folder, true);
        }

        public void Collapse()
        {
            if (_item.Row is FolderRow folder)
                _item.Owner.SetExpanded?.Invoke(folder, false);
        }

        internal void RaiseExpandCollapseChanged(bool expanded) =>
            RaisePropertyChangedEvent(
                ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                expanded ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded,
                expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    /// <summary>The machine line's template for a machine, the folder line's for a folder.</summary>
    public sealed class LibraryRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? MachineTemplate { get; set; }

        public DataTemplate? FolderTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item) =>
            (item is FolderRow ? FolderTemplate : MachineTemplate)!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
            SelectTemplateCore(item);
    }
}
