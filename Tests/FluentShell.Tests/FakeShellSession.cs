using FluentShell.Core;
using FluentShell.Models;

namespace FluentShell.Tests;

/// <summary>
/// <see cref="IShellSession"/> 的测试适配器：连接行为由构造参数给出，
/// 激活次数与内容元素供标签栏与外壳协调的断言使用。
/// </summary>
internal sealed class FakeShellSession : IShellSession
{
    private readonly Func<FakeShellSession, Task> _connect;

    public FakeShellSession(ServerProfile profile, Func<FakeShellSession, Task>? connect = null)
    {
        Profile = profile;
        _connect = connect ?? (_ => Task.CompletedTask);
        ContentElement = new object();
    }

    public ServerProfile Profile { get; }
    public string DisplayTitle => Profile.Name;
    public object ContentElement { get; }
    public bool IsConnected => ConnectionState == SessionConnectionState.Connected;
    public SessionConnectionState ConnectionState { get; private set; } = SessionConnectionState.Disconnected;
    public bool IsTransferActive => false;
    public int MetricsPollingStarts { get; private set; }
    public CancellationToken LastConnectionCancellationToken { get; private set; }

    public event EventHandler<ServerMetrics?>? MetricsUpdated
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? StatusChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? ConnectionFailed;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        LastConnectionCancellationToken = cancellationToken;
        return _connect(this);
    }

    public void ReportConnectionFailure(string message) => ConnectionFailed?.Invoke(this, message);

    public void SetConnectionState(SessionConnectionState connectionState) =>
        ConnectionState = connectionState;

    public void SetActive(bool active)
    {
        if (active) MetricsPollingStarts++;
    }

    public void SetTerminalFontSize(double value) { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>连接后立刻进入已连接状态的会话，是多数测试需要的默认行为。</summary>
    public static FakeShellSession Connectable(ServerProfile profile) =>
        new(profile, session =>
        {
            session.SetConnectionState(SessionConnectionState.Connected);
            return Task.CompletedTask;
        });
}
