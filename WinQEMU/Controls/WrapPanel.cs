using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinQEMU.Controls
{
    /// <summary>
    /// A row of buttons that goes on to the next line when the column is too narrow, which WinUI has no panel for: in a
    /// horizontal StackPanel the last of the encryption section's buttons came out as « Oublier le mot de p ».
    /// </summary>
    public sealed class WrapPanel : Panel
    {
        public double HorizontalSpacing { get; set; } = 8;

        public double VerticalSpacing { get; set; } = 8;

        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0, y = 0, lineHeight = 0, width = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                if (child.Visibility == Visibility.Collapsed)
                    continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > availableSize.Width)
                {
                    y += lineHeight + VerticalSpacing;
                    x = 0;
                    lineHeight = 0;
                }
                x += size.Width + HorizontalSpacing;
                width = Math.Max(width, x - HorizontalSpacing);
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return new Size(width, y + lineHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, lineHeight = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed)
                    continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width)
                {
                    y += lineHeight + VerticalSpacing;
                    x = 0;
                    lineHeight = 0;
                }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width + HorizontalSpacing;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return finalSize;
        }
    }
}
