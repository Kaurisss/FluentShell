namespace FluentShell.Core;

/// <summary>
/// 标签栏与内容宿主之间的协调：哪些会话在标签栏上、谁被选中、内容区该显示哪个会话。
/// </summary>
public sealed class SessionHost
{
    private readonly ISessionTabStrip _tabStrip;

    public SessionHost(ISessionTabStrip tabStrip)
    {
        _tabStrip = tabStrip;
        _tabStrip.NewSessionRequested += TabStrip_NewSessionRequested;
        _tabStrip.SessionSelected += TabStrip_SessionSelected;
        _tabStrip.SessionCloseRequested += TabStrip_SessionCloseRequested;
    }

    public IShellSession? Selected { get; private set; }

    public event EventHandler? NewSessionRequested;
    public event EventHandler<IShellSession>? SessionSelected;
    public event EventHandler<IShellSession>? SessionCloseRequested;

    /// <summary>内容区该显示的会话；为 <c>null</c> 表示当前没有会话可显示。</summary>
    public event EventHandler<IShellSession?>? ContentChanged;

    public void Add(IShellSession session)
    {
        _tabStrip.Add(session);
        Select(session);
    }

    public void Select(IShellSession session)
    {
        _tabStrip.Select(session);
        Selected = session;
        ContentChanged?.Invoke(this, session);
    }

    public void Remove(IShellSession session)
    {
        _tabStrip.Remove(session);
        if (!ReferenceEquals(Selected, session)) return;

        Selected = null;
        ContentChanged?.Invoke(this, null);
    }

    private void TabStrip_NewSessionRequested(object? sender, EventArgs e) =>
        NewSessionRequested?.Invoke(this, EventArgs.Empty);

    private void TabStrip_SessionSelected(object? sender, IShellSession session) =>
        SessionSelected?.Invoke(this, session);

    private void TabStrip_SessionCloseRequested(object? sender, IShellSession session) =>
        SessionCloseRequested?.Invoke(this, session);
}
