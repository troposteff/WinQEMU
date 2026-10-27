using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinQEMU.Core.Qemu;
using WinQEMU.ViewModels;

namespace WinQEMU.Controls
{
    /// <summary>
    /// Draws a machine's snapshot tree as VMware's snapshot manager does: boxes from left to right, each
    /// joined to the one it comes from by a right-angled line. The layout itself is SnapshotTree's.
    /// </summary>
    public sealed class SnapshotTreeView : Canvas
    {
        private const double BoxWidth = 176;
        private const double BoxHeight = 52;
        private const double ColumnGap = 36;
        private const double RowGap = 14;

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

            var nodes = root.SelfAndDescendants().ToList();
            var lines = Themed("ControlStrongStrokeColorDefaultBrush");
            foreach (var node in nodes)
            {
                foreach (var child in node.Children)
                    Children.Add(Connector(node, child, lines));
            }
            foreach (var node in nodes)
                Children.Add(Box(node, items.FirstOrDefault(i => !node.IsRetained && node.Kind == SnapshotNodeKind.Snapshot && i.Name == node.Title),
                    node.Kind == SnapshotNodeKind.Snapshot && !node.IsRetained && node.Title == selectedName));

            Width = (nodes.Max(n => n.Column) + 1) * (BoxWidth + ColumnGap);
            Height = (nodes.Max(n => n.Row) + 1) * (BoxHeight + RowGap);
        }

        /// <summary>A brush of the current theme; grey should a name ever be missing, rather than a crash.</summary>
        private static Brush Themed(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        private static double Left(SnapshotTreeNode node) => node.Column * (BoxWidth + ColumnGap);

        private static double Top(SnapshotTreeNode node) => node.Row * (BoxHeight + RowGap);

        /// <summary>From the right of the parent to the left of the child, turning halfway across the gap.</summary>
        private static Polyline Connector(SnapshotTreeNode parent, SnapshotTreeNode child, Brush brush)
        {
            var startX = Left(parent) + BoxWidth;
            var startY = Top(parent) + BoxHeight / 2;
            var endX = Left(child);
            var endY = Top(child) + BoxHeight / 2;
            var turnX = startX + ColumnGap / 2;
            return new Polyline
            {
                Points = { new(startX, startY), new(turnX, startY), new(turnX, endY), new(endX, endY) },
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = child.IsRetained ? new DoubleCollection { 3, 2 } : null
            };
        }

        private Border Box(SnapshotTreeNode node, SnapshotItem? item, bool selected)
        {
            var (glyph, subtitle) = node.Kind switch
            {
                SnapshotNodeKind.Origin => ("", "avant tout instantané"),
                SnapshotNodeKind.Current => ("", "état actuel de la machine"),
                SnapshotNodeKind.LinkedClone => ("", "clone lié"),
                _ when node.IsRetained => ("", "conservé pour un clone lié"),
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
            ToolTipService.SetToolTip(box, item is null ? node.Title : $"{item.Name}\n{item.DetailText}\nPris le {item.CreatedText}");

            if (node.Kind is SnapshotNodeKind.Snapshot or SnapshotNodeKind.LinkedClone && !node.IsRetained)
                box.Tapped += (_, _) => NodeClicked?.Invoke(node);
            return box;
        }
    }
}
