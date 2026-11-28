using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;

namespace VNotch.Services;

internal static partial class LocalizedPresentation
{
    private const string NativeDll = "VNotch.Native.dll";
    private static bool _initialized;

    [LibraryImport(NativeDll, EntryPoint = "vn_localized_presentation_resolve_flow_direction")]
    private static partial int NativeResolveFlowDirection(byte isRightToLeft, out int outFlowDirection);

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
        Loc.LanguageChanged += RefreshWindows;
    }

    private static void RefreshWindows()
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshWindows));
            return;
        }
        foreach (Window window in app.Windows) Apply(window);
    }

    internal static void Apply(FrameworkElement element)
    {
        var culture = Loc.GetCulture();
        element.SetCurrentValue(FrameworkElement.LanguageProperty, XmlLanguage.GetLanguage(culture.IetfLanguageTag));
        NativeResolveFlowDirection(culture.TextInfo.IsRightToLeft ? (byte)1 : (byte)0, out int flowDir);
        element.SetCurrentValue(FrameworkElement.FlowDirectionProperty, (FlowDirection)flowDir);
    }

    internal static string FormatTransferRate(double bytesPerSecond, bool roundKilobytes = false)
    {
        var culture = Loc.GetCulture();
        return bytesPerSecond switch
        {
            >= 1024 * 1024 => string.Create(culture, $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"),
            >= 1024 when roundKilobytes => string.Create(culture, $"{bytesPerSecond / 1024:0} KB/s"),
            >= 1024 => string.Create(culture, $"{bytesPerSecond / 1024:0.0} KB/s"),
            _ => string.Create(culture, $"{bytesPerSecond:0} B/s")
        };
    }

    internal static string FormatWatts(double watts) =>
        watts.ToString(watts >= 10 ? "0" : "0.0", Loc.GetCulture()) + " W";
}
