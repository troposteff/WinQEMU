using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using WinQEMU.Controls;
using WinQEMU.Core.Models;
using WinQEMU.Core.Text;
using WinQEMU.Text;
using WinQEMU.ViewModels;

namespace WinQEMU
{
    /// <summary>The library's folders: the list's keys, chevrons, context menu, drag and drop, and folder dialogs.</summary>
    public sealed partial class MainWindow
    {
        private bool _libraryListReady;
        private LibraryRow? _draggedRow;

        private void OnLibraryListLoaded(object sender, RoutedEventArgs e)
        {
            if (_libraryListReady)
                return;
            _libraryListReady = true;
            VmList.SetExpanded = _viewModel.SetExpanded;
            // handledEventsToo: the list item may take the tap for its selection before it reaches us.
            VmList.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnLibraryTapped), true);
            VmList.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnLibraryDoubleTapped), true);
        }

        /// <summary>The line an element of the list belongs to, and whether the element is (inside) a folder's chevron.</summary>
        private static (LibraryListViewItem? Container, bool OnChevron) LineOf(object? source)
        {
            var onChevron = false;
            for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element is FrameworkElement { Tag: "Chevron" })
                    onChevron = true;
                if (element is LibraryListViewItem container)
                    return (container, onChevron);
            }
            return (null, false);
        }

        private void OnLibraryTapped(object sender, TappedRoutedEventArgs e)
        {
            if (LineOf(e.OriginalSource) is { Container.Row: FolderRow folder, OnChevron: true })
                _viewModel.SetExpanded(folder, !folder.IsExpanded);
        }

        private void OnLibraryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // A double click on the chevron already toggled twice through Tapped.
            if (LineOf(e.OriginalSource) is { Container.Row: FolderRow folder, OnChevron: false })
                _viewModel.SetExpanded(folder, !folder.IsExpanded);
        }

        /// <summary>
        /// The keys of a tree, as Explorer's: Right opens a folder then goes to its first line, Left closes it or
        /// goes up to the folder holding the line, Enter opens or closes; F2 renames a folder, Delete deletes it.
        /// Up and Down stay the list's own.
        /// </summary>
        private async void OnLibraryKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.OriginalSource is not LibraryListViewItem { Row: { } row })
                return;

            switch (e.Key)
            {
                case VirtualKey.Right when row is FolderRow folder:
                    e.Handled = true;
                    if (!folder.IsExpanded)
                        _viewModel.SetExpanded(folder, true);
                    else if (_viewModel.Rows.IndexOf(folder) is var at && at + 1 < _viewModel.Rows.Count
                             && _viewModel.Rows[at + 1].Depth > folder.Depth)
                        SelectAndFocus(_viewModel.Rows[at + 1]);
                    break;

                case VirtualKey.Left:
                    e.Handled = true;
                    if (row is FolderRow { IsExpanded: true } open)
                        _viewModel.SetExpanded(open, false);
                    else if (!_viewModel.IsSearching && _viewModel.ContainerOf(row) is { } parentId
                             && _viewModel.Rows.OfType<FolderRow>().FirstOrDefault(f => f.Id == parentId) is { } parent)
                        SelectAndFocus(parent);
                    break;

                case VirtualKey.Enter when row is FolderRow folder:
                    e.Handled = true;
                    _viewModel.SetExpanded(folder, !folder.IsExpanded);
                    break;

                case VirtualKey.F2 when row is FolderRow folder:
                    e.Handled = true;
                    await RenameFolderAsync(folder);
                    break;

                case VirtualKey.Delete when row is FolderRow folder:
                    e.Handled = true;
                    await DeleteFolderAsync(folder);
                    break;
            }
        }

        private void SelectAndFocus(LibraryRow row)
        {
            _viewModel.SelectedRow = row;
            VmList.ScrollIntoView(row);
            (VmList.ContainerFromItem(row) as Control)?.Focus(FocusState.Keyboard);
        }

        // ---- Context menu ----

        /// <summary>Right click, Shift+F10 or the menu key: on a line, or on the empty part of the list.</summary>
        private void OnLibraryContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;
            var (container, _) = LineOf(e.OriginalSource);
            var row = container?.Row;
            var menu = new MenuFlyout();

            void Add(string text, string glyph, Action click, bool enabled = true)
            {
                var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
                item.Click += (_, _) => click();
                menu.Items.Add(item);
            }

            if (row is FolderRow folder)
            {
                if (folder.IsExpanded)
                    Add(LibraryText.Collapse, "", () => _viewModel.SetExpanded(folder, false));
                else
                    Add(LibraryText.Expand, "", () => _viewModel.SetExpanded(folder, true));
                menu.Items.Add(new MenuFlyoutSeparator());
                Add(LibraryText.NewSubfolder, "", () => _ = NewFolderAsync(folder.Id));
                Add(LibraryText.RenameFolder, "", () => _ = RenameFolderAsync(folder));
                Add(LibraryText.DeleteFolder, "", () => _ = DeleteFolderAsync(folder));
            }
            else if (row is null)
            {
                Add(LibraryText.NewFolder, "", () => _ = NewFolderAsync(null));
            }

            if (row is not null)
            {
                if (menu.Items.Count > 0)
                    menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MoveToMenu(row));
            }

            // Selected first, so what the menu acts on is what the summary shows.
            if (row is not null && !ReferenceEquals(_viewModel.SelectedRow, row))
                _viewModel.SelectedRow = row;

            // A window of its own, as the toolbar's menus: opened near the console's edge, a menu drawn by XAML
            // would go under QEMU's window.
            menu.ShouldConstrainToRootBounds = false;
            menu.OverlayInputPassThroughElement = RootGrid;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_openMenu, menu))
                    _openMenu = null;
            };
            _openMenu = menu;
            if (e.TryGetPosition(VmList, out var point))
                menu.ShowAt(VmList, new FlyoutShowOptions { Position = point });
            else if (container is not null)
                menu.ShowAt(container);
            else
                menu.ShowAt(VmList);
        }

        /// <summary>"Move to" and every folder, indented as in the tree; where the line cannot go is disabled.</summary>
        private MenuFlyoutSubItem MoveToMenu(LibraryRow row)
        {
            var menu = new MenuFlyoutSubItem { Text = LibraryText.MoveTo, Icon = new FontIcon { Glyph = "" } };

            void Add(string text, string? folderId, int depth)
            {
                var item = new MenuFlyoutItem
                {
                    // Em spaces: a menu item has no indentation of its own, and ordinary spaces are too narrow to read as one.
                    Text = new string(' ', depth) + text,
                    IsEnabled = _viewModel.WhyCannotMove(row, folderId) is null
                };
                item.Click += (_, _) => MoveRow(row, folderId);
                menu.Items.Add(item);
            }

            Add(LibraryText.LibraryRoot, null, 0);
            foreach (var (folder, depth) in _viewModel.AllFolders())
                Add(folder.Name, folder.Id, depth + 1);
            return menu;
        }

        private void MoveRow(LibraryRow row, string? folderId)
        {
            if (_viewModel.Move(row, folderId) is { } error)
                ShowLibraryError(error);
        }

        private async void ShowLibraryError(string message)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Content = DialogBody(message),
                CloseButtonText = CommonText.Ok,
                XamlRoot = Content.XamlRoot
            });
        }

        // ---- Drag and drop within the list ----

        private void OnLibraryDragStarting(object sender, DragItemsStartingEventArgs e)
        {
            // A search shows no folders to drop on.
            if (_viewModel.IsSearching || e.Items.FirstOrDefault() is not LibraryRow row)
            {
                e.Cancel = true;
                return;
            }
            _draggedRow = row;
            e.Data.RequestedOperation = DataPackageOperation.Move;
        }

        private void OnLibraryDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggedRow = null;

        /// <summary>
        /// Where a drop at this point files the dragged line: into a folder dropped on, beside a machine dropped on
        /// (its folder), at the root below the last line.
        /// </summary>
        private string? DropFolderAt(Point point)
        {
            foreach (var row in _viewModel.Rows)
            {
                if (VmList.ContainerFromItem(row) is not FrameworkElement container)
                    continue;
                var top = container.TransformToVisual(VmList).TransformPoint(new Point(0, 0)).Y;
                if (point.Y >= top && point.Y < top + container.ActualHeight)
                    return row is FolderRow folder ? folder.Id : _viewModel.ContainerOf(row);
            }
            return null;
        }

        private void OnLibraryDragOver(object sender, DragEventArgs e)
        {
            // Only our own lines: files from Explorer are for the console.
            if (_draggedRow is not { } row)
                return;
            var folderId = DropFolderAt(e.GetPosition(VmList));
            var refusal = _viewModel.WhyCannotMove(row, folderId);
            e.AcceptedOperation = refusal is null ? DataPackageOperation.Move : DataPackageOperation.None;
            if (e.DragUIOverride is { } ui)
            {
                ui.Caption = refusal is { Length: > 0 }
                    ? refusal
                    : folderId is null
                        ? LibraryText.DragMoveToRoot
                        : string.Format(CultureInfo.CurrentCulture, LibraryText.DragMoveInto, _viewModel.FolderName(folderId));
                ui.IsCaptionVisible = refusal is not { Length: 0 };
                ui.IsGlyphVisible = true;
            }
            e.Handled = true;
        }

        private void OnLibraryDrop(object sender, DragEventArgs e)
        {
            if (_draggedRow is not { } row)
                return;
            e.Handled = true;
            var folderId = DropFolderAt(e.GetPosition(VmList));
            if (_viewModel.WhyCannotMove(row, folderId) is null)
                MoveRow(row, folderId);
            _draggedRow = null;
        }

        // ---- Buttons and dialogs ----

        private void OnNewFolderClick(object sender, RoutedEventArgs e) => _ = NewFolderAsync(_viewModel.CurrentFolderId);

        private void OnRenameFolderClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedFolder is { } folder)
                _ = RenameFolderAsync(folder);
        }

        private void OnDeleteFolderClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedFolder is { } folder)
                _ = DeleteFolderAsync(folder);
        }

        private void OnSortClick(object sender, RoutedEventArgs e)
        {
            if (OnlyClosedItsMenu(sender))
                return;
            var menu = new MenuFlyout();
            void Add(string text, LibrarySort sort)
            {
                var item = new RadioMenuFlyoutItem { Text = text, GroupName = "LibrarySort", IsChecked = _viewModel.LibrarySort == sort };
                item.Click += (_, _) => _viewModel.LibrarySort = sort;
                menu.Items.Add(item);
            }
            Add(LibraryText.SortByName, LibrarySort.Name);
            Add(LibraryText.SortByState, LibrarySort.State);
            Add(LibraryText.SortByLastUsed, LibrarySort.LastUsed);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = LibraryText.SortNote, IsEnabled = false });
            menu.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
            ShowAboveConsole(menu, (FrameworkElement)sender);
        }

        private Task NewFolderAsync(string? parentId) =>
            AskFolderNameAsync(
                LibraryText.NewFolder,
                LibraryText.CreateButton,
                _viewModel.Library.UniqueName(LibraryText.DefaultFolderName, parentId),
                parentId,
                exceptId: null,
                name => _viewModel.CreateFolder(name, parentId));

        private Task RenameFolderAsync(FolderRow folder) =>
            AskFolderNameAsync(
                LibraryText.RenameFolderTitle,
                LibraryText.RenameButton,
                folder.Name,
                folder.Folder.ParentId,
                folder.Id,
                name => _viewModel.RenameFolder(folder, name));

        /// <summary>A folder's name, checked as it is typed (non-empty, unique among its siblings), then applied.</summary>
        private async Task AskFolderNameAsync(
            string title, string primary, string initial, string? parentId, string? exceptId, Func<string, string?> apply)
        {
            var box = new TextBox { Header = LibraryText.FolderNameLabel, Text = initial };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var critical) && critical is Brush brush)
                error.Foreground = brush;
            var where = new TextBlock
            {
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Text = parentId is null
                    ? LibraryText.NewFolderAtRoot
                    : string.Format(CultureInfo.CurrentCulture, LibraryText.NewFolderIn, _viewModel.FolderName(parentId))
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 360, Children = { where, box, error } };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = primary,
                CloseButtonText = CommonText.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            void Check()
            {
                var refusal = _viewModel.WhyFolderNameIsRefused(box.Text, parentId, exceptId);
                dialog.IsPrimaryButtonEnabled = refusal is null;
                // An empty box needs no sentence: the disabled button says enough while the user types.
                error.Text = refusal ?? string.Empty;
                error.Visibility = refusal is not null && box.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            box.TextChanged += (_, _) => Check();
            box.Loaded += (_, _) =>
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (apply(box.Text) is { } refusal)
                {
                    args.Cancel = true;
                    error.Text = refusal;
                    error.Visibility = Visibility.Visible;
                }
            };
            Check();

            await ShowDialogAsync(dialog);
        }

        /// <summary>Deletes a folder, asking first when it holds anything; machines and files are never deleted.</summary>
        private async Task DeleteFolderAsync(FolderRow folder)
        {
            if (!folder.IsEmpty)
            {
                var parentId = folder.Folder.ParentId;
                var body = parentId is null
                    ? string.Format(CultureInfo.CurrentCulture, LibraryText.DeleteFolderToRoot, folder.ContentText)
                    : string.Format(CultureInfo.CurrentCulture, LibraryText.DeleteFolderToParent, folder.ContentText, _viewModel.FolderName(parentId));
                var result = await ShowDialogAsync(new ContentDialog
                {
                    Title = string.Format(CultureInfo.CurrentCulture, LibraryText.DeleteFolderTitle, folder.Name),
                    Content = DialogBody(body),
                    PrimaryButtonText = LibraryText.DeleteFolder,
                    CloseButtonText = CommonText.Cancel,
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                });
                if (result != ContentDialogResult.Primary)
                    return;
            }
            _viewModel.DeleteFolder(folder);
        }
    }
}
