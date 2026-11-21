using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private SettingsWindow? _preparedSettingsWindow;
    private SettingsWindow? _activeSettingsWindow;
    private NotchSettings? _preparedSettingsSnapshot;
    private bool _firstOpenUiPrepared;
    private bool _settingsPreparationScheduled;
    private int _settingsPreparationGeneration;
    private UiScheduledTimer? _settingsPreparationExpiry;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new MainWindowAutomationPeer(this);

    private sealed class MainWindowAutomationPeer(MainWindow owner)
        : System.Windows.Automation.Peers.WindowAutomationPeer(owner)
    {
        protected override System.Collections.Generic.List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }

    private SettingsWindow CreateSettingsWindow(bool prepareForFirstShow = false) => new(
        _settings, _settingsService, _spotlightController.IsHotkeyRegistered,
        _updateService, prepareForFirstShow,
        clipboardHotkeyError: () => ClipboardHotkeyRegistrationError,
        invisibleHotkeyRegistered: () => IsInvisibleHotkeyRegistered,
        appNotificationState: ReadAppNotificationState)
    { Owner = this };

    private void PrepareFirstOpenUi()
    {
        if (_firstOpenUiPrepared || _cleanedUp) return;
        _firstOpenUiPrepared = true;
        PrepareExpandedContentForFirstOpen();
        PrepareSecondaryContentForFirstOpen();
    }

    private void ScheduleSettingsFirstOpenPreparation()
    {
        if (_cleanedUp || _activeSettingsWindow != null) return;
        if (_settingsPreparationExpiry != null)
        {
            _settingsPreparationExpiry.Stop();
            _settingsPreparationExpiry.Start();
            return;
        }
        if (_settingsPreparationScheduled) return;
        _settingsPreparationScheduled = true;
        int generation = _settingsPreparationGeneration;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            PrepareSettingsFirstOpenAsync(generation).SafeFireAndForget("SETTINGS-PREPARE")));
    }

    private async Task PrepareSettingsFirstOpenAsync(int generation)
    {
        if (!await WaitForSettingsPreparationIdleAsync(generation)) return;
        try
        {
            // Both stages run after the notch's entrance. Yield between XAML
            // construction and native template realization so input can run.
            _preparedSettingsSnapshot = _settings.Clone();
            var prepared = CreateSettingsWindow(prepareForFirstShow: true);
            _preparedSettingsWindow = prepared;
            _settingsPreparationExpiry = new UiScheduledTimer(TimeSpan.FromSeconds(30),
                System.Windows.Threading.DispatcherPriority.Background, (_, _) => DetachPreparedSettingsWindow()?.Close(), Dispatcher);
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (!await WaitForSettingsPreparationIdleAsync(generation)) return;
            await prepared.PrepareForFirstShowAsync();
        }
        catch (Exception ex)
        {
            if (generation == _settingsPreparationGeneration) DetachPreparedSettingsWindow()?.Close();
            RuntimeLog.Warn("FIRST-OPEN", $"Settings preparation skipped: {ex.GetType().Name}");
        }
    }

    private async Task<bool> WaitForSettingsPreparationIdleAsync(int generation)
    {
        // Dispatcher idle can fall between frames of the notch entrance or an expansion.
        while (!_cleanedUp && generation == _settingsPreparationGeneration)
        {
            if (_postAppearInitializationStarted && !_isAnimating) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private void PrepareSecondaryContentForFirstOpen()
    {
        if (SecondaryContent == null) return;
        var visibility = SecondaryContent.Visibility;
        double opacity = SecondaryContent.Opacity;
        double targetWidth = Math.Max(0, ClipboardTrayWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right);
        double targetHeight = Math.Max(0, ClipboardTrayHeight - SecondaryContent.Margin.Top - SecondaryContent.Margin.Bottom);
        try
        {
            SecondaryContent.Opacity = 0;
            SecondaryContent.Visibility = Visibility.Visible;
            SecondaryContent.Width = targetWidth;
            SecondaryContent.Height = targetHeight;
            SecondaryContent.Measure(new Size(targetWidth, targetHeight));
            SecondaryContent.Arrange(new Rect(0, 0, targetWidth, targetHeight));
        }
        finally
        {
            SecondaryContent.Visibility = visibility;
            SecondaryContent.Opacity = opacity;
        }
    }

    private void PrepareExpandedContentForFirstOpen()
    {
        var visibility = ExpandedContent.Visibility;
        double opacity = ExpandedContent.Opacity;
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        if (MediaRightColumn != null) MediaRightColumn.Visibility = IsNoWidgetMode && !_isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        double targetWidth = Math.Max(0, _expandedWidth - ExpandedContentHorizontalInset);
        double targetHeight = Math.Max(0, _expandedHeight - ExpandedContentVerticalInset);
        try
        {
            ExpandedContent.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.Width = targetWidth;
            ExpandedContent.Height = targetHeight;
            ExpandedContent.Measure(new Size(targetWidth, targetHeight));
            ExpandedContent.Arrange(new Rect(0, 0, targetWidth, targetHeight));
            PrepareExpandedContentLayoutForReveal();
            EnsureCachedThumbnailExpandTarget();
        }
        finally
        {
            ExpandedContent.Visibility = visibility;
            ExpandedContent.Opacity = opacity;
        }
    }

    private SettingsWindow TakeSettingsWindow()
    {
        var snapshot = _preparedSettingsSnapshot;
        var prepared = DetachPreparedSettingsWindow();
        if (prepared == null) return CreateSettingsWindow();

        prepared.ActivatePreparedWindow(
            _settings,
            snapshotMatches: snapshot?.ValueEquals(_settings) == true,
            _spotlightController.IsHotkeyRegistered);
        return prepared;
    }

    private SettingsWindow? DetachPreparedSettingsWindow()
    {
        // Opening Settings while the hidden warm-up yields takes ownership of the window.
        _settingsPreparationGeneration++;
        _settingsPreparationScheduled = false;
        _settingsPreparationExpiry?.Dispose();
        _settingsPreparationExpiry = null;
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        _preparedSettingsSnapshot = null;
        return prepared;
    }

    private void DiscardPreparedSettingsWindow()
    {
        DetachPreparedSettingsWindow()?.Close();
        var active = _activeSettingsWindow;
        _activeSettingsWindow = null;
        active?.Close();
    }
}
