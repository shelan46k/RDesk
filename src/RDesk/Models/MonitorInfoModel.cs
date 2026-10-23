namespace RDesk.Models;

public class MonitorInfoModel
{
    public string DisplayId { get; set; } = string.Empty;
    public int Index { get; set; }
    public bool IsPrimary { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int WorkAreaX { get; set; }
    public int WorkAreaY { get; set; }
    public int WorkAreaWidth { get; set; }
    public int WorkAreaHeight { get; set; }
    public int OuterX { get; set; }
    public int OuterY { get; set; }
    public int OuterWidth { get; set; }
    public int OuterHeight { get; set; }
}
