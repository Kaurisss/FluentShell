using FluentShell.Core;
using FluentShell.Models;

namespace FluentShell.Tests;

[TestClass]
public sealed class SessionHostTests
{
    [TestMethod]
    public void Returning_to_menu_preserves_tabs_and_allows_reselecting_the_same_session()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var session = CreateSession("生产机");
        host.Add(session);
        IShellSession? content = session;
        host.ContentChanged += (_, current) => content = current;

        host.Select(null);

        Assert.IsNull(host.Selected);
        Assert.IsNull(tabStrip.Selected);
        Assert.IsNull(content);
        CollectionAssert.AreEqual(new[] { session }, tabStrip.Tabs);

        host.Select(session);
        Assert.AreSame(session, content);
        Assert.AreSame(session, tabStrip.Selected);
    }

    [TestMethod]
    public void Adding_a_session_puts_it_on_the_tab_strip_and_selects_it()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var session = CreateSession("生产机");
        IShellSession? presentedContent = null;
        host.ContentChanged += (_, current) => presentedContent = current;

        host.Add(session);

        CollectionAssert.AreEqual(new[] { session }, tabStrip.Tabs);
        Assert.AreSame(session, tabStrip.Selected);
        Assert.AreSame(session, host.Selected);
        Assert.AreSame(session, presentedContent);
    }

    [TestMethod]
    public void Selecting_another_session_moves_the_selection_and_the_content()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var first = CreateSession("第一台");
        var second = CreateSession("第二台");
        host.Add(first);
        host.Add(second);

        host.Select(first);

        Assert.AreSame(first, tabStrip.Selected);
        Assert.AreSame(first, host.Selected);
    }

    [TestMethod]
    public void Closing_the_selected_session_clears_the_content()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var session = CreateSession("生产机");
        host.Add(session);
        IShellSession? presentedContent = session;
        host.ContentChanged += (_, current) => presentedContent = current;

        host.Remove(session);

        Assert.IsEmpty(tabStrip.Tabs);
        Assert.IsNull(host.Selected);
        Assert.IsNull(presentedContent, "关闭选中的会话后内容区必须被清空。");
    }

    [TestMethod]
    public void Closing_an_unselected_session_leaves_the_content_alone()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var first = CreateSession("第一台");
        var second = CreateSession("第二台");
        host.Add(first);
        host.Add(second);
        var contentChanges = 0;
        host.ContentChanged += (_, _) => contentChanges++;

        host.Remove(first);

        Assert.AreEqual(0, contentChanges, "关闭未选中的会话不应改变内容区。");
        Assert.AreSame(second, host.Selected);
        CollectionAssert.AreEqual(new[] { second }, tabStrip.Tabs);
    }

    [TestMethod]
    public void Tab_strip_requests_reach_the_host()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var session = CreateSession("生产机");
        host.Add(session);
        var newSessionRequests = 0;
        IShellSession? selectRequest = null;
        IShellSession? closeRequest = null;
        host.NewSessionRequested += (_, _) => newSessionRequests++;
        host.SessionSelected += (_, current) => selectRequest = current;
        host.SessionCloseRequested += (_, current) => closeRequest = current;

        tabStrip.RaiseNewSessionRequested();
        tabStrip.RaiseSessionSelected(session);
        tabStrip.RaiseSessionCloseRequested(session);

        Assert.AreEqual(1, newSessionRequests);
        Assert.AreSame(session, selectRequest);
        Assert.AreSame(session, closeRequest);
    }

    [TestMethod]
    public async Task Same_server_tabs_select_and_close_the_requested_session()
    {
        var tabStrip = new RecordingSessionTabStrip();
        var host = new SessionHost(tabStrip);
        var shell = new ShellCoordinator(new InMemoryLocalStore(),
            (profile, _, _) => FakeShellSession.Connectable(profile),
            _ => Task.FromResult<string?>(null), _ => Task.FromResult(false));
        shell.SessionAdded += (_, session) => host.Add(session);
        shell.SessionSelected += (_, session) => host.Select(session);
        shell.SessionRemoved += (_, session) => host.Remove(session);
        host.SessionSelected += (_, session) => shell.SelectSession(session);
        object? content = null;
        host.ContentChanged += (_, session) => content = session?.ContentElement;
        var profile = new ServerProfile { Name = "生产机", Host = "offline.invalid", Username = "fixture" };
        await shell.ConnectAsync(profile);
        await shell.ConnectAsync(profile);
        var first = tabStrip.Tabs[0];
        var second = tabStrip.Tabs[1];

        tabStrip.RaiseSessionSelected(first);

        Assert.HasCount(2, tabStrip.Tabs);
        Assert.AreSame(first, shell.SelectedSession);
        Assert.AreSame(first.ContentElement, content);

        await shell.CloseSessionAsync(first, _ => Task.FromResult(true));

        CollectionAssert.AreEqual(new[] { second }, tabStrip.Tabs);
        Assert.AreSame(second, host.Selected);
        Assert.AreSame(second.ContentElement, content);
    }

    [TestMethod]
    public void Tab_text_is_derived_from_the_session_title()
    {
        var session = CreateSession("生产机");

        var presentation = SessionTabPresentation.For(session);

        Assert.AreEqual("生产机", presentation.Title);
        Assert.AreEqual("生产机", presentation.ToolTip);
        Assert.AreEqual("切换到 生产机 会话", presentation.SelectAccessibleName);
        Assert.AreEqual("关闭会话", presentation.CloseToolTip);
        Assert.AreEqual("关闭 生产机 会话", presentation.CloseAccessibleName);
    }

    private static FakeShellSession CreateSession(string name) =>
        new(new ServerProfile { Name = name, Host = "host", Username = "user" });

    private sealed class RecordingSessionTabStrip : ISessionTabStrip
    {
        public List<IShellSession> Tabs { get; } = [];
        public IShellSession? Selected { get; private set; }

        public event EventHandler? NewSessionRequested;
        public event EventHandler<IShellSession>? SessionSelected;
        public event EventHandler<IShellSession>? SessionCloseRequested;

        public void Add(IShellSession session) => Tabs.Add(session);

        public void Select(IShellSession? session) => Selected = session;

        public void Remove(IShellSession session)
        {
            Tabs.Remove(session);
            if (ReferenceEquals(Selected, session)) Selected = null;
        }

        public void RaiseNewSessionRequested() => NewSessionRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionSelected(IShellSession session) =>
            SessionSelected?.Invoke(this, session);

        public void RaiseSessionCloseRequested(IShellSession session) =>
            SessionCloseRequested?.Invoke(this, session);
    }
}
