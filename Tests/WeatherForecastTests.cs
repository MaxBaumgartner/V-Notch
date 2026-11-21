using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WeatherForecastTests
{
    [Fact]
    public void OpenMeteo_ParsesNowHoursDaysAndQuarters()
    {
        const string json = """
        {
          "current": { "temperature_2m": 6.9, "apparent_temperature": 4.2, "relative_humidity_2m": 68, "is_day": 1,
                       "weather_code": 3, "wind_speed_10m": 9.4, "wind_direction_10m": 76, "wind_gusts_10m": 21.6, "uv_index": 0.4 },
          "hourly": {
            "time": ["2026-11-02T16:00", "2026-11-02T17:00", "2026-11-02T18:00"],
            "temperature_2m": [6.9, 6.1, null],
            "precipitation_probability": [5, 40, 80],
            "precipitation": [0, 0.2, 1.1],
            "weather_code": [3, 61, 63],
            "is_day": [1, 0, 0]
          },
          "daily": {
            "time": ["2026-11-02", "2026-11-03"],
            "weather_code": [61, 3],
            "temperature_2m_max": [7.4, 7.0],
            "temperature_2m_min": [3.8, 3.9],
            "precipitation_probability_max": [80, null],
            "precipitation_sum": [1.3, 0],
            "uv_index_max": [1.2, 0.9]
          },
          "minutely_15": { "time": ["2026-11-02T16:15", "2026-11-02T16:30"], "precipitation": [0, 0.3] }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var forecast = WeatherService.ParseOpenMeteo(doc.RootElement, "Linz", 48.31, 14.29, new DateTime(2026, 11, 2, 16, 21, 0));

        Assert.NotNull(forecast);
        Assert.Equal("Open-Meteo", forecast!.Source);
        Assert.Equal(6.9, forecast.Now.Temperature);
        Assert.Equal(4.2, forecast.Now.FeelsLike);
        Assert.Equal(21.6, forecast.Now.GustKmh);
        Assert.True(forecast.Now.IsDay);
        // The step without a temperature is left out.
        Assert.Equal(2, forecast.Hours.Count);
        Assert.Equal(new DateTime(2026, 11, 2, 17, 0, 0), forecast.Hours[1].Time);
        Assert.Equal(40, forecast.Hours[1].PrecipitationChance);
        Assert.Equal(61, forecast.Hours[1].Code);
        Assert.Equal(2, forecast.Days.Count);
        Assert.Equal(80, forecast.Days[0].PrecipitationChance);
        Assert.Equal(-1, forecast.Days[1].PrecipitationChance);
        Assert.Equal(2, forecast.Quarters.Count);
        Assert.Equal(0.3, forecast.Quarters[1].Precipitation);
    }

    [Fact]
    public void OpenMeteo_WithoutCurrentConditions_IsNoForecast()
    {
        using var doc = JsonDocument.Parse("""{ "hourly": { "time": [] }, "daily": { "time": [] } }""");
        Assert.Null(WeatherService.ParseOpenMeteo(doc.RootElement, "", 0, 0, DateTime.Now));
    }

    [Fact]
    public void MetNorway_ConvertsUnitsSymbolsAndBuildsDays()
    {
        const string json = """
        {
          "properties": { "timeseries": [
            { "time": "2026-11-02T15:00:00Z", "data": {
                "instant": { "details": { "air_temperature": 6.9, "apparent_air_temperature": 5.1, "relative_humidity": 68.6,
                                          "wind_speed": 2.5, "wind_from_direction": 76.1, "ultraviolet_index_clear_sky": 0.1 } },
                "next_1_hours": { "summary": { "symbol_code": "cloudy" }, "details": { "precipitation_amount": 0 } },
                "next_6_hours": { "summary": { "symbol_code": "lightrain" }, "details": { "air_temperature_max": 7.4, "air_temperature_min": 4.6, "precipitation_amount": 0.6 } } } },
            { "time": "2026-11-02T16:00:00Z", "data": {
                "instant": { "details": { "air_temperature": 6.2, "relative_humidity": 72, "wind_speed": 2.0, "wind_from_direction": 80 } },
                "next_1_hours": { "summary": { "symbol_code": "lightrainshowers_night" }, "details": { "precipitation_amount": 0.4 } } } },
            { "time": "2026-11-05T06:00:00Z", "data": {
                "instant": { "details": { "air_temperature": 1.2, "relative_humidity": 90, "wind_speed": 5.6, "wind_from_direction": 101 } },
                "next_6_hours": { "summary": { "symbol_code": "clearsky_day" }, "details": { "air_temperature_max": 8.1, "air_temperature_min": 1.0, "precipitation_amount": 0 } } } },
            { "time": "2026-11-12T00:00:00Z", "data": { "instant": { "details": { "air_temperature": 0.5 } } } }
          ] }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var forecast = WeatherService.ParseMetNorway(doc.RootElement, "Linz", 48.31, 14.29, DateTime.Now);

        Assert.NotNull(forecast);
        Assert.Equal("MET Norway", forecast!.Source);
        Assert.Equal(9, forecast.Now.WindKmh, 3);       // 2.5 m/s
        Assert.Equal(5.1, forecast.Now.FeelsLike);
        Assert.Null(forecast.Now.GustKmh);
        // The last step has no period after it and is left out.
        Assert.Equal(3, forecast.Hours.Count);
        Assert.Equal(80, forecast.Hours[1].Code);
        Assert.False(forecast.Hours[1].IsDay);
        Assert.Equal(-1, forecast.Hours[1].PrecipitationChance);
        // Six-hour amounts are spread per hour.
        Assert.Equal(0, forecast.Hours[2].Precipitation);
        var first = forecast.Days[0];
        Assert.Equal(DateTime.SpecifyKind(new DateTime(2026, 11, 2, 15, 0, 0), DateTimeKind.Utc).ToLocalTime().Date, first.Date);
        Assert.Equal(7.4, first.High);
        Assert.Equal(4.6, first.Low);
        Assert.Equal(80, first.Code);  // the roughest weather of the day
        Assert.Equal(8.1, forecast.Days.Last().High);
    }

    [Theory]
    [InlineData("clearsky_day", 0, true)]
    [InlineData("fair_night", 1, false)]
    [InlineData("partlycloudy_polartwilight", 2, true)]
    [InlineData("cloudy", 3, null)]
    [InlineData("fog", 45, null)]
    [InlineData("heavyrain", 65, null)]
    [InlineData("rainshowersandthunder_day", 95, true)]
    [InlineData("lightsleet", 66, null)]
    [InlineData("heavysnowshowers_night", 86, false)]
    [InlineData("", 3, null)]
    public void MetSymbols_MapToWmoCodes(string symbol, int code, bool? isDay)
    {
        Assert.Equal(code, WeatherService.MetSymbolToWmo(symbol, out bool? day));
        Assert.Equal(isDay, day);
    }

    [Fact]
    public void SunTimes_MatchLinzAtMidsummerAndInNovember()
    {
        var june = SunTimes.For(new DateTime(2026, 6, 21), 48.306, 14.286)!.Value;
        AssertNear(new DateTime(2026, 6, 21, 3, 1, 0, DateTimeKind.Utc), june.Rise.ToUniversalTime());
        AssertNear(new DateTime(2026, 6, 21, 19, 7, 0, DateTimeKind.Utc), june.Set.ToUniversalTime());

        var november = SunTimes.For(new DateTime(2026, 11, 2), 48.306, 14.286)!.Value;
        AssertNear(new DateTime(2026, 11, 2, 15, 43, 0, DateTimeKind.Utc), november.Set.ToUniversalTime());

        // Svalbard in midwinter: the sun stays down.
        Assert.Null(SunTimes.For(new DateTime(2026, 12, 21), 78.2, 15.6));
    }

    private static void AssertNear(DateTime expectedUtc, DateTime actualUtc) =>
        Assert.True(Math.Abs((expectedUtc - actualUtc).TotalMinutes) < 4, $"expected {expectedUtc:HH:mm}, got {actualUtc:HH:mm}");

}
