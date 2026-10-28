using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using WinQEMU.Core.Models;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// The library's folders, as VMware Workstation's inventory has them: the tree, its lines in the list, and
    /// the folder operations. The tree itself is <see cref="LibraryTree"/>, kept in the settings.
    /// </summary>
    /// <remarks>
    /// Selection still has one way in for the rest of the shell: <see cref="SelectedItem"/>, a machine or nothing.
    /// The list binds <see cref="SelectedRow"/>, which can also be a folder; a folder selected means no machine
    /// selected, so every machine command is disabled exactly as with an empty library.
    /// </remarks>
    public partial class MainViewModel
    {
        private readonly Dictionary<string, FolderRow> _folderRows = new(StringComparer.Ordinal);
        private readonly HashSet<VmListItem> _watchedRows = new();
        private bool _rowsRefreshQueued;
        private bool _selectingFromList;

        public LibraryTree Library => Settings.Library;

        /// <summary>The lines of the library list: the tree flattened, or the search matches.</summary>
        public ObservableCollection<LibraryRow> Rows { get; } = new();

        private LibraryRow? _selectedRow;
        /// <summary>What the list has selected, a machine or a folder. Bound by the list only.</summary>
        public LibraryRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (ReferenceEquals(_selectedRow, value))
                    return;
                SetSelectedRowField(value);

                _selectingFromList = true;
                try
                {
                    // A folder selected leaves no machine selected.
                    SelectedItem = value as VmListItem;
                }
                finally
                {
                    _selectingFromList = false;
                }
            }
        }

        public FolderRow? SelectedFolder => _selectedRow as FolderRow;

        public bool IsFolderSelected => SelectedFolder is not null;

        public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

        /// <summary>The empty library's message: not while folders are listed, which it would be drawn over.</summary>
        public bool ShowsLibraryEmpty => HasNoVirtualMachines && Rows.Count == 0;

        public LibrarySort LibrarySort
        {
            get => Library.Sort;
            set
            {
                if (Library.Sort == value)
                    return;
                Library.Sort = value;
                SaveLibrary();
                RaisePropertyChanged();
                RefreshLibraryRows();
            }
        }

        /// <summary>Called once by the constructor.</summary>
        private void InitializeLibraryFolders()
        {
            // A machine going up or down changes a folder's count, and its place when sorted by state. Queued:
            // state changes arrive on background threads.
            Manager.SessionCreated += (_, session) =>
                session.StateChanged += (_, _) => _dispatcher.TryEnqueue(QueueLibraryRowsRefresh);

            // A machine deleted, or removed from the list, leaves its folder. Only removals: a reload clears
            // the list (a reset, which says nothing about which machine went) and adds everything back.
            Manager.VirtualMachines.CollectionChanged += (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Remove || e.OldItems is null)
                    return;
                var changed = false;
                foreach (var machine in e.OldItems.OfType<VmConfig>())
                    changed |= Library.Machines.Remove(machine.Id);
                if (changed)
                    SaveLibrary();
            };
        }

        /// <summary>
        /// After the library is loaded: repairs the tree, and forgets the machines no longer in the library.
        /// Not while the library's folder is missing (a disconnected drive): every machine would look gone,
        /// and all of them would lose their folder.
        /// </summary>
        private void RepairLibraryFolders()
        {
            var library = Directory.Exists(Settings.EffectiveLibraryRoot)
                ? Manager.VirtualMachines.Select(vm => vm.Id)
                : null;
            if (Library.Repair(library, LibraryText.UnnamedFolder))
                SaveLibrary();
        }

        private void SaveLibrary()
        {
            try
            {
                Settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportError(ex);
            }
        }

        // ---- Lines of the list ----

        private void QueueLibraryRowsRefresh()
        {
            if (_rowsRefreshQueued)
                return;
            _rowsRefreshQueued = true;
            _dispatcher.TryEnqueue(() =>
            {
                _rowsRefreshQueued = false;
                RefreshLibraryRows();
            });
        }

        /// <summary>
        /// Brings <see cref="Rows"/> in line with the tree, or with the search. Changed in place rather than
        /// rebuilt: emptying the list made the ListView drop its selection, and a dropped selection detaches the
        /// console — on every expand, every move, and every state change when sorted by state.
        /// </summary>
        private void RefreshLibraryRows()
        {
            var sort = Library.Sort;
            MachineSortKey Key(VmListItem item) => MachineSortKey.Of(item.Config, item.State);

            var target = new List<LibraryRow>();
            if (IsSearching)
            {
                // Flat, folders hidden: a match deep in a closed folder is still found. Its folders say where it is.
                foreach (var item in LibraryLayout.Order(Items, Key, sort))
                {
                    item.Depth = 0;
                    item.Location = PathText(Library.FolderOf(item.Config.Id));
                    target.Add(item);
                }
            }
            else
            {
                var counts = LibraryLayout.Count(Library, _allItems, i => i.Config.Id, i => i.State);
                foreach (var entry in LibraryLayout.Flatten(Library, _allItems, i => i.Config.Id, Key, sort))
                {
                    if (entry.Folder is { } folder)
                    {
                        if (!_folderRows.TryGetValue(folder.Id, out var row))
                            _folderRows[folder.Id] = row = new FolderRow(folder);
                        var (machines, running) = counts[folder.Id];
                        var folders = Library.Folders.Count(f => f.Id != folder.Id && Library.IsSameOrInside(f.Id, folder.Id));
                        var parentPath = PathText(folder.ParentId);
                        row.Update(folder, entry.Depth, machines, running, folders, parentPath);
                        target.Add(row);
                    }
                    else if (entry.Machine is { } item)
                    {
                        item.Depth = entry.Depth;
                        item.Location = string.Empty;
                        target.Add(item);
                    }
                }
            }

            foreach (var gone in _folderRows.Keys.Where(id => Library.Find(id) is null).ToList())
                _folderRows.Remove(gone);

            // A rename or a first start changes a machine's place in its folder.
            _watchedRows.RemoveWhere(item => !_allItems.Contains(item));
            foreach (var item in _allItems.Where(_watchedRows.Add))
                item.PropertyChanged += OnRowPropertyChanged;

            LibraryLayout.Synchronize(Rows, target, _selectedRow);

            RaisePropertyChanged(nameof(ShowsLibraryEmpty));
            if (SelectedFolder is not null)
                RaisePropertyChanged(nameof(SelectedFolder));
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // VmListItem raises Name for any change of its configuration, LastStartedUtc included.
            if (e.PropertyName is nameof(VmListItem.Name) or nameof(VmListItem.State))
                QueueLibraryRowsRefresh();
        }

        /// <summary>"Work › Linux"; empty for the root.</summary>
        private string PathText(string? folderId) =>
            string.Join(" › ", Library.PathOf(folderId).Select(f => f.Name));

        private void SetSelectedRowField(LibraryRow? row)
        {
            _selectedRow = row;
            RaisePropertyChanged(nameof(SelectedRow));
            RaisePropertyChanged(nameof(SelectedFolder));
            RaisePropertyChanged(nameof(IsFolderSelected));
        }

        /// <summary>
        /// Follows a machine selected by code (a new machine, a clone opened from the snapshot tree, the start of
        /// the list) into the list, opening its folders if they hide it. Called by the setter of SelectedItem.
        /// </summary>
        private void OnSelectedItemChanged(VmListItem? item)
        {
            if (_selectingFromList)
                return;
            if (item is null)
            {
                if (_selectedRow is VmListItem)
                    SetSelectedRowField(null);
                return;
            }
            if (!IsSearching && Library.Reveal(Library.FolderOf(item.Config.Id)))
            {
                SaveLibrary();
                RefreshLibraryRows();
            }
            if (Rows.Contains(item))
                SetSelectedRowField(item);
        }

        /// <summary>A folder is selected and still listed: a rebuild of the rows keeps it rather than picking a machine.</summary>
        private bool KeepsSelectedFolder() => _selectedRow is FolderRow folder && Rows.Contains(folder);

        /// <summary>The first machine the list shows; the first folder when every machine is in a closed one.</summary>
        private void SelectFirstRow()
        {
            if (Rows.OfType<VmListItem>().FirstOrDefault() is { } machine)
                SelectedItem = machine;
            else if (Rows.FirstOrDefault() is FolderRow folder)
                SelectedRow = folder;
            else
                SelectedItem = null;
        }

        // ---- Folder operations ----

        /// <summary>Where "New folder" creates one: in the selected folder, or next to the selected machine.</summary>
        public string? CurrentFolderId =>
            SelectedFolder?.Id ?? (SelectedVm is { } vm ? Library.FolderOf(vm.Id) : null);

        /// <summary>A new machine (created, cloned, imported) goes where the user is in the tree, as VMware does.</summary>
        private void PlaceNewMachine(VmConfig config)
        {
            if (Library.Machines.ContainsKey(config.Id) || CurrentFolderId is not { } folder)
                return;
            Library.MoveMachine(config.Id, folder);
            SaveLibrary();
            RefreshLibraryRows();
        }

        public string? WhyFolderNameIsRefused(string name, string? parentId, string? exceptId = null) =>
            Describe(Library.CheckName(name, parentId, exceptId), name.Trim());

        /// <summary>Puts a folder problem into words; null for none.</summary>
        public static string? Describe(FolderProblem problem, string name) => problem switch
        {
            FolderProblem.None => null,
            FolderProblem.EmptyName => LibraryText.FolderNameEmpty,
            FolderProblem.NameTaken => string.Format(CultureInfo.CurrentCulture, LibraryText.FolderNameTaken, name),
            FolderProblem.IntoItself => LibraryText.FolderIntoItself,
            _ => LibraryText.FolderNotFound
        };

        /// <summary>Creates a folder and selects it. Returns why it was refused, or null.</summary>
        public string? CreateFolder(string name, string? parentId)
        {
            var problem = Library.CreateFolder(name, parentId, out var created);
            if (created is null)
                return Describe(problem, name.Trim());
            SaveLibrary();
            RefreshLibraryRows();
            if (_folderRows.TryGetValue(created.Id, out var row))
                SelectedRow = row;
            return null;
        }

        public string? RenameFolder(FolderRow folder, string name)
        {
            var problem = Library.Rename(folder.Id, name);
            if (problem != FolderProblem.None)
                return Describe(problem, name.Trim());
            SaveLibrary();
            RefreshLibraryRows();
            return null;
        }

        /// <summary>Deletes a folder, never a machine: its content moves to its parent, which is then selected.</summary>
        public void DeleteFolder(FolderRow folder)
        {
            var parentId = folder.Folder.ParentId;
            // Moved off the line first, so the list never drops its selection (see RefreshLibraryRows).
            if (ReferenceEquals(_selectedRow, folder) && parentId is not null && _folderRows.TryGetValue(parentId, out var parent))
                SelectedRow = parent;
            if (!Library.DeleteFolder(folder.Id))
                return;
            SaveLibrary();
            RefreshLibraryRows();
            if (_selectedRow is null || !Rows.Contains(_selectedRow))
                SelectFirstRow();
        }

        /// <summary>Opens or closes a folder. Closing one that holds the selection selects the folder, as a tree does.</summary>
        public void SetExpanded(FolderRow folder, bool expanded)
        {
            if (folder.IsExpanded == expanded)
                return;
            if (!expanded && IsInside(_selectedRow, folder.Id))
                SelectedRow = folder;
            folder.Folder.IsExpanded = expanded;
            SaveLibrary();
            RefreshLibraryRows();
        }

        /// <summary>Whether a line lies somewhere inside the folder (the folder itself excluded).</summary>
        private bool IsInside(LibraryRow? row, string folderId) => row switch
        {
            VmListItem machine => Library.FolderOf(machine.Config.Id) is { } id && Library.IsSameOrInside(id, folderId),
            FolderRow other => other.Id != folderId && Library.IsSameOrInside(other.Id, folderId),
            _ => false
        };

        /// <summary>The folder a line is in: a machine's folder, a folder's parent; null for the root.</summary>
        public string? ContainerOf(LibraryRow row) => row switch
        {
            VmListItem machine => Library.FolderOf(machine.Config.Id),
            FolderRow folder => folder.Folder.ParentId,
            _ => null
        };

        /// <summary>
        /// Why a line cannot go into <paramref name="folderId"/> (null: the root), or null when it can. Going where
        /// it already is counts as refused, so a drop there shows no move.
        /// </summary>
        public string? WhyCannotMove(LibraryRow row, string? folderId)
        {
            if (ContainerOf(row) == folderId)
                return string.Empty;
            return row switch
            {
                VmListItem => folderId is null || Library.Find(folderId) is not null ? null : LibraryText.FolderNotFound,
                FolderRow folder => Describe(Library.WhyFolderCannotMove(folder.Id, folderId), folder.Name),
                _ => string.Empty
            };
        }

        /// <summary>Moves a machine or a folder into <paramref name="folderId"/> (null: the root), and keeps it selected.</summary>
        public string? Move(LibraryRow row, string? folderId)
        {
            if (WhyCannotMove(row, folderId) is { } refusal)
                return refusal.Length == 0 ? null : refusal;

            var problem = row switch
            {
                VmListItem machine => Library.MoveMachine(machine.Config.Id, folderId),
                FolderRow folder => Library.MoveFolder(folder.Id, folderId),
                _ => FolderProblem.None
            };
            if (problem != FolderProblem.None)
                return Describe(problem, (row as FolderRow)?.Name ?? string.Empty);

            // Opened, so what was moved stays in view and stays selected.
            Library.Reveal(folderId);
            SaveLibrary();
            RefreshLibraryRows();
            return null;
        }

        /// <summary>Every folder for a "Move to" menu, depth first, with its depth.</summary>
        public IReadOnlyList<(LibraryFolder Folder, int Depth)> AllFolders() => Library.AllFolders();

        public string FolderName(string? folderId) =>
            Library.Find(folderId)?.Name ?? LibraryText.LibraryRoot;
    }
}
