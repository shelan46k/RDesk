using System.Net.Http.Json;
using System.Text.Json;
using RDesk.Models;

namespace RDesk.Services;

public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<WeatherSnapshot?> FetchAsync(double lat, double lon, string locationName, CancellationToken ct = default)
    {
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=3";

        using var doc = await Http.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null)
        {
            return null;
        }

        var root = doc.RootElement;
        var current = root.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var (desc, emoji) = Describe(code);

        var days = new List<DailyForecast>();
        var daily = root.GetProperty("daily");
        var times = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var maxes = daily.GetProperty("temperature_2m_max");
        var mins = daily.GetProperty("temperature_2m_min");
        var labels = new[] { "今天", "明天", "後天" };
        for (var i = 0; i < Math.Min(3, times.GetArrayLength()); i++)
        {
            var dCode = codes[i].GetInt32();
            var (_, dEmoji) = Describe(dCode);
            days.Add(new DailyForecast
            {
                Label = labels[i],
                Emoji = dEmoji,
                Min = mins[i].GetDouble(),
                Max = maxes[i].GetDouble()
            });
        }

        return new WeatherSnapshot
        {
            LocationName = locationName,
            Temperature = current.GetProperty("temperature_2m").GetDouble(),
            ApparentTemperature = current.GetProperty("apparent_temperature").GetDouble(),
            Humidity = current.GetProperty("relative_humidity_2m").GetInt32(),
            WindSpeed = current.GetProperty("wind_speed_10m").GetDouble(),
            WeatherCode = code,
            Description = desc,
            Emoji = emoji,
            Days = days
        };
    }

    /// <summary>
    /// Open-Meteo's geocoder only matches romanized names ("Taichung"), so CJK queries ("台中")
    /// go to OpenStreetMap Nominatim first. Results from both are merged and de-duplicated.
    /// </summary>
    public async Task<IReadOnlyList<GeoResult>> SearchCityAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Array.Empty<GeoResult>();
        }

        var hasCjk = name.Any(ch => ch is >= '\u3400' and <= '\u9FFF');
        var merged = new List<GeoResult>();

        async Task AddFrom(Func<Task<IReadOnlyList<GeoResult>>> source)
        {
            try
            {
                foreach (var r in await source())
                {
                    if (merged.All(m => m.DisplayName != r.DisplayName))
                    {
                        merged.Add(r);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                StartupLog.Write("City search source failed", ex);
            }
        }

        if (hasCjk)
        {
            await AddFrom(() => SearchNominatimAsync(name, ct));
        }

        await AddFrom(() => SearchOpenMeteoAsync(name, ct));
        if (!hasCjk && merged.Count == 0)
        {
            await AddFrom(() => SearchNominatimAsync(name, ct));
        }

        return merged.Take(8).ToList();
    }

    private static async Task<IReadOnlyList<GeoResult>> SearchNominatimAsync(string name, CancellationToken ct)
    {
        var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&addressdetails=1" +
                  $"&accept-language=zh-TW&q={Uri.EscapeDataString(name)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RDesk/1.0 (Windows desktop widget)");
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        string? Field(JsonElement address, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (address.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s)
                {
                    return s;
                }
            }

            return null;
        }

        var list = new List<GeoResult>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("address", out var address))
            {
                continue;
            }

            var city = Field(address, "city", "county", "state", "province", "region");
            var district = Field(address, "city_district", "district", "suburb", "town", "village", "quarter");
            var country = Field(address, "country");
            var parts = new[] { city, district != city ? district : null, country }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToArray();
            if (parts.Length == 0)
            {
                continue;
            }

            list.Add(new GeoResult
            {
                Name = city ?? district ?? name,
                DisplayName = string.Join(" · ", parts),
                Latitude = double.Parse(item.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                Longitude = double.Parse(item.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        return list;
    }

    private static async Task<IReadOnlyList<GeoResult>> SearchOpenMeteoAsync(string name, CancellationToken ct)
    {
        var url =
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=5&language=zh&format=json";
        using var doc = await Http.GetFromJsonAsync<JsonDocument>(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var results))
        {
            return Array.Empty<GeoResult>();
        }

        var list = new List<GeoResult>();
        foreach (var item in results.EnumerateArray())
        {
            var n = item.GetProperty("name").GetString() ?? name;
            var admin = item.TryGetProperty("admin1", out var a) ? a.GetString() : null;
            var country = item.TryGetProperty("country", out var c) ? c.GetString() : null;
            var display = string.Join(" · ", new[] { n, admin, country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            list.Add(new GeoResult
            {
                Name = n,
                DisplayName = display,
                Latitude = item.GetProperty("latitude").GetDouble(),
                Longitude = item.GetProperty("longitude").GetDouble()
            });
        }

        return list;
    }

    public static (string Description, string Emoji) Describe(int code) => code switch
    {
        0 => ("晴朗", "☀️"),
        1 or 2 => ("多雲", "⛅"),
        3 => ("陰天", "☁️"),
        45 or 48 => ("霧", "🌫️"),
        51 or 53 or 55 => ("毛毛雨", "🌦️"),
        61 or 63 or 65 => ("雨", "🌧️"),
        71 or 73 or 75 => ("雪", "🌨️"),
        80 or 81 or 82 => ("陣雨", "🌧️"),
        95 or 96 or 99 => ("雷雨", "⛈️"),
        _ => ("多雲", "☁️")
    };
}
