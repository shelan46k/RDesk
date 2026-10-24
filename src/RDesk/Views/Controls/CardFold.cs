using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RDesk.Views.Controls;

internal static class CardFold
{
    public const string ExpandGlyph = "\uE70D";
    public const string CollapseGlyph = "\uE70E";

    private static readonly Thickness FoldedPadding = new(10, 7, 10, 7);
    private const double FoldedRowSpacing = 4;
    private static readonly Dictionary<Border, Thickness> OpenPadding = new();
    private static readonly Dictionary<Grid, double> OpenRowSpacing = new();

    public static void Apply(FrameworkElement? body, FontIcon? glyph, Button? button, bool folded,
        FrameworkElement? summary = null)
    {
        if (body is not null)
        {
            body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        }

        if (summary is not null)
        {
            summary.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (glyph is not null)
        {
            glyph.Glyph = folded ? ExpandGlyph : CollapseGlyph;
        }

        if (button is not null)
        {
            ToolTipService.SetToolTip(button, folded ? "展開這格" : "縮起這格");
        }

        Tighten(body ?? summary, folded);
    }

    private static void Tighten(FrameworkElement? start, bool folded)
    {
        if (FindParent<Grid>(start) is { } grid)
        {
            if (!OpenRowSpacing.ContainsKey(grid))
            {
                OpenRowSpacing[grid] = grid.RowSpacing;
            }

            grid.RowSpacing = folded ? FoldedRowSpacing : OpenRowSpacing[grid];
        }

        if (FindParent<Border>(start) is { } card)
        {
            if (!OpenPadding.ContainsKey(card))
            {
                OpenPadding[card] = card.Padding;
            }

            card.Padding = folded ? FoldedPadding : OpenPadding[card];
        }
    }

    private static T? FindParent<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var p = start is FrameworkElement fe ? fe.Parent : null; p is not null;)
        {
            if (p is T match)
            {
                return match;
            }

            p = p is FrameworkElement next ? next.Parent : null;
        }

        return null;
    }
}
