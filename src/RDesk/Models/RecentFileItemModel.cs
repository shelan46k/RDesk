using Microsoft.UI.Xaml.Media;

namespace RDesk.Models;

public sealed class RecentFileItemModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ExtensionLabel { get; set; } = string.Empty;
    public string TimeLabel { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public ImageSource? Icon { get; set; }
}
