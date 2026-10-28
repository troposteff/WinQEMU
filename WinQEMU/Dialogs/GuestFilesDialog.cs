using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinQEMU.Core.Sharing;
using WinQEMU.Core.Text;
using WinQEMU.Text;
using WinQEMU.ViewModels;

namespace WinQEMU.Dialogs
{
    /// <summary>
    /// The guest's files, browsed through its agent, to choose what to copy to this PC. Opens on the guest
    /// user's desktop; a double click enters a folder; several items can be chosen in one folder.
    /// </summary>
    /// <remarks>Built in code: a list, a path and two buttons do not need a XAML file of their own.</remarks>
    public sealed class GuestFilesDialog : ContentDialog
    {
        private readonly MainViewModel _viewModel;
        private readonly bool _windows;
        private readonly TextBox _path = new() { MinWidth = 420 };
        private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 340, MinWidth = 520 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        private string _folder = string.Empty;

        /// <summary>Full paths in the guest of what was chosen; empty when cancelled.</summary>
        public IReadOnlyList<string> Chosen { get; private set; } = Array.Empty<string>();

        public GuestFilesDialog(MainViewModel viewModel)
        {
            _viewModel = viewModel;
            _windows = viewModel.IsSelectedWindowsGuest;
            Title = DialogText.GuestFilesTitle;
            PrimaryButtonText = DialogText.GuestFilesButton;
            CloseButtonText = CommonText.Cancel;
            DefaultButton = ContentDialogButton.Primary;
            IsPrimaryButtonEnabled = false;

            var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
            ToolTipService.SetToolTip(up, DialogText.GuestFilesParent);
            up.Click += async (_, _) => await OpenAsync(GuestFilePull.Parent(_folder, _windows));
            _path.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                    await OpenAsync(_path.Text.Trim());
            };
            _list.SelectionChanged += (_, _) => IsPrimaryButtonEnabled = _list.SelectedItems.Count > 0;
            _list.IsDoubleTapEnabled = true;
            _list.DoubleTapped += async (_, _) =>
            {
                if (_list.SelectedItems.LastOrDefault() is Item { Entry.IsDirectory: true } item)
                    await OpenAsync(GuestFilePull.Combine(_folder, item.Entry.Name, _windows));
            };
            PrimaryButtonClick += (_, _) =>
                Chosen = _list.SelectedItems.OfType<Item>().Select(i => GuestFilePull.Combine(_folder, i.Entry.Name, _windows)).ToList();

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            top.Children.Add(up);
            top.Children.Add(_path);
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(top);
            panel.Children.Add(_list);
            panel.Children.Add(_status);
            Content = panel;

            Loaded += async (_, _) =>
            {
                try
                {
                    await OpenAsync(await _viewModel.GuestHomeAsync());
                }
                catch (Exception ex) when (ex is Core.Qemu.GuestAgentException or InvalidOperationException or System.IO.IOException)
                {
                    _status.Text = ex.Message;
                }
            };
        }

        private async Task OpenAsync(string folder)
        {
            if (folder.Length == 0)
                return;
            _status.Text = DialogText.GuestFilesReading;
            try
            {
                var entries = await _viewModel.ListGuestFolderAsync(folder);
                _folder = folder;
                _path.Text = folder;
                _list.ItemsSource = entries.Select(e => new Item(e)).ToList();
                _status.Text = entries.Count == 0
                    ? DialogText.GuestFilesEmpty
                    : DialogText.GuestFilesHint;
            }
            catch (Exception ex) when (ex is Core.Qemu.GuestAgentException or InvalidOperationException or System.IO.IOException)
            {
                _status.Text = ex.Message;
            }
        }

        private sealed record Item(GuestFileEntry Entry)
        {
            public override string ToString() => Entry.IsDirectory
                ? $"📁  {Entry.Name}"
                : $"📄  {Entry.Name}    {Sizes.Format(Entry.Size)}";
        }
    }
}
