using System.Windows;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class MainWindow
{
    private bool _powerDetails;
    private PowerDetailView? _powerDetail;
    private ScrollViewer? _powerDetailScroll;
    private string _powerDetailKey = "";
    internal bool ShowingPowerDetails => IsMonitorPage && _powerDetails;

    private void OpenPowerDetails()
    {
        if (!IsMonitorPage) return;
        _monitorView?.CloseCard();
        _powerDetails = true;
        RenderMonitor(); UpdateMonitorDemand();
        _powerDetail?.BackButton.Focus();
    }

    private void ClosePowerDetails()
    {
        if (!_powerDetails || !IsMonitorPage) return;
        _powerDetails = false; _powerDetail?.SetActive(false);
        RenderMonitor(); UpdateMonitorDemand();
        _monitorView?.FocusPowerLink();
    }

    private void RenderPowerDetails()
    {
        string key = string.Join('|', PanelTheme.Generation, Loc.Language, Settings.MonitorPalette, Settings.MonitorCustomColors.Key,
            Settings.MonitorTemperatures, Settings.MonitorFans);
        if (_powerDetail is null || key != _powerDetailKey)
        {
            _powerDetail?.SetActive(false);
            _powerDetailKey = key;
            _powerDetail = new PowerDetailView(MonitorThemeColors.For(Settings), ClosePowerDetails, Settings.MonitorTemperatures, Settings.MonitorFans);
            _powerDetail.BackButton.Style = (Style)FindResource("PanelSourceButton");
            _powerDetailScroll = new ScrollViewer
            {
                Content = _powerDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false
            };
            // A wheel on this nested page scrolls its content rather than changing top-level pages at the end of the scroll.
            _powerDetailScroll.PreviewMouseWheel += (_, e) =>
            {
                if (_powerDetailScroll.ScrollableHeight <= 0) return;
                _powerDetailScroll.ScrollToVerticalOffset(_powerDetailScroll.VerticalOffset - e.Delta / 3.0); e.Handled = true;
            };
        }
        if (!MonitorContent.Children.Contains(_powerDetailScroll!))
        {
            MonitorContent.Children.Clear(); MonitorContent.Children.Add(_powerDetailScroll!);
            _powerDetailScroll!.ScrollToTop();
        }
        // Very short panels scroll; values and controls retain a readable size instead of being cropped or scaled to fit.
        _powerDetail.MinHeight = Math.Max(PowerDetailView.MinimumHeight, ContentCanvas.Height - _header.Row - MonitorBottom(ContentCanvas.Height));
        UpdatePowerDetails(_app.Monitor?.Latest ?? WaitingSnapshot);
    }

    private void UpdatePowerDetails(MonitorSnapshot snapshot)
    {
        if (_powerDetail is null) return;
        _powerDetail.Update(snapshot, BatteryProvider.MainsConnection);
        _powerDetail.SetActive(_expanded && ShowingPowerDetails && IsVisible && !IsSuppressed && !DisplayPower.Off && MonitorMotion);
    }
}
