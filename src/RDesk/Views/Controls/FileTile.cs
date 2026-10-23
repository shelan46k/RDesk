using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RDesk.Services;

namespace RDesk.Views.Controls;

/// <summary>Compact "icon + name + meta" tile with a soft hover highlight (reference style).</summary>
public static class FileTile
{
    public static Border Create(ImageSource? icon, string title, string meta, string tooltip, bool pinned = false)
    {
        var img = new Image { Width = 24, Height = 24, Source = icon, VerticalAlignment = VerticalAlignment.Center };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Brush(ThemeService.PrimaryTextBrushKey)
        };
        var metaText = new TextBlock
        {
            Text = meta,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Brush(ThemeService.SecondaryTextBrushKey)
        };

        var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        if (pinned)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE840",
                FontSize = 10,
                Foreground = ThemeService.Brush(ThemeService.AccentTextBrushKey)
            });
            row.Children.Add(titleText);
            textStack.Children.Add(row);
        }
        else
        {
            textStack.Children.Add(titleText);
        }

        textStack.Children.Add(metaText);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(img);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var border = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(2),
            Child = grid
        };
        ToolTipService.SetToolTip(border, tooltip);
        border.PointerEntered += (_, _) => border.Background = ThemeService.Brush(ThemeService.ItemHoverBrushKey);
        border.PointerExited += (_, _) => border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return border;
    }
}
