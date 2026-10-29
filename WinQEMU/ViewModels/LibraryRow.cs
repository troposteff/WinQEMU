using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using WinQEMU.Core.Models;
using WinQEMU.Text;

namespace WinQEMU.ViewModels
{
    /// <summary>
    /// One line of the library list: a machine (<see cref="VmListItem"/>) or a folder (<see cref="FolderRow"/>),
    /// at its depth in the tree. The list is the tree flattened (see MainViewModel.RefreshLibraryRows), so the
    /// depth is all a line needs to know about where it sits.
    /// </summary>
    public abstract class LibraryRow : ObservableObject
    {
        /// <summary>Indentation per level: a machine's status dot lines up under its folder's icon.</summary>
        public const double IndentStep = 24;

        private int _depth;
        /// <summary>0 at the root. Every line is at 0 while a search shows the matches flat.</summary>
        public int Depth
        {
            get => _depth;
            set
            {
                if (SetProperty(ref _depth, value))
                    RaisePropertyChanged(nameof(Indent));
            }
        }

        public Thickness Indent => new(Depth * IndentStep, 0, 0, 0);

        private string _location = string.Empty;
        /// <summary>The folders a search match is in ("Work › Linux"), shown under it; empty in the tree.</summary>
        public string Location
        {
            get => _location;
            set
            {
                if (SetProperty(ref _location, value ?? string.Empty))
                    RaisePropertyChanged(nameof(HasLocation));
            }
        }

        public bool HasLocation => Location.Length > 0;

        /// <summary>The language changed: every text of the line is read again.</summary>
        public void RefreshText() => RaisePropertyChanged(string.Empty);
    }

    /// <summary>A folder of the library, as its line in the list shows it.</summary>
    public sealed class FolderRow : LibraryRow
    {
        public FolderRow(LibraryFolder folder) => Folder = folder;

        /// <summary>The settings' own folder: renaming or moving it there is what the line then shows.</summary>
        public LibraryFolder Folder { get; private set; }

        public string Id => Folder.Id;

        public string Name => Folder.Name;

        public bool IsExpanded => Folder.IsExpanded;

        /// <summary>Segoe Fluent chevrons: down when open, right when closed, as in Explorer's tree.</summary>
        public string ChevronGlyph => IsExpanded ? "" : "";

        public string FolderGlyph => IsExpanded ? "" : "";

        public int MachineCount { get; private set; }

        public int RunningCount { get; private set; }

        public int FolderCount { get; private set; }

        /// <summary>"3 machines, 1 running", or "empty".</summary>
        public string Summary => Describe(MachineCount, RunningCount);

        /// <summary>
        /// "Folder Work, 3 machines": a screen reader otherwise reads the class name of the bound object. Open or
        /// closed is not in it: the line says that through the expand/collapse pattern (Controls.LibraryListView).
        /// </summary>
        public string AutomationName => string.Format(CultureInfo.CurrentCulture, LibraryText.FolderAutomationName, Name, Summary);

        /// <summary>Where the folder is, for its summary: "Work › Linux", or empty at the root.</summary>
        public string ParentPath { get; private set; } = string.Empty;

        public string ParentPathText => ParentPath.Length == 0
            ? LibraryText.NewFolderAtRoot
            : string.Format(CultureInfo.CurrentCulture, LibraryText.NewFolderIn, ParentPath);

        /// <summary>Takes the folder's current state; raises only what changed, so an unchanged line is not redrawn.</summary>
        public void Update(LibraryFolder folder, int depth, int machines, int running, int folders, string parentPath)
        {
            Depth = depth;
            var changed = !ReferenceEquals(Folder, folder) || MachineCount != machines || RunningCount != running
                || FolderCount != folders || ParentPath != parentPath;
            Folder = folder;
            MachineCount = machines;
            RunningCount = running;
            FolderCount = folders;
            ParentPath = parentPath;
            if (changed)
            {
                RaisePropertyChanged(nameof(Summary));
                RaisePropertyChanged(nameof(ParentPath));
                RaisePropertyChanged(nameof(ParentPathText));
            }
            // Name and expansion live in the folder, which the tree changes in place: always raised.
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(AutomationName));
            RaisePropertyChanged(nameof(IsExpanded));
            RaisePropertyChanged(nameof(ChevronGlyph));
            RaisePropertyChanged(nameof(FolderGlyph));
        }

        public static string Describe(int machines, int running)
        {
            var count = machines switch
            {
                0 => LibraryText.MachineCountNone,
                1 => LibraryText.MachineCountOne,
                _ => string.Format(CultureInfo.CurrentCulture, LibraryText.MachineCountMany, machines)
            };
            return running > 0
                ? count + ", " + string.Format(CultureInfo.CurrentCulture, LibraryText.RunningCount, running)
                : count;
        }

        /// <summary>"3 machines, 2 folders": what deleting the folder moves up a level.</summary>
        public string ContentText
        {
            get
            {
                var parts = new List<string>();
                if (MachineCount > 0)
                    parts.Add(MachineCount == 1
                        ? LibraryText.MachineCountOne
                        : string.Format(CultureInfo.CurrentCulture, LibraryText.MachineCountMany, MachineCount));
                if (FolderCount > 0)
                    parts.Add(FolderCount == 1
                        ? LibraryText.FolderCountOne
                        : string.Format(CultureInfo.CurrentCulture, LibraryText.FolderCountMany, FolderCount));
                return string.Join(", ", parts);
            }
        }

        public bool IsEmpty => MachineCount == 0 && FolderCount == 0;
    }
}
