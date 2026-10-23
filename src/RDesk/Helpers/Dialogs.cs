using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RDesk.Services;

namespace RDesk.Helpers;

public static class Dialogs
{
    private static bool _open;

    /// <summary>Only one ContentDialog may be open per XamlRoot; extra requests are ignored.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open)
        {
            return ContentDialogResult.None;
        }

        _open = true;
        try
        {
            dialog.RequestedTheme = AppHost.Current.Theme.CurrentElementTheme;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            StartupLog.Write("Dialog failed", ex);
            return ContentDialogResult.None;
        }
        finally
        {
            _open = false;
        }
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string initial, string? hint = null)
    {
        var box = new TextBox { Text = initial, PlaceholderText = hint ?? string.Empty };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            var dot = initial.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : initial.Length);
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? box.Text?.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirmText = "確定")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了"
        };
        await ShowAsync(dialog);
    }
}
