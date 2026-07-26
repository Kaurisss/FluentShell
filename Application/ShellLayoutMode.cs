namespace FluentShell.Core;

public enum NavigationPaneDisplay
{
    Left,
    LeftMinimal
}

/// <summary>一次布局计算的结果，由调用方原样应用到控件上。</summary>
/// <param name="IsNarrow">当前是否为窄屏布局。</param>
/// <param name="PaneStateChanged">
/// 面板显示模式与开合是否需要写回。为 <c>false</c> 时调用方只应用与宽度相关的呈现，
/// 不得触碰面板 —— 这样用户在同一断点内的手动开合不会被布局计算覆盖。
/// </param>
public sealed record ShellLayout(
    bool IsNarrow,
    bool PaneStateChanged,
    NavigationPaneDisplay PaneDisplay,
    bool IsPaneOpen);

/// <summary>内容区四周的留白。</summary>
/// <param name="Horizontal">左右留白。</param>
/// <param name="Bottom">底部留白。</param>
public sealed record ShellContentSpacing(double Horizontal, double Bottom);

/// <summary>
/// 外壳的响应式布局规则：由窗口宽度与用户意图计算布局模式与导航面板状态。
/// </summary>
/// <remarks>
/// 本模块只返回结果，不持有也不操作控件。应用结果时必须包在
/// <see cref="BeginApplying"/> 里，否则由应用动作反弹回来的面板开合会被误记为用户意图。
/// </remarks>
public sealed class ShellLayoutMode
{
    public const double NarrowBreakpoint = 720;

    private bool? _isNarrow;
    private bool _paneWasOpenBeforeNarrow = true;
    private bool _isApplying;

    /// <summary>是否已经量过一次宽度。量过之前不应用布局，避免在控件加载完成前写面板状态。</summary>
    public bool IsMeasured => _isNarrow is not null;

    public bool IsNarrow => _isNarrow == true;

    /// <summary>侧栏是否被用户收起，决定连接侧栏是否继续渲染指标。</summary>
    public bool IsSidebarCollapsed { get; private set; }

    /// <summary>存在任何会话时导航被锁定：会话内容占据内容区，切换导航项没有去处。</summary>
    public bool IsNavigationLockedBySessions(int sessionCount) => sessionCount > 0;

    /// <summary>
    /// 内容区留白。会话布局比页面布局收得更紧：终端和目录列表要的是可视面积，
    /// 页面列表要的是呼吸感，两者不共用一套数值。
    /// </summary>
    public static ShellContentSpacing MeasureContentSpacing(bool isNarrow, bool isSessionLayout) =>
        isSessionLayout
            ? new ShellContentSpacing(isNarrow ? 8 : 12, isNarrow ? 8 : 10)
            : new ShellContentSpacing(isNarrow ? 16 : 30, isNarrow ? 16 : 28);

    public ShellLayout Measure(double width, bool isPaneOpen)
    {
        var isNarrow = width < NarrowBreakpoint;
        if (isNarrow == _isNarrow)
        {
            return new ShellLayout(
                isNarrow,
                PaneStateChanged: false,
                isNarrow ? NavigationPaneDisplay.LeftMinimal : NavigationPaneDisplay.Left,
                isPaneOpen);
        }

        var wasMeasured = IsMeasured;
        _isNarrow = isNarrow;
        if (!isNarrow)
        {
            return new ShellLayout(
                false,
                PaneStateChanged: true,
                NavigationPaneDisplay.Left,
                _paneWasOpenBeforeNarrow);
        }

        // 记下进入窄屏前的开合，窄屏往返后据此恢复。首次量宽时没有"之前"可记。
        if (wasMeasured) _paneWasOpenBeforeNarrow = isPaneOpen;
        return new ShellLayout(
            true,
            PaneStateChanged: true,
            NavigationPaneDisplay.LeftMinimal,
            IsPaneOpen: false);
    }

    /// <summary>面板已打开。由布局应用引起的开合不计为用户意图。</summary>
    public void NotePaneOpened()
    {
        if (_isApplying || _isNarrow != false) return;
        _paneWasOpenBeforeNarrow = true;
        IsSidebarCollapsed = false;
    }

    /// <summary>面板已关闭。由布局应用引起的开合不计为用户意图。</summary>
    public void NotePaneClosed()
    {
        if (_isApplying || _isNarrow != false) return;
        _paneWasOpenBeforeNarrow = false;
        IsSidebarCollapsed = true;
    }

    /// <summary>把布局结果写回控件的作用域；其间到达的面板开合不计为用户意图。</summary>
    public IDisposable BeginApplying()
    {
        _isApplying = true;
        return new ApplyScope(this);
    }

    private sealed class ApplyScope : IDisposable
    {
        private readonly ShellLayoutMode _owner;

        public ApplyScope(ShellLayoutMode owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner._isApplying = false;
    }
}
