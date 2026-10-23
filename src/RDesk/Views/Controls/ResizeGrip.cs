using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RDesk.Services;

namespace RDesk.Views.Controls;

/// <summary>Bottom-right corner handle (dotted triangle) with the diagonal resize cursor.</summary>
public sealed class ResizeGrip : Grid
{
    public ResizeGrip()
    {
        Width = 18;
        Height = 18;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        ToolTipService.SetToolTip(this, "拖曳調整大小");

        var canvas = new Canvas { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (x, y) in new[] { (9, 1), (5, 5), (9, 5), (1, 9), (5, 9), (9, 9) })
        {
            var dot = new Ellipse
            {
                Width = 2.4,
                Height = 2.4,
                Fill = ThemeService.Brush(ThemeService.SecondaryTextBrushKey),
                Opacity = 0.7
            };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            canvas.Children.Add(dot);
        }

        Children.Add(canvas);
    }
}
