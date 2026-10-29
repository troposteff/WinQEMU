using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinQEMU.Text;

namespace WinQEMU.Controls
{
    /// <summary>
    /// One resource over the last two minutes, as a line: one measure per chart, never two scales on one
    /// axis. Up to two series of the same unit (read and write, received and sent), each with its key in
    /// the legend. Hovering shows a crosshair and the values at that second.
    /// </summary>
    /// <remarks>
    /// Colours are the first two slots of the data-viz reference palette, blue then orange, stepped for
    /// each theme and validated for colour-blind separation; text stays in the theme's ink, never in a
    /// series colour. Lines 2px, gridlines hairline, a light fill under a lone series.
    /// </remarks>
    public sealed class MetricChart : Grid
    {
        private const double PlotHeight = 64;

        private static readonly Color[] Light = { Color.FromArgb(255, 0x2a, 0x78, 0xd6), Color.FromArgb(255, 0xeb, 0x68, 0x34) };
        private static readonly Color[] Dark = { Color.FromArgb(255, 0x39, 0x87, 0xe5), Color.FromArgb(255, 0xd9, 0x59, 0x26) };

        private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.75 };
        private readonly Canvas _plot = new() { Height = PlotHeight, Background = new SolidColorBrush(Colors.Transparent) };
        private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        private readonly TextBlock _note = new() { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly Border _tip = new() { Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBlock _tipText = new() { FontSize = 11 };
        private readonly Line _crosshair = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        private IReadOnlyList<(string Name, IReadOnlyList<double> Values)> _series = Array.Empty<(string, IReadOnlyList<double>)>();
        private double _max = 1;
        private double? _hoverX;

        public MetricChart()
        {
            RowSpacing = 4;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid();
            header.Children.Add(_title);
            header.Children.Add(_value);
            Children.Add(header);

            var plotHost = new Grid();
            plotHost.Children.Add(_plot);
            _tip.Child = _tipText;
            plotHost.Children.Add(_tip);
            SetRow(plotHost, 1);
            Children.Add(plotHost);

            SetRow(_legend, 2);
            Children.Add(_legend);
            SetRow(_note, 3);
            Children.Add(_note);

            _plot.SizeChanged += (_, _) => Redraw();
            _plot.PointerMoved += OnPointerMoved;
            _plot.PointerExited += (_, _) => { _hoverX = null; UpdateHover(); };
            ActualThemeChanged += (_, _) => Redraw();
        }

        public string Title { get => _title.Text; set => _title.Text = value; }

        /// <summary>How a value reads, with its unit ("23 %", "1,2 Mo/s").</summary>
        public Func<double, string> Format { get; set; } = v => v.ToString("0.#", CultureInfo.CurrentCulture);

        /// <summary>The top of the scale when it is known (100 %, the memory allocated); else it follows the data.</summary>
        public double? FixedMax { get; set; }

        /// <summary>Draws the series, oldest value first; NaN where nothing is known.</summary>
        /// <param name="note">Shown under the chart instead of the legend, e.g. why there is nothing to draw.</param>
        public void Show(IReadOnlyList<(string Name, IReadOnlyList<double> Values)> series, string? note = null)
        {
            _series = series;
            _note.Text = note ?? string.Empty;
            _note.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            var latest = series.Select(s => Last(s.Values)).ToList();
            _value.Text = series.Count == 1 ? (double.IsNaN(latest[0]) ? "—" : Format(latest[0])) : string.Empty;
            Redraw();
        }

        private static double Last(IReadOnlyList<double> values)
        {
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (!double.IsNaN(values[i]))
                    return values[i];
            }
            return double.NaN;
        }

        private Color SeriesColor(int index) => (ActualTheme == ElementTheme.Dark ? Dark : Light)[index % 2];

        /// <summary>A brush of this chart's theme (ThemeBrushes): the application's would be Windows' theme.</summary>
        private Brush Ink(string key, byte fallback) =>
            ThemeBrushes.Get(this, key) ?? new SolidColorBrush(Color.FromArgb(255, fallback, fallback, fallback));

        private void Redraw()
        {
            _plot.Children.Clear();
            var width = _plot.ActualWidth;
            if (width <= 0 || _series.Count == 0)
                return;

            var observed = _series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max();
            _max = FixedMax ?? NiceCeiling(Math.Max(observed * 1.15, 1));

            // Recessive frame: the baseline and the top of the scale, hairline, with the top's value.
            var grid = Ink("DividerStrokeColorDefaultBrush", 128);
            _plot.Children.Add(new Line { X1 = 0, X2 = width, Y1 = PlotHeight - 0.5, Y2 = PlotHeight - 0.5, Stroke = grid, StrokeThickness = 1 });
            _plot.Children.Add(new Line { X1 = 0, X2 = width, Y1 = 0.5, Y2 = 0.5, Stroke = grid, StrokeThickness = 1 });
            var scale = new TextBlock { Text = Format(_max), FontSize = 10, Opacity = 0.6, Foreground = Ink("TextFillColorSecondaryBrush", 100) };
            Canvas.SetLeft(scale, 2);
            Canvas.SetTop(scale, 1);
            _plot.Children.Add(scale);

            for (var index = 0; index < _series.Count; index++)
            {
                var values = _series[index].Values;
                var color = SeriesColor(index);
                foreach (var run in Runs(values, width))
                {
                    if (_series.Count == 1 && run.Count > 1)
                    {
                        var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)) };
                        area.Points.Add(new Windows.Foundation.Point(run[0].X, PlotHeight));
                        foreach (var point in run)
                            area.Points.Add(point);
                        area.Points.Add(new Windows.Foundation.Point(run[^1].X, PlotHeight));
                        _plot.Children.Add(area);
                    }
                    var line = new Polyline
                    {
                        Stroke = new SolidColorBrush(color),
                        StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    foreach (var point in run)
                        line.Points.Add(point);
                    _plot.Children.Add(line);
                }
            }

            _crosshair.Stroke = Ink("TextFillColorSecondaryBrush", 100);
            _crosshair.Y1 = 0;
            _crosshair.Y2 = PlotHeight;
            _plot.Children.Add(_crosshair);
            _tip.Background = Ink("SolidBackgroundFillColorBaseBrush", 240);
            _tip.BorderBrush = Ink("CardStrokeColorDefaultBrush", 200);
            _tip.BorderThickness = new Thickness(1);

            UpdateLegend();
            UpdateHover();
        }

        /// <summary>The known stretches of a series, as points; a gap where nothing is known breaks the line.</summary>
        private IEnumerable<List<Windows.Foundation.Point>> Runs(IReadOnlyList<double> values, double width)
        {
            var step = values.Count > 1 ? width / (values.Count - 1) : width;
            var run = new List<Windows.Foundation.Point>();
            for (var i = 0; i < values.Count; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    if (run.Count > 0)
                        yield return run;
                    run = new List<Windows.Foundation.Point>();
                    continue;
                }
                var y = PlotHeight - 1 - Math.Clamp(values[i] / _max, 0, 1) * (PlotHeight - 2);
                run.Add(new Windows.Foundation.Point(i * step, y));
            }
            if (run.Count > 0)
                yield return run;
        }

        private void UpdateLegend()
        {
            _legend.Children.Clear();
            _legend.Visibility = _series.Count > 1 && _note.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
            if (_series.Count < 2)
                return;
            for (var index = 0; index < _series.Count; index++)
            {
                var latest = Last(_series[index].Values);
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                item.Children.Add(new Rectangle
                {
                    Width = 12, Height = 2, RadiusX = 1, RadiusY = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(SeriesColor(index))
                });
                item.Children.Add(new TextBlock
                {
                    FontSize = 12,
                    Text = $"{_series[index].Name} {(double.IsNaN(latest) ? "—" : Format(latest))}"
                });
                _legend.Children.Add(item);
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _hoverX = e.GetCurrentPoint(_plot).Position.X;
            UpdateHover();
        }

        /// <summary>The crosshair at the nearest second, with each series' value then.</summary>
        private void UpdateHover()
        {
            var width = _plot.ActualWidth;
            if (_hoverX is not { } x || width <= 0 || _series.Count == 0 || _series[0].Values.Count < 2)
            {
                _crosshair.Visibility = Visibility.Collapsed;
                _tip.Visibility = Visibility.Collapsed;
                return;
            }

            var count = _series[0].Values.Count;
            var index = (int)Math.Round(Math.Clamp(x / width, 0, 1) * (count - 1));
            var px = index * width / (count - 1);
            _crosshair.X1 = _crosshair.X2 = px;
            _crosshair.Visibility = Visibility.Visible;

            var ago = count - 1 - index;
            var lines = new List<string> { ago == 0 ? DialogText.ChartNow : string.Format(CultureInfo.CurrentCulture, DialogText.ChartSecondsAgo, ago) };
            foreach (var (name, values) in _series)
            {
                var value = values[index];
                lines.Add((_series.Count > 1 ? name + " " : string.Empty) + (double.IsNaN(value) ? "—" : Format(value)));
            }
            _tipText.Text = string.Join("\n", lines);
            _tip.Visibility = Visibility.Visible;
            _tip.HorizontalAlignment = HorizontalAlignment.Left;
            _tip.VerticalAlignment = VerticalAlignment.Top;
            _tip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = px + 8 + _tip.DesiredSize.Width > width ? px - 8 - _tip.DesiredSize.Width : px + 8;
            _tip.Margin = new Thickness(Math.Max(0, left), 2, 0, 0);
        }

        /// <summary>1, 2 or 5 times a power of ten, at or above the value: a scale that reads well.</summary>
        public static double NiceCeiling(double value)
        {
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var step in new[] { 1d, 2d, 5d, 10d })
            {
                if (step * magnitude >= value)
                    return step * magnitude;
            }
            return 10 * magnitude;
        }
    }
}
