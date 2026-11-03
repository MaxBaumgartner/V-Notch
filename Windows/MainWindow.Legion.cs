using System.Windows.Input;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    #region Legion tab

    // Two rows of tiles under their headers.
    private const double LegionViewWidth = 560;
    private const double LegionViewHeight = 300;

    private bool _isLegionView;

    private void InitializeLegion() => LegionCenter.AttachLegion();

    private void DisposeLegion() => LegionCenter.Dispose();

    private void LegionIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isLegionView) NavigateToNotchView(NotchView.Legion);
    }

    #endregion
}
