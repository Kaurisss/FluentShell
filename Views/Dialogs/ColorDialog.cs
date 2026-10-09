using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FluentShell.Views.Dialogs;

/// <summary>WinUI color dialog with a spectrum and RGB/hex inputs.</summary>
public sealed class ColorDialog : ContentDialog
{
    private readonly ColorPicker _picker;

    public ColorDialog(string title, Color initialColor)
    {
        Title = title;
        _picker = new ColorPicker
        {
            Color = initialColor,
            IsAlphaEnabled = false,
            IsColorSpectrumVisible = true,
            IsColorPreviewVisible = true,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            IsColorChannelTextInputVisible = true
        };
        Content = new ScrollViewer { Content = _picker, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PrimaryButtonText = "保存";
        SecondaryButtonText = "使用默认";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        CornerRadius = new CornerRadius(8);
    }

    public string SelectedHex => $"#{_picker.Color.R:X2}{_picker.Color.G:X2}{_picker.Color.B:X2}";

    public static Color Parse(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
}
