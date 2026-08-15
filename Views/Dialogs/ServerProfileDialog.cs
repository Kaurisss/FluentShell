using FluentShell.Models;
using FluentShell.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Dialogs;

public sealed class ServerProfileDialogContext
{
    public required XamlRoot XamlRoot { get; init; }
    public required IntPtr WindowHandle { get; init; }
    public required Brush MutedTextBrush { get; init; }
    public required bool HasSavedCredential { get; init; }
    public required bool RememberCredentialsByDefault { get; init; }
    public required IReadOnlyList<ServerProfile> ExistingProfiles { get; init; }
}

public sealed class ServerProfileDialogResult
{
    public required ServerProfile Profile { get; init; }
    public required bool SaveCredential { get; init; }
    public required bool CredentialIdentityChanged { get; init; }
    public string OriginalUsername { get; init; } = string.Empty;
    public required bool ConnectAfterSave { get; init; }
    public string EnteredSecret { get; init; } = string.Empty;
}

public static class ServerProfileDialog
{
    private static readonly PrivateKeyValidator PrivateKeyValidator = new();
    private static readonly ServerProfileValidator ServerProfileValidator = new();

    public static async Task<ServerProfileDialogResult?> ShowAsync(
        ServerProfile? editing,
        ServerProfileDialogContext context)
    {
        var originalUsername = editing?.Username;
        var originalAuthentication = editing?.Authentication;
        var name = new TextBox
        {
            Header = "显示名称",
            Text = editing?.Name ?? string.Empty,
            PlaceholderText = "例如：生产服务器"
        };
        var host = new TextBox
        {
            Header = "主机地址",
            Text = editing?.Host ?? string.Empty,
            PlaceholderText = "example.com 或 IP 地址"
        };
        var port = new NumberBox
        {
            Header = "端口",
            Value = editing?.Port ?? 22,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var user = new TextBox
        {
            Header = "用户名",
            Text = editing?.Username ?? string.Empty
        };
        var duplicateWarning = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var userSection = new StackPanel { Spacing = 4 };
        userSection.Children.Add(user);
        userSection.Children.Add(duplicateWarning);

        var authentication = new ComboBox
        {
            Header = "认证方式",
            SelectedIndex = editing?.Authentication == AuthenticationMethod.PrivateKey ? 1 : 0
        };
        authentication.Items.Add(new ComboBoxItem { Content = "密码" });
        authentication.Items.Add(new ComboBoxItem { Content = "私钥" });

        var secret = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
        var rememberCredential = new CheckBox
        {
            Content = "保存凭据到 Windows 凭据管理器",
            IsChecked = context.HasSavedCredential || context.RememberCredentialsByDefault
        };
        var credentialInfo = new TextBlock
        {
            Text = "凭据不会写入服务器配置文件。留空会保留已有凭据；取消勾选会删除这台服务器已保存的凭据。",
            FontSize = 12,
            Foreground = context.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        var keyPath = new TextBox
        {
            Header = "私钥文件",
            Text = editing?.PrivateKeyPath ?? string.Empty,
            PlaceholderText = "选择或输入 OpenSSH 私钥文件"
        };
        var keyValidationProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var keyFormatGuidance = new TextBlock
        {
            Text = "支持 OpenSSH 格式私钥；PuTTY .ppk 需先转换。",
            FontSize = 12,
            Foreground = context.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        var keyValidationMessage = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var keyPickerSection = new StackPanel { Spacing = 4 };

        var notes = new TextBox
        {
            Header = "备注",
            Text = editing?.Notes ?? string.Empty,
            PlaceholderText = "可选",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        var form = new StackPanel { Spacing = 12, MaxWidth = 560 };
        var validationError = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        using var validationState = new PrivateKeyValidationState(PrivateKeyValidator);
        ContentDialog? dialog = null;

        bool UsesPrivateKey() => authentication.SelectedIndex == 1;

        int SelectedPort() => port.Value is double value && !double.IsNaN(value)
            ? (int)value
            : 22;

        void UpdateActionButtons()
        {
            if (dialog is null) return;

            var keyIsReady = !UsesPrivateKey() ||
                (!validationState.IsValidating && validationState.Result?.IsValid == true);
            dialog.IsPrimaryButtonEnabled = keyIsReady;
            dialog.IsSecondaryButtonEnabled = keyIsReady;
        }

        void UpdateSecretField()
        {
            var usesPrivateKey = UsesPrivateKey();
            var selectedAuthentication = usesPrivateKey
                ? AuthenticationMethod.PrivateKey
                : AuthenticationMethod.Password;
            var canPreserveSavedCredential = context.HasSavedCredential &&
                originalAuthentication == selectedAuthentication &&
                string.Equals(originalUsername, user.Text.Trim(), StringComparison.Ordinal);
            var requiresPassphrase = validationState.Result?.RequiresPassphrase == true;

            secret.Header = usesPrivateKey
                ? requiresPassphrase ? "私钥口令" : "私钥口令（可选）"
                : "密码";
            secret.PlaceholderText = usesPrivateKey && requiresPassphrase
                ? "此私钥需要口令"
                : canPreserveSavedCredential
                    ? "已保存；留空保持不变"
                    : usesPrivateKey ? "私钥没有口令时可留空" : "输入登录密码";
        }

        void UpdateKeyValidationPresentation()
        {
            keyValidationProgress.IsActive = validationState.IsValidating;
            keyValidationProgress.Visibility = validationState.IsValidating
                ? Visibility.Visible
                : Visibility.Collapsed;

            var message = validationState.IsValidating ? null : validationState.Result?.ErrorMessage;
            keyValidationMessage.Text = message ?? string.Empty;
            keyValidationMessage.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
            keyValidationMessage.Foreground = validationState.Result?.RequiresPassphrase == true
                ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

            UpdateSecretField();
            UpdateActionButtons();
        }

        void UpdateAuthenticationFields()
        {
            var usesPrivateKey = UsesPrivateKey();
            keyPickerSection.Visibility = usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
            if (!usesPrivateKey)
                validationState.Reset();

            UpdateSecretField();
            UpdateActionButtons();
        }

        void UpdateDuplicateWarning()
        {
            var candidate = new ServerProfile
            {
                Host = host.Text,
                Port = SelectedPort(),
                Username = user.Text
            };
            var result = ServerProfileValidator.CheckForDuplicate(
                context.ExistingProfiles,
                candidate,
                editing?.Id);

            duplicateWarning.Text = result.IsDuplicate
                ? $"已存在相同的服务器配置（{result.ExistingProfileName}），确定要创建重复配置吗？"
                : string.Empty;
            duplicateWarning.Visibility = result.IsDuplicate
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void ScheduleKeyPathValidation()
        {
            if (UsesPrivateKey())
                validationState.Schedule(keyPath.Text.Trim());
        }

        string? ValidateFields()
        {
            if (string.IsNullOrWhiteSpace(name.Text) ||
                string.IsNullOrWhiteSpace(host.Text) ||
                string.IsNullOrWhiteSpace(user.Text))
            {
                return "显示名称、主机地址和用户名不能为空。";
            }
            if (UsesPrivateKey() && string.IsNullOrWhiteSpace(keyPath.Text))
                return "私钥认证需要选择或输入一个本机私钥文件。";
            return null;
        }

        void ShowValidationError(string error)
        {
            validationError.Text = error;
            validationError.Visibility = Visibility.Visible;
        }

        void ClearValidationError()
        {
            validationError.Text = string.Empty;
            validationError.Visibility = Visibility.Collapsed;
        }

        async void CancelCloseWhenInvalid(ContentDialog _, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var error = ValidateFields();
                if (error is not null)
                {
                    args.Cancel = true;
                    ShowValidationError(error);
                    return;
                }

                if (UsesPrivateKey())
                {
                    var result = await validationState.ValidateAsync(keyPath.Text.Trim(), force: true);
                    if (result is null || !result.IsValid)
                    {
                        args.Cancel = true;
                        ShowValidationError(result?.ErrorMessage ?? "私钥文件验证未完成，请重试。");
                        return;
                    }
                }

                ClearValidationError();
            }
            finally
            {
                deferral.Complete();
            }
        }

        var keyPickerRow = BuildKeyPickerRow(
            keyPath,
            context.WindowHandle,
            keyValidationProgress,
            async () => { await validationState.ValidateAsync(keyPath.Text.Trim(), force: true); });
        keyPickerSection.Children.Add(keyPickerRow);
        keyPickerSection.Children.Add(keyFormatGuidance);
        keyPickerSection.Children.Add(keyValidationMessage);

        foreach (var child in new UIElement[]
        {
            name,
            host,
            port,
            userSection,
            authentication,
            keyPickerSection,
            secret,
            rememberCredential,
            credentialInfo,
            notes,
            validationError
        })
        {
            form.Children.Add(child);
        }

        dialog = new ContentDialog
        {
            Title = editing is null ? "添加服务器" : "编辑服务器",
            Content = new ScrollViewer { Content = form, MaxHeight = 620 },
            PrimaryButtonText = editing is null ? "保存" : "保存修改",
            SecondaryButtonText = "保存并连接",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = context.XamlRoot
        };
        dialog.PrimaryButtonClick += CancelCloseWhenInvalid;
        dialog.SecondaryButtonClick += CancelCloseWhenInvalid;
        dialog.Closed += (_, _) => validationState.Dispose();

        validationState.Changed += (_, _) => UpdateKeyValidationPresentation();
        authentication.SelectionChanged += (_, _) =>
        {
            UpdateAuthenticationFields();
            if (UsesPrivateKey())
                _ = validationState.ValidateAsync(keyPath.Text.Trim(), force: true);
        };
        user.TextChanged += (_, _) =>
        {
            UpdateSecretField();
            UpdateDuplicateWarning();
        };
        host.TextChanged += (_, _) => UpdateDuplicateWarning();
        port.ValueChanged += (_, _) => UpdateDuplicateWarning();
        keyPath.TextChanged += (_, _) => ScheduleKeyPathValidation();

        UpdateAuthenticationFields();
        UpdateDuplicateWarning();
        if (UsesPrivateKey() && !string.IsNullOrWhiteSpace(keyPath.Text))
            _ = validationState.ValidateAsync(keyPath.Text.Trim());

        ContentDialogResult dialogResult;
        try
        {
            dialogResult = await dialog.ShowAsync();
        }
        finally
        {
            validationState.Dispose();
        }

        if (dialogResult == ContentDialogResult.None) return null;

        var selectedAuthentication = UsesPrivateKey()
            ? AuthenticationMethod.PrivateKey
            : AuthenticationMethod.Password;
        var newUsername = user.Text.Trim();
        var profile = editing ?? new ServerProfile();
        profile.Name = name.Text.Trim();
        profile.Host = host.Text.Trim();
        profile.Port = SelectedPort();
        profile.Username = newUsername;
        profile.Authentication = selectedAuthentication;
        profile.PrivateKeyPath = keyPath.Text.Trim();
        profile.Notes = notes.Text.Trim();

        return new ServerProfileDialogResult
        {
            Profile = profile,
            SaveCredential = rememberCredential.IsChecked == true,
            CredentialIdentityChanged = editing is not null &&
                (!string.Equals(originalUsername, newUsername, StringComparison.Ordinal) ||
                    originalAuthentication != selectedAuthentication),
            OriginalUsername = originalUsername ?? string.Empty,
            ConnectAfterSave = dialogResult == ContentDialogResult.Secondary,
            EnteredSecret = secret.Password
        };
    }

    private static Grid BuildKeyPickerRow(
        TextBox keyPath,
        IntPtr windowHandle,
        ProgressRing validationProgress,
        Func<Task> validateKeyPathAsync)
    {
        var chooseKeyButton = new Button
        {
            Content = "选择文件",
            MinHeight = 40,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTipService.SetToolTip(chooseKeyButton, "选择 OpenSSH 格式私钥文件");
        chooseKeyButton.Click += async (_, _) =>
        {
            var selectedPath = PrivateKeyFilePicker.Pick(windowHandle);
            if (selectedPath is null) return;

            keyPath.Text = selectedPath;
            await validateKeyPathAsync();
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(keyPath);
        Grid.SetColumn(validationProgress, 1);
        row.Children.Add(validationProgress);
        Grid.SetColumn(chooseKeyButton, 2);
        row.Children.Add(chooseKeyButton);
        return row;
    }

    private sealed class PrivateKeyValidationState : IDisposable
    {
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
        private readonly PrivateKeyValidator _validator;
        private readonly Dictionary<string, PrivateKeyValidationResult> _cache =
            new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _validationCancellation;
        private CancellationTokenSource? _debounceCancellation;
        private long _generation;
        private bool _isDisposed;

        public PrivateKeyValidationState(PrivateKeyValidator validator) => _validator = validator;

        public PrivateKeyValidationResult? Result { get; private set; }
        public bool IsValidating { get; private set; }
        public event EventHandler? Changed;

        public void Reset()
        {
            if (_isDisposed) return;

            Cancel(ref _debounceCancellation);
            CancelValidation();
            Result = null;
            IsValidating = false;
            NotifyChanged();
        }

        public void Schedule(string? privateKeyPath)
        {
            if (_isDisposed) return;

            Cancel(ref _debounceCancellation);
            CancelValidation();
            Result = null;
            IsValidating = false;
            NotifyChanged();
            if (string.IsNullOrWhiteSpace(privateKeyPath))
            {
                _ = ValidateAsync(privateKeyPath);
                return;
            }

            var cancellationSource = new CancellationTokenSource();
            _debounceCancellation = cancellationSource;
            _ = ValidateAfterDebounceAsync(privateKeyPath, cancellationSource);
        }

        public Task<PrivateKeyValidationResult?> ValidateAsync(
            string? privateKeyPath,
            bool force = false)
        {
            if (_isDisposed) return Task.FromResult<PrivateKeyValidationResult?>(null);

            Cancel(ref _debounceCancellation);
            return ValidateCoreAsync(privateKeyPath, force);
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            Changed = null;
            Cancel(ref _debounceCancellation);
            CancelValidation();
        }

        private async Task<PrivateKeyValidationResult?> ValidateCoreAsync(
            string? privateKeyPath,
            bool force)
        {
            CancelValidation();
            var requestGeneration = ++_generation;
            var normalizedPath = privateKeyPath?.Trim();
            Result = null;
            IsValidating = true;
            NotifyChanged();

            if (!force &&
                !string.IsNullOrWhiteSpace(normalizedPath) &&
                _cache.TryGetValue(normalizedPath, out var cachedResult))
            {
                if (IsCurrent(requestGeneration))
                {
                    Result = cachedResult;
                    IsValidating = false;
                    NotifyChanged();
                }
                return cachedResult;
            }

            var cancellationSource = new CancellationTokenSource();
            _validationCancellation = cancellationSource;
            try
            {
                var result = await _validator.ValidateAsync(normalizedPath, cancellationSource.Token);
                if (!IsCurrent(requestGeneration, cancellationSource) ||
                    cancellationSource.IsCancellationRequested)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(normalizedPath))
                    _cache[normalizedPath] = result;
                Result = result;
                return result;
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
                var result = new PrivateKeyValidationResult(
                    false,
                    PrivateKeyValidator.InvalidFormatMessage,
                    false);
                if (IsCurrent(requestGeneration, cancellationSource))
                {
                    Result = result;
                    return result;
                }

                return null;
            }
            finally
            {
                if (ReferenceEquals(_validationCancellation, cancellationSource))
                    _validationCancellation = null;
                if (IsCurrent(requestGeneration))
                {
                    IsValidating = false;
                    NotifyChanged();
                }

                cancellationSource.Dispose();
            }
        }

        private async Task ValidateAfterDebounceAsync(
            string privateKeyPath,
            CancellationTokenSource cancellationSource)
        {
            try
            {
                await Task.Delay(DebounceDelay, cancellationSource.Token);
                if (!cancellationSource.IsCancellationRequested)
                    await ValidateCoreAsync(privateKeyPath, force: false);
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_debounceCancellation, cancellationSource))
                    _debounceCancellation = null;
                cancellationSource.Dispose();
            }
        }

        private bool IsCurrent(long generation, CancellationTokenSource? cancellationSource = null) =>
            !_isDisposed &&
            generation == _generation &&
            (cancellationSource is null || ReferenceEquals(_validationCancellation, cancellationSource));

        private void CancelValidation()
        {
            _generation++;
            Cancel(ref _validationCancellation);
        }

        private void NotifyChanged()
        {
            if (!_isDisposed)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private static void Cancel(ref CancellationTokenSource? cancellationSource)
        {
            var source = cancellationSource;
            cancellationSource = null;
            if (source is null) return;

            // 当前异步操作在 finally 中释放 CTS；此处立即 Dispose 会与仍在使用该令牌的操作竞争。
            source.Cancel();
        }
    }
}
