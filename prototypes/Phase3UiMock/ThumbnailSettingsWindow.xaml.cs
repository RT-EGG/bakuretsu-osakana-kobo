using System.Windows;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

public partial class ThumbnailSettingsWindow : Window
{
    public ThumbnailSettingsWindow(double intervalPercent)
    {
        InitializeComponent();
        IntervalSlider.Value = intervalPercent;
        UpdateValueText();
        IntervalSlider.ValueChanged += IntervalSlider_OnValueChanged;
        Closed += ThumbnailSettingsWindow_OnClosed;
    }

    public double SelectedIntervalPercent => IntervalSlider.Value;

    private void IntervalSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueText();

    private void UpdateValueText() => IntervalValueText.Text = $"{IntervalSlider.Value:0.00}%";

    private void OkButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void ThumbnailSettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        IntervalSlider.ValueChanged -= IntervalSlider_OnValueChanged;
        Closed -= ThumbnailSettingsWindow_OnClosed;
    }
}
