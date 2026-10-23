using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RDesk.Services;

namespace RDesk.Views.Controls;

/// <summary>
/// iOS / Windows 11 style segmented control: a soft recessed track with a raised white "thumb"
/// for the selected segment (accent-coloured text), instead of a dark filled toggle.
/// </summary>
public sealed class SegmentBar : UserControl
{
    private readonly Border _track = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), BorderThickness = new Thickness(1) };
    private readonly StackPanel _host = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Border> _segments = new();
    private readonly ThemeShadow _shadow = new();
    private int _selectedIndex = -1;

    public event EventHandler<int>? SelectionChanged;

    public SegmentBar()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        _track.Background = ThemeService.Brush(ThemeService.SegmentTrackBrushKey);
        _track.Child = _host;
        Content = _track;
    }

    public double SegmentFontSize { get; set; } = 12;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => Select(value, raise: false);
    }

    public int Count => _segments.Count;

    public void AddSegment(string text, string? glyph = null, string? tooltip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (!string.IsNullOrEmpty(glyph))
        {
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = SegmentFontSize - 1 });
        }

        if (!string.IsNullOrEmpty(text))
        {
            content.Children.Add(new TextBlock { Text = text, FontSize = SegmentFontSize, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        }

        var segment = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 3, 10, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = content
        };
        if (!string.IsNullOrEmpty(tooltip))
        {
            ToolTipService.SetToolTip(segment, tooltip);
        }

        var index = _segments.Count;
        segment.Tapped += (_, e) =>
        {
            e.Handled = true;
            Select(index, raise: true);
        };
        segment.PointerEntered += (_, _) =>
        {
            if (index != _selectedIndex)
            {
                segment.Background = ThemeService.Brush(ThemeService.ItemHoverBrushKey);
            }
        };
        segment.PointerExited += (_, _) =>
        {
            if (index != _selectedIndex)
            {
                segment.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        };

        _segments.Add(segment);
        _host.Children.Add(segment);
        Refresh();
    }

    public bool IsSegmentVisible(int index) =>
        index >= 0 && index < _segments.Count && _segments[index].Visibility == Visibility.Visible;

    public void SetSegmentVisible(int index, bool visible)
    {
        if (index >= 0 && index < _segments.Count)
        {
            _segments[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public int VisibleCount => _segments.Count(s => s.Visibility == Visibility.Visible);

    public void Select(int index, bool raise)
    {
        if (index < 0 || index >= _segments.Count)
        {
            return;
        }

        var changed = index != _selectedIndex;
        _selectedIndex = index;
        Refresh();
        if (raise && changed)
        {
            SelectionChanged?.Invoke(this, index);
        }
    }

    /// <summary>Re-reads theme brushes (selected = raised white thumb + accent text).</summary>
    public void Refresh()
    {
        _track.Background = ThemeService.Brush(ThemeService.SegmentTrackBrushKey);
        _track.BorderBrush = ThemeService.Brush(ThemeService.CardBorderBrushKey);
        for (var i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            var selected = i == _selectedIndex;
            seg.Background = selected
                ? ThemeService.Brush(ThemeService.SegmentSelectedBrushKey)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            seg.Shadow = selected ? _shadow : null;
            seg.Translation = selected ? new System.Numerics.Vector3(0, 0, 6) : System.Numerics.Vector3.Zero;
            var fg = selected
                ? ThemeService.Brush(ThemeService.AccentTextBrushKey)
                : ThemeService.Brush(ThemeService.SecondaryTextBrushKey);
            if (seg.Child is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    switch (child)
                    {
                        case TextBlock t:
                            t.Foreground = fg;
                            break;
                        case FontIcon f:
                            f.Foreground = fg;
                            break;
                    }
                }
            }
        }
    }
}
