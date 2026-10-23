using RDesk.Models;

namespace RDesk.Services;

/// <summary>Extension → category mapping shared by stacking, desktop organize and search.</summary>
public static class FileCategories
{
    public const string Folder = "資料夾";
    public const string Other = "其他";

    public static List<OrganizeRuleModel> DefaultRules() =>
    [
        Rule("圖片", ".jpg .jpeg .png .gif .bmp .webp .heic .svg .ico .tif .tiff .psd .ai"),
        Rule("文件", ".doc .docx .xls .xlsx .ppt .pptx .pdf .txt .md .rtf .odt .ods .odp .csv .epub"),
        Rule("壓縮包", ".zip .rar .7z .tar .gz .bz2 .xz .iso"),
        Rule("影音", ".mp3 .wav .flac .aac .m4a .ogg .mp4 .mkv .mov .avi .wmv .webm .srt .ass"),
        Rule("安裝程式", ".exe .msi .msix .appx .apk"),
        Rule("程式碼", ".cs .js .ts .py .java .cpp .c .h .json .xml .html .css .sql .ps1 .bat .sh")
    ];

    private static OrganizeRuleModel Rule(string name, string extensions) => new()
    {
        Name = name,
        Extensions = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList()
    };

    public static string Classify(string path, IEnumerable<OrganizeRuleModel> rules, bool isDirectory = false)
    {
        if (isDirectory)
        {
            return Folder;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return Other;
        }

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (rule.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return rule.Name;
            }
        }

        return Other;
    }

    public static string Glyph(string category) => category switch
    {
        Folder => "\uE8B7",
        "圖片" => "\uEB9F",
        "文件" => "\uE8A5",
        "壓縮包" => "\uF012",
        "影音" => "\uE8B2",
        "安裝程式" => "\uE7B8",
        "程式碼" => "\uE943",
        _ => "\uE7C3"
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };
}
