using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FluentShell.Models;

public enum AuthenticationMethod
{
    Password,
    PrivateKey
}

public sealed class ServerProfile : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private AuthenticationMethod _authentication = AuthenticationMethod.Password;
    private string _privateKeyPath = string.Empty;
    private string _notes = string.Empty;
    private string _hostFingerprint = string.Empty;
    private DateTimeOffset? _lastConnectedAt;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Host { get => _host; set => SetField(ref _host, value); }
    public int Port { get => _port; set => SetField(ref _port, value); }
    public string Username { get => _username; set => SetField(ref _username, value); }
    public AuthenticationMethod Authentication { get => _authentication; set => SetField(ref _authentication, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => SetField(ref _privateKeyPath, value); }
    public string Notes { get => _notes; set => SetField(ref _notes, value); }
    public string HostFingerprint { get => _hostFingerprint; set => SetField(ref _hostFingerprint, value); }
    public DateTimeOffset? LastConnectedAt { get => _lastConnectedAt; set => SetField(ref _lastConnectedAt, value); }

    public string Address => $"{Host}:{Port}";
    public string AuthenticationLabel => Authentication == AuthenticationMethod.Password ? "密码" : "私钥";
    public string LastConnectedLabel => LastConnectedAt is null ? "尚未连接" : $"上次连接 {LastConnectedAt:MM-dd HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Host) or nameof(Port)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Address)));
        if (propertyName is nameof(Authentication)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AuthenticationLabel)));
        if (propertyName is nameof(LastConnectedAt)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastConnectedLabel)));
    }
}

public sealed class AppSettings : INotifyPropertyChanged
{
    private string _theme = "系统";
    private string _backdropMaterial = "Mica";
    private double _terminalFontSize = 14;
    private string _downloadDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
    private bool _rememberCredentials;

    public string Theme { get => _theme; set => SetField(ref _theme, value); }
    public string BackdropMaterial { get => _backdropMaterial; set => SetField(ref _backdropMaterial, value); }
    public double TerminalFontSize { get => _terminalFontSize; set => SetField(ref _terminalFontSize, value); }
    public string DownloadDirectory { get => _downloadDirectory; set => SetField(ref _downloadDirectory, value); }
    public bool RememberCredentials { get => _rememberCredentials; set => SetField(ref _rememberCredentials, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RemoteFileItem
{
    public string Name { get; init; } = string.Empty;
    public string SortName => (IsDirectory ? "0" : "1") + Name;
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string TypeLabel { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string SizeLabel { get; init; } = string.Empty;
    public DateTime ModifiedAt { get; init; }
    public string ModifiedLabel { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public string FullPath { get; init; } = string.Empty;

    public override string ToString() => IsDirectory
        ? $"{Name,-42}  目录                     {ModifiedLabel}"
        : $"{Name,-42}  {SizeLabel,12}       {ModifiedLabel}";
}

public sealed class ServerMetrics
{
    public double? CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double SwapPercent { get; init; }
    public string LoadAverage { get; init; } = "—";
    public string Hostname { get; init; } = "—";
    public string OperatingSystem { get; init; } = "Linux";
    public string Uptime { get; init; } = "—";
}
