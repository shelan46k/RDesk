namespace RDesk.Models;

public class AppConfig
{
    public string TargetMonitorDeviceId { get; set; } = string.Empty;
    /// <summary>-1 = use primary monitor.</summary>
    public int TargetMonitorIndex { get; set; } = -1;
    public string DockSide { get; set; } = "Right";
    public int SidebarWidth { get; set; } = 380;
    /// <summary>Light / Dark / System.</summary>
    public string ThemeMode { get; set; } = "Light";
    /// <summary>Legacy opacity (1 = opaque). Kept in sync with <see cref="PanelTransparency"/>.</summary>
    public double PanelOpacity { get; set; } = 0.85;
    /// <summary>Sidebar / search chrome. 0 = opaque, 1 = fully transparent.</summary>
    public double PanelTransparency { get; set; } = 0.15;
    /// <summary>Cards and detached desktop boxes. 0 = opaque, 1 = fully transparent.</summary>
    public double CardTransparency { get; set; } = 0.47;
    /// <summary>True after converting <see cref="PanelOpacity"/> into <see cref="PanelTransparency"/>.</summary>
    public bool TransparencyMigrated { get; set; }
    /// <summary>Blue / Purple / Green / Orange / Pink.</summary>
    public string AccentColor { get; set; } = "Blue";
    /// <summary>Acrylic / Mica / Solid.</summary>
    public string Backdrop { get; set; } = "Acrylic";
    public bool PinToDesktop { get; set; } = true;
    public bool RunOnStartup { get; set; } = false;
    public const string DefaultUpdateGithubUrl = "https://github.com/shelan46k/RDesk";

    /// <summary>Public GitHub repo used by Velopack. Empty disables auto-update.</summary>
    public string UpdateGithubUrl { get; set; } = DefaultUpdateGithubUrl;
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>Global hotkey to reveal / toggle the sidebar (DeskBox-style).</summary>
    public bool EnableRevealHotkey { get; set; } = true;
    public string RevealHotkey { get; set; } = "F7";
    public bool EnableSearchHotkey { get; set; } = true;
    public string SearchHotkey { get; set; } = "Ctrl+Alt+F";
    /// <summary>Bring every desktop file box in front of other windows.</summary>
    public bool EnableBoxesHotkey { get; set; } = true;
    public string BoxesHotkey { get; set; } = "Ctrl+Alt+B";

    public bool ShowWeatherCard { get; set; } = true;
    public bool ShowRecentCard { get; set; } = true;
    public bool ShowFavoritesCard { get; set; } = true;
    public bool ShowTodoCard { get; set; } = true;
    public bool ShowMusicCard { get; set; } = true;
    /// <summary>Folder whose audio files become the default queue. Empty until the user picks one.</summary>
    public string MusicFolder { get; set; } = string.Empty;
    /// <summary>0 = mute, 1 = full.</summary>
    public double MusicVolume { get; set; } = 1.0;
    /// <summary>__folder__ uses MusicFolder; otherwise a custom playlist id.</summary>
    public string MusicPlaylistId { get; set; } = "__folder__";
    public int MusicIndex { get; set; }
    /// <summary>None / All / One.</summary>
    public string MusicRepeatMode { get; set; } = "All";
    /// <summary>Saved play order for the folder queue. Empty = alphabetical. New files append at the end.</summary>
    public List<string> MusicFolderOrder { get; set; } = new();
    public List<MusicPlaylistModel> MusicPlaylists { get; set; } = new();
    public bool ShowSystemCard { get; set; } = true;
    public bool ShowNetworkCard { get; set; } = true;
    public bool ShowNotesTab { get; set; } = true;
    public bool ShowClipboardTab { get; set; } = true;
    public bool ShowSidebarTip { get; set; } = true;

    public string LocationName { get; set; } = "台中市 · 南區";
    public double Latitude { get; set; } = 24.1235;
    public double Longitude { get; set; } = 120.6750;

    public List<string> PinnedRecentPaths { get; set; } = new();
    public List<FavoriteGroupModel> FavoriteGroups { get; set; } = new();

    /// <summary>Root folder for storage boxes (收納格子). Empty = %UserProfile%\RDesk 收納.</summary>
    public string StorageRoot { get; set; } = string.Empty;
    /// <summary>Dropping files into a folder-backed box moves them instead of copying.</summary>
    public bool DropMovesFiles { get; set; } = false;

    public bool AutoOrganizeEnabled { get; set; } = false;
    public List<OrganizeRuleModel> OrganizeRules { get; set; } = new();

    public List<TodoItemModel> TodoItems { get; set; } = new();
    public string TodoFilter { get; set; } = "Active";

    public List<NoteItemModel> Notes { get; set; } = new();
    /// <summary>Legacy single quick note; migrated into <see cref="Notes"/> on load.</summary>
    public string QuickNoteContent { get; set; } = string.Empty;
    public bool ClipboardHistoryEnabled { get; set; } = true;
    public List<ClipItemModel> ClipboardHistory { get; set; } = new();

    public List<string> SearchHistory { get; set; } = new();
    public DateTime? LastAutoSnapshot { get; set; }
}

public class FavoriteGroupModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Favorites";
    /// <summary>Shows the live contents of an existing folder (映射資料夾).</summary>
    public bool IsFolderMapping { get; set; } = false;
    /// <summary>Owns a real folder under the storage root; drops copy / move files into it (收納格子).</summary>
    public bool IsStorageBox { get; set; } = false;
    /// <summary>Backing folder for mapping and storage boxes.</summary>
    public string? MappedFolderPath { get; set; }
    /// <summary>Favorites box only: folder holding desktop items that were moved into the box.</summary>
    public string? StoreFolder { get; set; }
    public List<FavoriteItemModel> Items { get; set; } = new();

    /// <summary>Icon / List.</summary>
    public string ViewMode { get; set; } = "Icon";
    /// <summary>Custom / Name / Type / Size / Date.</summary>
    public string SortBy { get; set; } = "Custom";
    public bool SortDescending { get; set; } = false;
    /// <summary>Show category chips (圖片 / 文件 / 壓縮包 …) to stack files by type.</summary>
    public bool StackByType { get; set; } = false;

    public bool IsDetachedToDesktop { get; set; } = false;
    public bool IsCollapsedToCapsule { get; set; } = false;
    public int DetachedX { get; set; } = 200;
    public int DetachedY { get; set; } = 200;
    public int DetachedWidth { get; set; } = 320;
    public int DetachedHeight { get; set; } = 260;

    public bool IsFolderBacked => IsFolderMapping || IsStorageBox;
}

public class FavoriteItemModel
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; } = false;
    /// <summary>Where the item lived before it was tidied into a box (e.g. the desktop); used by 放回桌面.</summary>
    public string? OriginalPath { get; set; }
}

public class TodoItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public bool IsCompleted { get; set; } = false;
    public bool IsImportant { get; set; } = false;
    /// <summary>Empty / Red / Orange / Yellow / Green / Blue / Purple.</summary>
    public string Color { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public bool RemindEnabled { get; set; } = false;
    /// <summary>Minutes before <see cref="DueAt"/> to fire the reminder.</summary>
    public int RemindLeadMinutes { get; set; } = 0;
    public DateTime? SnoozeUntil { get; set; }
    public bool Reminded { get; set; } = false;
    /// <summary>None / Daily / Weekdays / Weekly / Monthly.</summary>
    public string Repeat { get; set; } = "None";
    public List<string> Attachments { get; set; } = new();

    public DateTime? NextReminderAt =>
        !RemindEnabled || DueAt is null || IsCompleted
            ? null
            : SnoozeUntil ?? DueAt.Value.AddMinutes(-RemindLeadMinutes);
}

public class NoteItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public bool IsPinned { get; set; } = false;
    public List<string> Attachments { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class ClipItemModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Text / Files.</summary>
    public string Kind { get; set; } = "Text";
    public string Text { get; set; } = string.Empty;
    public List<string> Paths { get; set; } = new();
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}

public class MusicPlaylistModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "播放清單";
    public List<string> Paths { get; set; } = new();
}

public class OrganizeRuleModel
{
    public string Name { get; set; } = string.Empty;
    public List<string> Extensions { get; set; } = new();
    public bool Enabled { get; set; } = true;
}
