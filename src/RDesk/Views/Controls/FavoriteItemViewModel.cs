using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using RDesk.Helpers;
using RDesk.Models;
using RDesk.Services;

namespace RDesk.Views.Controls;

public sealed class FavoriteItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public FavoriteItemViewModel(FavoriteItemModel model)
    {
        Model = model;
        Exists = model.IsDirectory ? Directory.Exists(model.FullPath) : File.Exists(model.FullPath);
        if (!Exists)
        {
            return;
        }

        try
        {
            if (model.IsDirectory)
            {
                Modified = Directory.GetLastWriteTime(model.FullPath);
            }
            else
            {
                var info = new FileInfo(model.FullPath);
                Size = info.Length;
                Modified = info.LastWriteTime;
            }
        }
        catch
        {
        }
    }

    public FavoriteItemModel Model { get; }
    public bool Exists { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string Category { get; set; } = FileCategories.Other;

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name == value)
            {
                return;
            }

            Model.Name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>Name as the desktop shows it (shortcuts without .lnk / .url).</summary>
    public string DisplayName => !Model.IsDirectory &&
                                 Path.GetExtension(Model.Name).ToLowerInvariant() is ".lnk" or ".url"
        ? Path.GetFileNameWithoutExtension(Model.Name)
        : Model.Name;

    public string Tooltip => Exists ? $"{DisplayName}\n{Details}\n{FullPath}" : $"{DisplayName}\n找不到檔案：{FullPath}";

    public string FullPath => Model.FullPath;

    public string TypeLabel => Model.IsDirectory
        ? "資料夾"
        : Path.GetExtension(Model.FullPath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "檔案";

    /// <summary>Tile subtitle, e.g. "PNG · 10/23" (reference: EXT · date).</summary>
    public string Meta => !Exists
        ? "找不到檔案"
        : Modified == default ? TypeLabel : $"{TypeLabel} · {Modified:MM/dd}";

    /// <summary>List-mode details column.</summary>
    public string Details => !Exists
        ? "找不到檔案"
        : Model.IsDirectory
            ? $"資料夾 · {Modified:yyyy/MM/dd HH:mm}"
            : $"{FileCategories.FormatSize(Size)} · {Modified:yyyy/MM/dd HH:mm}";

    public double ItemOpacity => Exists ? 1.0 : 0.5;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadIconAsync()
    {
        Icon = await ShellHelper.GetFileIconAsync(Model.FullPath);
    }

    public static IEnumerable<FavoriteItemModel> ResolveItems(FavoriteGroupModel group)
    {
        if (!group.IsFolderBacked)
        {
            return group.Items;
        }

        if (string.IsNullOrWhiteSpace(group.MappedFolderPath) || !Directory.Exists(group.MappedFolderPath))
        {
            return Array.Empty<FavoriteItemModel>();
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(group.MappedFolderPath)
                .Where(p =>
                {
                    try
                    {
                        return (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Take(500)
                .Select(p => new FavoriteItemModel
                {
                    Name = Path.GetFileName(p),
                    FullPath = p,
                    IsDirectory = Directory.Exists(p)
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<FavoriteItemModel>();
        }
    }
}
