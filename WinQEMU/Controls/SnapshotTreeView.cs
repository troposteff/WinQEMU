using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinQEMU.Core.Qemu;
using WinQEMU.Text;
using WinQEMU.ViewModels;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Draws a machine's snapshot tree as a file tree is drawn: one box per line, top to bottom, each set in from the one
    /// it comes from and joined to it by a right-angled line. SnapshotTree gives the shape (who comes from whom, in what
    /// order); the lines are its nodes in depth-first order.
    /// </summary>
    /// <remarks>
    /// Top to bottom rather than VMware's left to right: from three levels on, left to right ran out of the summary's
    /// width and the tree was cut, with a scroll bar for the rest. Downwards, a level costs one indent, not one box.
    /// </remarks>
    public sealed class SnapshotTreeView : Canvas
    {
        private const double BoxWidth = 300;
        private const double BoxHeight = 46;
        private const double Indent = 28;
        private const double RowGap = 8;

        /// <summary>Each node's line and level, for the drawing in progress.</summary>
        private readonly Dictionary<SnapshotTreeNode, (int Row, int Depth)> _places = new();

        /// <summary>A box was clicked: a snapshot to select, or a linked clone to open.</summary>
        public event Action<SnapshotTreeNode>? NodeClicked;

        /// <summary>Redraws the tree, with <paramref name="selectedName"/>'s box marked.</summary>
        /// <param name="items">The list's items, for each snapshot's description and date.</param>
        public void Show(SnapshotTreeNode? root, IReadOnlyCollection<SnapshotItem> items, string? selectedName)
        {
            Children.Clear();
            if (root is null)
            {
                Width = Height = 0;
                return;
            }

            // SelfAndDescendants is depth-first, children in order: exactly a file tree's lines.
            var nodes = root.SelfAndDescendants().ToList();
            _places.Clear();
            Place(root, 0);
            var lines = Themed("ControlStrongStrokeColorDefaultBrush");
            foreach (var node in nodes)
            {
                foreach (var child in node.Children)
                    Children.Add(Connector(node, child, lines));
            }
            foreach (var node in nodes)
                Children.Add(Box(node, items.FirstOrDefault(i => !node.IsRetained && node.Kind == SnapshotNodeKind.Snapshot && i.Name == node.Title),
                    node.Kind == SnapshotNodeKind.Snapshot && !node.IsRetained && node.Title == selectedName));

            Width = _places.Values.Max(p => p.Depth) * Indent + BoxWidth;
            Height = _places.Count * (BoxHeight + RowGap) - RowGap;
        }

        private void Place(SnapshotTreeNode node, int depth)
        {
            _places[node] = (_places.Count, depth);
            foreach (var child in node.Children)
                Place(child, depth + 1);
        }

        /// <summary>A brush of this element's theme (ThemeBrushes); grey should a name ever be missing, rather than a crash.</summary>
        private Brush Themed(string key) => ThemeBrushes.Get(this, key) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

        private double Left(SnapshotTreeNode node) => _places[node].Depth * Indent;

        private double Top(SnapshotTreeNode node) => _places[node].Row * (BoxHeight + RowGap);

        /// <summary>Down from under the parent's box, then right into the child's, as a file tree joins its lines.</summary>
        private Polyline Connector(SnapshotTreeNode parent, SnapshotTreeNode child, Brush brush)
        {
            var x = Left(parent) + Indent / 2;
            var startY = Top(parent) + BoxHeight;
            var endY = Top(child) + BoxHeight / 2;
            return new Polyline
            {
                Points = { new(x, startY), new(x, endY), new(Left(child), endY) },
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = child.IsRetained ? new DoubleCollection { 3, 2 } : null
            };
        }

        private Border Box(SnapshotTreeNode node, SnapshotItem? item, bool selected)
        {
            var (glyph, subtitle) = node.Kind switch
            {
                SnapshotNodeKind.Origin => ("", DialogText.TreeOriginSubtitle),
                SnapshotNodeKind.Current => ("", DialogText.TreeCurrentSubtitle),
                SnapshotNodeKind.LinkedClone => ("", DialogText.TreeLinkedCloneSubtitle),
                _ when node.IsRetained => ("", DialogText.TreeRetainedSubtitle),
                _ => ("", item is null ? string.Empty : $"{item.CreatedText} · {item.DetailText}")
            };

            var current = node.Kind == SnapshotNodeKind.Current;
            var foreground = current ? Themed("TextOnAccentFillColorPrimaryBrush") : null;
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = node.Title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = foreground ?? Themed("TextFillColorPrimaryBrush")
            });
            if (subtitle.Length > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Opacity = 0.75,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = foreground ?? Themed("TextFillColorSecondaryBrush")
                });
            }

            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
            if (foreground is not null)
                icon.Foreground = foreground;
            content.Children.Add(icon);
            Grid.SetColumn(text, 1);
            content.Children.Add(text);

            var box = new Border
            {
                Width = BoxWidth,
                Height = BoxHeight,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = Themed(selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
                Background = Themed(current ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"),
                Opacity = node.IsRetained ? 0.6 : 1,
                Child = content
            };
            SetLeft(box, Left(node));
            SetTop(box, Top(node));
            ToolTipService.SetToolTip(box, item is null ? node.Title : string.Format(CultureInfo.CurrentCulture, DialogText.TreeTooltip, item.Name, item.DetailText, item.CreatedText));

            if (node.Kind is SnapshotNodeKind.Snapshot or SnapshotNodeKind.LinkedClone && !node.IsRetained)
                box.Tapped += (_, _) => NodeClicked?.Invoke(node);
            return box;
        }
    }
}
