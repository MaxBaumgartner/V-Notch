using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Weather forecast

    private WeatherService? _weatherForecast;

    private void InitializeWeather()
    {
        _weatherForecast = new WeatherService();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateIdleShape);
    }

    private WeatherForecast? _weatherCache;
    private Task<WeatherForecast?>? _weatherRequest;

    /// <summary>
    /// The forecast for the idle notch: reused while younger than <paramref name="maxAge"/>, and one
    /// request at a time. The place comes from the weather settings (empty: found from the IP address).
    /// </summary>
    private Task<WeatherForecast?> GetWeatherAsync(TimeSpan maxAge, CancellationToken token)
    {
        if (_weatherCache is { } cached && DateTime.Now - cached.FetchedAt < maxAge) return Task.FromResult<WeatherForecast?>(cached);
        if (_weatherRequest is { IsCompleted: false } running) return running;
        _weatherRequest = FetchWeatherAsync(token);
        return _weatherRequest;
    }

    private async Task<WeatherForecast?> FetchWeatherAsync(CancellationToken token)
    {
        var service = _weatherForecast;
        var result = service == null ? null : await service.GetForecastAsync(_settings?.ManualCity, token);
        if (result != null && !_cleanedUp)
        {
            _weatherCache = result;
            UpdateIdleWeather();
        }
        return result;
    }

    private void DisposeWeather()
    {
        _weatherForecast?.Dispose();
    }

    #endregion
}
