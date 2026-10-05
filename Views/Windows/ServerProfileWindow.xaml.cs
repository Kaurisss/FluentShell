using FluentShell.Models;
using FluentShell.Services;
using FluentShell.Views.Shell;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace FluentShell.Views;

public sealed class ServerProfileWindowContext
{
    public required XamlRoot OwnerXamlRoot { get; init; }
    public required IntPtr OwnerWindowHandle { get; init; }
    public required bool HasSavedCredential { get; init; }
    public required IReadOnlyList<ServerProfile> ExistingProfiles { get; init; }
    public TestServerConnection? TestConnectionAsync { get; init; }
}

public sealed class ServerProfileWindowResult
{
    public required ServerProfile Profile { get; init; }
    public required bool SaveCredential { get; init; }
    public required bool CredentialIdentityChanged { get; init; }
    public string OriginalUsername { get; init; } = string.Empty;
    public required bool ConnectAfterSave { get; init; }
    public string EnteredSecret { get; init; } = string.Empty;
}

public sealed partial class ServerProfileWindow : Window
{
    private static readonly PrivateKeyValidator PrivateKeyValidator = new();
    private static readonly ServerProfileValidator ServerProfileValidator = new();
    private readonly TaskCompletionSource<ServerProfileWindowResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ServerProfileWindowResult?> Completion => _completion.Task;

    internal static bool HasPrivateKeyPath(string? privateKeyPath) => !string.IsNullOrWhiteSpace(privateKeyPath);

    public ServerProfileWindow(
        ServerProfile? editing,
        ServerProfileWindowContext context)
    {
        InitializeComponent();
        ToastSurface.Translation = new System.Numerics.Vector3(0, 0, 24);
        Title = editing is null ? "添加服务器" : "编辑服务器";
        WindowTitle.Text = Title;
        EditorHeading.Text = Title;
        SaveButton.Content = editing is null ? "保存" : "保存修改";
        var ownerRoot = context.OwnerXamlRoot.Content as FrameworkElement;
        void ApplyWindowTheme(FrameworkElement? _, object? args)
        {
            if (ownerRoot is not null) RootGrid.RequestedTheme = ownerRoot.ActualTheme;
            WindowChrome.ApplyTitleBarColors(AppWindow, RootGrid.ActualTheme, "系统");
        }
        ApplyWindowTheme(null, null);
        if (ownerRoot is not null) ownerRoot.ActualThemeChanged += ApplyWindowTheme;
        RootGrid.ActualThemeChanged += (_, _) => WindowChrome.ApplyTitleBarColors(AppWindow, RootGrid.ActualTheme, "系统");
        ConfigureWindow(context);
        var originalUsername = editing?.Username;
        var originalAuthentication = editing?.Authentication;
        var originalProtocol = editing?.Protocol;
        var protocol = new ComboBox { Header = "连接协议", HorizontalAlignment = HorizontalAlignment.Stretch };
        protocol.Items.Add(new ComboBoxItem { Content = "SSH（终端和文件）" });
        protocol.Items.Add(new ComboBoxItem { Content = "SFTP（仅文件）" });
        protocol.Items.Add(new ComboBoxItem { Content = "FTP（仅文件，不加密）" });
        protocol.SelectedIndex = (int)(editing?.Protocol ?? ConnectionProtocol.Ssh);
        var ftpWarning = new TextBlock
        {
            Text = "FTP 会明文传输密码和文件，请仅用于可信网络。需要加密时请选择 SFTP。",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)RootGrid.Resources["WarningTextStyle"]
        };
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
            Style = (Style)RootGrid.Resources["WarningTextStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var userSection = new StackPanel { Spacing = 4 };
        userSection.Children.Add(user);
        userSection.Children.Add(duplicateWarning);

        var jumpHost = new ComboBox
        {
            Header = "跳板服务器",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        jumpHost.Items.Add(new ComboBoxItem { Content = "不使用（直连）" });
        foreach (var candidate in context.ExistingProfiles.Where(candidate =>
                     candidate.Protocol == ConnectionProtocol.Ssh && candidate.JumpProfileId is null &&
                     (editing is null || JumpHostResolver.IsEligible(editing, candidate))))
        {
            jumpHost.Items.Add(new ComboBoxItem
            {
                Content = $"{candidate.Name}（{candidate.Address}）",
                Tag = candidate.Id
            });
        }
        jumpHost.SelectedIndex = 0;
        if (editing?.JumpProfileId is Guid selectedJumpId)
        {
            var selected = jumpHost.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is Guid id && id == selectedJumpId);
            if (selected is null)
            {
                selected = new ComboBoxItem
                {
                    Content = "原跳板已失效，请重新选择",
                    Tag = selectedJumpId
                };
                jumpHost.Items.Add(selected);
            }
            jumpHost.SelectedItem = selected;
        }

        var jumpSection = new StackPanel { Spacing = 4 };
        jumpSection.Children.Add(jumpHost);

        var authentication = new ComboBox
        {
            Header = "认证方式",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            SelectedIndex = editing?.Authentication == AuthenticationMethod.PrivateKey ? 1 : 0
        };
        authentication.Items.Add(new ComboBoxItem { Content = "密码" });
        authentication.Items.Add(new ComboBoxItem { Content = "私钥" });

        var secret = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek, VerticalAlignment = VerticalAlignment.Top };
        var rememberCredential = new CheckBox
        {
            Content = "保存凭据到 Windows 凭据管理器",
            IsChecked = true
        };
        var credentialInfo = new TextBlock
        {
            Text = "凭据不会写入服务器配置文件。留空会保留已有凭据；取消勾选会删除这台服务器已保存的凭据。",
            FontSize = 12,
            Style = (Style)RootGrid.Resources["MutedTextStyle"],
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
            Style = (Style)RootGrid.Resources["MutedTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var keyValidationMessage = new TextBlock
        {
            Style = (Style)RootGrid.Resources["ErrorTextStyle"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var keyPickerSection = new StackPanel { Spacing = 4 };
        var authenticationFields = new Grid { ColumnSpacing = 12 };
        authenticationFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        authenticationFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        authenticationFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        authenticationFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        authenticationFields.Children.Add(authentication);
        Grid.SetColumn(keyPickerSection, 1);
        authenticationFields.Children.Add(keyPickerSection);
        authenticationFields.Children.Add(secret);

        var notes = new TextBox
        {
            Header = "备注",
            Text = editing?.Notes ?? string.Empty,
            PlaceholderText = "可选",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        var form = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(32, 0, 32, 24)
        };
        var validationError = ValidationError;

        var validationState = new PrivateKeyValidationState(PrivateKeyValidator);
        CancellationTokenSource? testCancellation = null;
        var isClosed = false;
        var isSaving = false;
        ServerProfileWindowResult? savedResult = null;
        var testButton = TestButton;
        var testProgress = new ProgressRing { Width = 20, Height = 20, MinWidth = 20, MinHeight = 20, IsActive = false };
        var trustPanel = TrustPanel;
        var trustMessage = TrustMessage;
        var acceptTrust = AcceptTrustButton;
        var rejectTrust = RejectTrustButton;
        TaskCompletionSource<bool>? trustDecision = null;
        acceptTrust.Click += (_, _) => trustDecision?.TrySetResult(true);
        rejectTrust.Click += (_, _) => trustDecision?.TrySetResult(false);
        var toastTimer = DispatcherQueue.CreateTimer();
        toastTimer.Interval = TimeSpan.FromSeconds(5);
        toastTimer.IsRepeating = false;
        toastTimer.Tick += (_, _) =>
        {
            if (!isClosed) ConnectionTestInfoBar.IsOpen = false;
        };
        var toastIsHovered = false;
        ConnectionTestInfoBar.PointerEntered += (_, _) => { toastIsHovered = true; toastTimer.Stop(); };
        ConnectionTestInfoBar.PointerExited += (_, _) =>
        {
            toastIsHovered = false;
            if (!isClosed && ConnectionTestInfoBar.IsOpen) toastTimer.Start();
        };
        ConnectionTestInfoBar.GotFocus += (_, _) => toastTimer.Stop();
        ConnectionTestInfoBar.LostFocus += (_, _) =>
        {
            if (!isClosed && ConnectionTestInfoBar.IsOpen && !toastIsHovered) toastTimer.Start();
        };
        ConnectionTestInfoBar.Closed += (_, _) =>
        {
            toastTimer.Stop();
            ToastSurface.Visibility = Visibility.Collapsed;
        };

        void ShowTestResult(InfoBarSeverity severity, string title, string message)
        {
            if (isClosed) return;
            toastTimer.Stop();
            ConnectionTestInfoBar.Severity = severity;
            ConnectionTestInfoBar.Title = title;
            ConnectionTestInfoBar.Message = message;
            ToastSurface.Visibility = Visibility.Visible;
            ConnectionTestInfoBar.IsOpen = true;
            if (!toastIsHovered) toastTimer.Start();
        }

        async Task<bool> ConfirmTestFingerprintAsync(HostFingerprintRequiredEventArgs fingerprint, CancellationToken token)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!form.DispatcherQueue.TryEnqueue(() =>
            {
                if (isClosed || token.IsCancellationRequested) { decision.TrySetResult(false); return; }
                trustDecision = decision;
                trustMessage.Text = $"首次连接“{fingerprint.Profile?.Name}”，请核对可信来源的服务器指纹。\n算法：{fingerprint.KeyType}\n指纹：{fingerprint.Fingerprint}";
                trustPanel.Visibility = Visibility.Visible;
            })) return false;
            try { return await decision.Task.WaitAsync(token).ConfigureAwait(false); }
            finally
            {
                form.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!isClosed && ReferenceEquals(trustDecision, decision))
                    {
                        trustDecision = null;
                        trustPanel.Visibility = Visibility.Collapsed;
                    }
                });
            }
        }

        bool UsesPrivateKey() => authentication.SelectedIndex == 1;
        ConnectionProtocol SelectedProtocol() => (ConnectionProtocol)protocol.SelectedIndex;

        Guid? SelectedJumpId() => (jumpHost.SelectedItem as ComboBoxItem)?.Tag is Guid id ? id : null;

        int SelectedPort() => port.Value is double value && !double.IsNaN(value)
            ? (int)value
            : ServerProfile.DefaultPort(SelectedProtocol());

        void UpdateActionButtons()
        {
            if (isClosed) return;

            var keyIsReady = !UsesPrivateKey() ||
                (!validationState.IsValidating && validationState.Result?.IsValid == true);
            SaveButton.IsEnabled = keyIsReady && !isSaving && testCancellation is null;
            SaveAndConnectButton.IsEnabled = SaveButton.IsEnabled;
            testButton.IsEnabled = !isSaving && (testCancellation is not null || (keyIsReady && context.TestConnectionAsync is not null));
        }

        void UpdateSecretField()
        {
            var usesPrivateKey = UsesPrivateKey();
            var selectedAuthentication = usesPrivateKey
                ? AuthenticationMethod.PrivateKey
                : AuthenticationMethod.Password;
            var canPreserveSavedCredential = context.HasSavedCredential &&
                originalProtocol == SelectedProtocol() &&
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
            keyValidationMessage.Style = (Style)RootGrid.Resources[validationState.Result?.RequiresPassphrase == true
                ? "WarningTextStyle" : "ErrorTextStyle"];

            UpdateSecretField();
            UpdateActionButtons();
        }

        void UpdateAuthenticationFields()
        {
            var usesPrivateKey = UsesPrivateKey();
            keyPickerSection.Visibility = usesPrivateKey ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(secret, usesPrivateKey ? 0 : 1);
            Grid.SetColumnSpan(secret, usesPrivateKey ? 2 : 1);
            Grid.SetRow(secret, usesPrivateKey ? 1 : 0);
            secret.Margin = usesPrivateKey ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            if (!usesPrivateKey)
                validationState.Reset();

            UpdateSecretField();
            UpdateActionButtons();
        }

        void UpdateDuplicateWarning()
        {
            var candidate = new ServerProfile
            {
                Protocol = SelectedProtocol(),
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
            if (!UsesPrivateKey())
                return;

            var privateKeyPath = keyPath.Text.Trim();
            if (HasPrivateKeyPath(privateKeyPath))
                validationState.Schedule(privateKeyPath);
            else
                validationState.Reset();
        }

        string? ValidateFields()
        {
            if (!Enum.IsDefined(SelectedProtocol())) return "请选择连接协议。";
            if (!double.IsFinite(port.Value) || port.Value < 1 || port.Value > 65535 || port.Value != Math.Truncate(port.Value))
                return "端口必须是 1 到 65535 之间的整数。";
            if (editing is not null && SelectedProtocol() != ConnectionProtocol.Ssh &&
                context.ExistingProfiles.Any(profile => profile.JumpProfileId == editing.Id))
                return "这台服务器正被用作 SSH 跳板，不能改为仅文件协议。";
            if (string.IsNullOrWhiteSpace(name.Text) ||
                string.IsNullOrWhiteSpace(host.Text) ||
                string.IsNullOrWhiteSpace(user.Text))
            {
                return "显示名称、主机地址和用户名不能为空。";
            }
            if (UsesPrivateKey() && string.IsNullOrWhiteSpace(keyPath.Text))
                return "私钥认证需要选择或输入一个本机私钥文件。";
            if (editing is not null && SelectedJumpId() is not null &&
                context.ExistingProfiles.Any(profile => profile.JumpProfileId == editing.Id))
                return "这台服务器正被用作跳板，请先修改引用它的服务器配置。";
            if (SelectedJumpId() is Guid jumpId)
            {
                var candidate = context.ExistingProfiles.FirstOrDefault(profile => profile.Id == jumpId);
                if (candidate is null ||
                    (editing is not null && !JumpHostResolver.IsEligible(editing, candidate)) ||
                    candidate.JumpProfileId is not null)
                    return "请选择另一台直连的已保存服务器作为跳板。";
            }
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

        async Task SaveAsync(bool connectAfterSave)
        {
            if (isClosed || isSaving || testCancellation is not null) return;
            isSaving = true;
            UpdateActionButtons();
            try
            {
                var error = ValidateFields();
                if (error is not null)
                {
                    ShowValidationError(error);
                    return;
                }

                if (UsesPrivateKey())
                {
                    var result = await validationState.ValidateAsync(keyPath.Text.Trim(), force: true);
                    if (result is null || !result.IsValid)
                    {
                        if (isClosed) return;
                        ShowValidationError(result?.ErrorMessage ?? "私钥文件验证未完成，请重试。");
                        return;
                    }
                }

                if (isClosed) return;
                ClearValidationError();
                var selectedAuthentication = UsesPrivateKey() ? AuthenticationMethod.PrivateKey : AuthenticationMethod.Password;
                var newUsername = user.Text.Trim();
                var profile = editing ?? new ServerProfile();
                profile.Protocol = SelectedProtocol();
                profile.Name = name.Text.Trim();
                profile.Host = host.Text.Trim();
                profile.Port = SelectedPort();
                profile.Username = newUsername;
                profile.JumpProfileId = SelectedJumpId();
                profile.Authentication = selectedAuthentication;
                profile.PrivateKeyPath = keyPath.Text.Trim();
                profile.Notes = notes.Text.Trim();
                savedResult = new ServerProfileWindowResult
                {
                    Profile = profile,
                    SaveCredential = rememberCredential.IsChecked == true,
                    CredentialIdentityChanged = editing is not null &&
                        (!string.Equals(originalUsername, newUsername, StringComparison.Ordinal) ||
                            originalAuthentication != selectedAuthentication || originalProtocol != SelectedProtocol()),
                    OriginalUsername = originalUsername ?? string.Empty,
                    ConnectAfterSave = connectAfterSave,
                    EnteredSecret = secret.Password
                };
                Close();
            }
            finally
            {
                isSaving = false;
                UpdateActionButtons();
            }
        }

        var keyPickerRow = BuildKeyPickerRow(
            keyPath,
            WindowNative.GetWindowHandle(this),
            keyValidationProgress,
            async () => { await validationState.ValidateAsync(keyPath.Text.Trim(), force: true); },
            () => isClosed,
            ShowValidationError);
        keyPickerSection.Children.Add(keyPickerRow);
        keyPickerSection.Children.Add(keyFormatGuidance);
        keyPickerSection.Children.Add(keyValidationMessage);

        foreach (var child in new UIElement[]
        {
            name,
            protocol,
            ftpWarning,
            host,
            port,
            userSection,
            jumpSection,
            authenticationFields,
            rememberCredential,
            credentialInfo,
            notes
        })
        {
            form.Children.Add(child);
        }

        var formScrollViewer = new ScrollViewer
        {
            Content = form,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        EditorHost.Children.Add(formScrollViewer);

        testButton.Click += async (_, _) =>
        {
            if (testCancellation is not null) { testCancellation.Cancel(); return; }
            var error = ValidateFields();
            if (error is not null) { ShowTestResult(InfoBarSeverity.Warning, "无法测试连接", error); return; }
            if (context.TestConnectionAsync is null) return;
            // Never mutate the saved profile when testing unsaved edits.
            var draft = new ServerProfile
            {
                Id = editing?.Id ?? Guid.NewGuid(), Name = name.Text.Trim(),
                Protocol = SelectedProtocol(), Host = host.Text.Trim(), Port = SelectedPort(),
                Username = user.Text.Trim(), JumpProfileId = SelectedJumpId(),
                Authentication = UsesPrivateKey() ? AuthenticationMethod.PrivateKey : AuthenticationMethod.Password,
                PrivateKeyPath = keyPath.Text.Trim(), HostFingerprint = editing?.HostFingerprint ?? string.Empty
            };
            using var cancellation = new CancellationTokenSource();
            testCancellation = cancellation;
            formScrollViewer.IsEnabled = false;
            ConnectionTestInfoBar.IsOpen = false;
            ClearValidationError();
            testProgress.IsActive = true;
            testButton.Content = testProgress;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(testButton, "取消测试连接");
            ToolTipService.SetToolTip(testButton, "正在测试连接，点击取消");
            UpdateActionButtons();
            try
            {
                await context.TestConnectionAsync(draft, secret.Password, ConfirmTestFingerprintAsync, cancellation.Token);
                ShowTestResult(InfoBarSeverity.Success, "连接成功", "已断开测试连接。配置尚未保存。");
            }
            catch (OperationCanceledException)
            {
                ShowTestResult(InfoBarSeverity.Informational, "测试已取消", "可以修改配置后重新测试。");
            }
            catch (Exception exception)
            {
                ShowTestResult(InfoBarSeverity.Error, "连接失败", exception.Message);
            }
            finally
            {
                testCancellation = null;
                trustDecision?.TrySetResult(false);
                if (!isClosed)
                {
                    trustPanel.Visibility = Visibility.Collapsed;
                    formScrollViewer.IsEnabled = true;
                    testProgress.IsActive = false;
                    testButton.Content = "测试连接";
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(testButton, "测试连接");
                    ToolTipService.SetToolTip(testButton, "测试连接");
                    UpdateActionButtons();
                }
            }
        };

        SaveButton.Click += async (_, _) => await SaveAsync(false);
        SaveAndConnectButton.Click += async (_, _) => await SaveAsync(true);
        var saveShortcut = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        saveShortcut.Invoked += async (_, args) => { args.Handled = true; if (SaveButton.IsEnabled) await SaveAsync(false); };
        var closeShortcut = new KeyboardAccelerator { Key = VirtualKey.Escape };
        closeShortcut.Invoked += (_, args) => { args.Handled = true; Close(); };
        RootGrid.KeyboardAccelerators.Add(saveShortcut);
        RootGrid.KeyboardAccelerators.Add(closeShortcut);
        RootGrid.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        Closed += (_, _) =>
        {
            isClosed = true;
            toastTimer.Stop();
            testCancellation?.Cancel();
            trustDecision?.TrySetResult(false);
            validationState.Dispose();
            if (ownerRoot is not null) ownerRoot.ActualThemeChanged -= ApplyWindowTheme;
            secret.Password = string.Empty;
            _completion.TrySetResult(savedResult);
        };

        validationState.Changed += (_, _) => UpdateKeyValidationPresentation();
        void UpdateProtocolFields()
        {
            var isFtp = SelectedProtocol() == ConnectionProtocol.Ftp;
            ftpWarning.Visibility = isFtp ? Visibility.Visible : Visibility.Collapsed;
            jumpSection.Visibility = isFtp ? Visibility.Collapsed : Visibility.Visible;
            authentication.IsEnabled = !isFtp;
            if (isFtp)
            {
                authentication.SelectedIndex = 0;
                jumpHost.SelectedIndex = 0;
            }
            UpdateAuthenticationFields();
            UpdateDuplicateWarning();
        }
        var previousProtocol = SelectedProtocol();
        protocol.SelectionChanged += (_, _) =>
        {
            if (port.Value == ServerProfile.DefaultPort(previousProtocol))
                port.Value = ServerProfile.DefaultPort(SelectedProtocol());
            previousProtocol = SelectedProtocol();
            secret.Password = string.Empty;
            UpdateProtocolFields();
        };
        authentication.SelectionChanged += (_, _) =>
        {
            UpdateAuthenticationFields();
            if (!UsesPrivateKey())
                return;

            var privateKeyPath = keyPath.Text.Trim();
            if (HasPrivateKeyPath(privateKeyPath))
                _ = validationState.ValidateAsync(privateKeyPath, force: true);
            else
                validationState.Reset();
        };
        user.TextChanged += (_, _) =>
        {
            UpdateSecretField();
            UpdateDuplicateWarning();
        };
        host.TextChanged += (_, _) => UpdateDuplicateWarning();
        port.ValueChanged += (_, _) => UpdateDuplicateWarning();
        keyPath.TextChanged += (_, _) => ScheduleKeyPathValidation();

        UpdateProtocolFields();
        if (UsesPrivateKey() && !string.IsNullOrWhiteSpace(keyPath.Text))
            _ = validationState.ValidateAsync(keyPath.Text.Trim());

    }

    private void ConfigureWindow(ServerProfileWindowContext context)
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(EditorTitleBar);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FluentShell.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        var owner = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(context.OwnerWindowHandle));
        var area = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = context.OwnerXamlRoot.RasterizationScale;
        var width = Math.Min((int)(680 * scale), area.Width);
        var height = Math.Min((int)(860 * scale), area.Height);
        var x = Math.Clamp(owner.Position.X + (owner.Size.Width - width) / 2, area.X, area.X + area.Width - width);
        var y = Math.Clamp(owner.Position.Y + (owner.Size.Height - height) / 2, area.Y, area.Y + area.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(560 * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(480 * scale), area.Height);
        }
    }

    private static Grid BuildKeyPickerRow(
        TextBox keyPath,
        IntPtr windowHandle,
        ProgressRing validationProgress,
        Func<Task> validateKeyPathAsync,
        Func<bool> isClosed,
        Action<string> showError)
    {
        var chooseKeyButton = new Button
        {
            Content = "选择文件",
            Height = 32,
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTipService.SetToolTip(chooseKeyButton, "选择 OpenSSH 格式私钥文件");
        chooseKeyButton.Click += async (_, _) =>
        {
            chooseKeyButton.IsEnabled = false;
            try
            {
                var selectedPath = await PrivateKeyFilePicker.PickAsync(windowHandle);
                if (isClosed() || selectedPath is null) return;
                keyPath.Text = selectedPath;
                await validateKeyPathAsync();
            }
            catch (Exception)
            {
                if (!isClosed()) showError("无法打开私钥文件选择器，请直接输入文件路径。");
            }
            finally
            {
                if (!isClosed()) chooseKeyButton.IsEnabled = true;
            }
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
