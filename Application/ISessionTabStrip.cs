namespace FluentShell.Core;

/// <summary>一个会话标签需要显示的全部文本，由会话本身推导。</summary>
public sealed record SessionTabPresentation(
    string Title,
    string ToolTip,
    string SelectAccessibleName,
    string CloseToolTip,
    string CloseAccessibleName)
{
    public static SessionTabPresentation For(IShellSession session) => new(
        session.DisplayTitle,
        session.DisplayTitle,
        $"切换到 {session.DisplayTitle} 会话",
        "关闭会话",
        $"关闭 {session.DisplayTitle} 会话");
}

/// <summary>
/// 会话标签栏的呈现出口：标签的增删与选中往这里写，用户的请求从这里回来。
/// </summary>
/// <remarks>
/// 生产适配器是 <c>Views/Shell/SessionTabStrip</c>，它把 <see cref="SessionTabPresentation"/>
/// 应用到按钮上；测试适配器只记录调用。标签上的文本是会话的纯函数，因此不需要窗口就能断言。
/// </remarks>
public interface ISessionTabStrip
{
    event EventHandler? NewSessionRequested;
    event EventHandler<IShellSession>? SessionSelected;
    event EventHandler<IShellSession>? SessionCloseRequested;

    void Add(IShellSession session);
    void Select(IShellSession session);
    void Remove(IShellSession session);
}
