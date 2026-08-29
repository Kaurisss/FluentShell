using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentShell.Services;

/// <summary>
/// 使用 WinUI 3 支持的系统文件选择器选择本机私钥文件。
/// </summary>
internal static class PrivateKeyFilePicker
{
    public static async Task<string?> PickAsync(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("文件选择器需要有效的宿主窗口句柄。", nameof(windowHandle));

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".pem");
        picker.FileTypeFilter.Add(".key");
        picker.FileTypeFilter.Add(".ppk");
        picker.FileTypeFilter.Add("*");

        // WinUI 3 desktop applications do not have package-based picker activation;
        // bind the picker to the real top-level window before showing it.
        InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
