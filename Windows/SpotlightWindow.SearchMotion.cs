using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly Controls.SpotlightSearchActivityMotion _searchMotion;
    private bool _searchInputPending;

    private void OnSearchMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(OnSearchMotionPreferenceChanged);
            return;
        }
        if (AnimationConfig.ReduceMotion)
        {
            ResetAiPromptReveal();
            Motion.Stop(StatusPanel, OpacityProperty, 1);
        }
        _searchMotion.Refresh();
    }
}
