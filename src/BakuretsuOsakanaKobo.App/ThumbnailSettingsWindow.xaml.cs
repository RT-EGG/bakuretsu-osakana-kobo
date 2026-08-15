using System.Windows;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;

namespace BakuretsuOsakanaKobo;

public partial class ThumbnailSettingsWindow : Window
{
    public ThumbnailSettingsWindow(double intervalPercent, double previewWidthPercent)
    {
        ThumbnailGenerationInterval.EnsureValid(intervalPercent, nameof(intervalPercent));
        ThumbnailPreviewSize.EnsureValid(previewWidthPercent, nameof(previewWidthPercent));
        InitializeComponent();
        IntervalSlider.Value = intervalPercent;
        PreviewWidthSlider.Value = previewWidthPercent;
        UpdateIntervalValueText();
        UpdatePreviewWidthValueText();
        IntervalSlider.ValueChanged += IntervalSlider_OnValueChanged;
        PreviewWidthSlider.ValueChanged += PreviewWidthSlider_OnValueChanged;
        Closed += ThumbnailSettingsWindow_OnClosed;
    }

    public double SelectedIntervalPercent => IntervalSlider.Value;

    public double SelectedPreviewWidthPercent => PreviewWidthSlider.Value;

    private void IntervalSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs) => UpdateIntervalValueText();

    private void UpdateIntervalValueText() => IntervalValueText.Text = $"{IntervalSlider.Value:0.00}%";

    private void PreviewWidthSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs) => UpdatePreviewWidthValueText();

    private void UpdatePreviewWidthValueText() =>
        PreviewWidthValueText.Text = $"{PreviewWidthSlider.Value:0}%";

    private void OkButton_OnClick(object sender, RoutedEventArgs eventArgs) => DialogResult = true;

    private void ThumbnailSettingsWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        IntervalSlider.ValueChanged -= IntervalSlider_OnValueChanged;
        PreviewWidthSlider.ValueChanged -= PreviewWidthSlider_OnValueChanged;
        Closed -= ThumbnailSettingsWindow_OnClosed;
    }
}
