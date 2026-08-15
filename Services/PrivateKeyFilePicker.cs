using System.Runtime.InteropServices;
using System.Text;

namespace FluentShell.Services;

/// <summary>
/// 使用 Windows 通用文件对话框，以便在存在时把 .ssh 目录作为私钥选择的默认位置。
/// </summary>
internal static class PrivateKeyFilePicker
{
    private const int MaxPathLength = 32_768;
    private const int OfnNoChangeDirectory = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const string Filter =
        "OpenSSH 私钥（*.pem;*.key;无扩展名）\0*.pem;*.key;*.\0" +
        "PuTTY 私钥（*.ppk，需转换）\0*.ppk\0" +
        "所有文件（*.*）\0*.*\0\0";

    public static string? Pick(IntPtr windowHandle)
    {
        // FileOpenPicker.SuggestedStartLocation 只能接受 PickerLocationId，无法指向任意的 .ssh 路径。
        // 此处保留 Windows 公共文件对话框，才能满足“优先从 .ssh 打开”的交互要求。
        var sshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
        var initialDirectory = Directory.Exists(sshDirectory)
            ? sshDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new OpenFileName
        {
            Owner = windowHandle,
            Filter = Filter,
            FileName = new StringBuilder(MaxPathLength),
            MaxFileName = MaxPathLength,
            FilterIndex = 1,
            InitialDirectory = initialDirectory,
            Title = "选择 OpenSSH 私钥文件",
            Flags = OfnExplorer | OfnNoChangeDirectory | OfnPathMustExist | OfnFileMustExist
        };

        return GetOpenFileName(dialog)
            ? dialog.FileName.ToString().TrimEnd('\0')
            : null;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        public int StructSize = Marshal.SizeOf<OpenFileName>();
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public StringBuilder FileName = new();
        public int MaxFileName;
        public StringBuilder? FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int ReservedValue;
        public int FlagsEx;
    }
}
