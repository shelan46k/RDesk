namespace RDesk.Models;

public sealed class WeatherSnapshot
{
    public string LocationName { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public double ApparentTemperature { get; set; }
    public int Humidity { get; set; }
    public double WindSpeed { get; set; }
    public int WeatherCode { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Emoji { get; set; } = "☁️";
    public List<DailyForecast> Days { get; set; } = new();
}

public sealed class DailyForecast
{
    public string Label { get; set; } = string.Empty;
    public string Emoji { get; set; } = "☁️";
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class GeoResult
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
