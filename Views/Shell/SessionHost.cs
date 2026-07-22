using FluentShell.Core;
using FluentShell.Views;

namespace FluentShell.Views.Shell;

public sealed class SessionHost
{
    private readonly SessionTabStrip _tabStrip = new();

    public SessionHost()
    {
        _tabStrip.NewSessionRequested += (_, _) => NewSessionRequested?.Invoke(this, EventArgs.Empty);
        _tabStrip.SessionSelected += (_, session) => SessionSelected?.Invoke(this, session);
        _tabStrip.SessionCloseRequested += (_, session) => SessionCloseRequested?.Invoke(this, session);
    }

    public SessionTabStrip TabStrip => _tabStrip;
    public SessionWorkspace? Selected { get; private set; }

    public event EventHandler? NewSessionRequested;
    public event EventHandler<SessionWorkspace>? SessionSelected;
    public event EventHandler<SessionWorkspace>? SessionCloseRequested;
    public event EventHandler<SessionWorkspace?>? ContentChanged;

    public void Add(SessionWorkspace session)
    {
        _tabStrip.Add(session);
        Select(session);
    }

    public void Select(SessionWorkspace session)
    {
        _tabStrip.Select(session);
        Selected = session;
        ContentChanged?.Invoke(this, session);
    }

    public void Remove(SessionWorkspace session)
    {
        _tabStrip.Remove(session);
        if (!ReferenceEquals(Selected, session)) return;
        Selected = null;
        ContentChanged?.Invoke(this, null);
    }
}