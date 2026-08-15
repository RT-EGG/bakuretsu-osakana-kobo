using System.Windows;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;

namespace BakuretsuOsakanaKobo;

public partial class ThumbnailSettingsWindow : Window
{
    public ThumbnailSettingsWindow(double intervalPercent)
    {
        ThumbnailGenerationInterval.EnsureValid(intervalPercent, nameof(intervalPercent));
        InitializeComponent();
        IntervalSlider.Value = intervalPercent;
        UpdateValueText();
        IntervalSlider.ValueChanged += IntervalSlider_OnValueChanged;
        Closed += ThumbnailSettingsWindow_OnClosed;
    }

    public double SelectedIntervalPercent => IntervalSlider.Value;

    private void IntervalSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs) => UpdateValueText();

    private void UpdateValueText() => IntervalValueText.Text = $"{IntervalSlider.Value:0.00}%";

    private void OkButton_OnClick(object sender, RoutedEventArgs eventArgs) => DialogResult = true;

    private void ThumbnailSettingsWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        IntervalSlider.ValueChanged -= IntervalSlider_OnValueChanged;
        Closed -= ThumbnailSettingsWindow_OnClosed;
    }
}
