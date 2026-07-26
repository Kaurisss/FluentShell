using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FluentShell.Views.Session;

/// <summary>
/// 终端与 SFTP 面板之间的拆分条。
/// 之所以是个容器而不是直接用 <see cref="Thumb"/>：光标要靠
/// <see cref="UIElement.ProtectedCursor"/> 换，而它是受保护成员、Thumb 又是密封类，
/// 只能把光标设在外层容器上，由内部 Thumb 沿用。指针形状是"这里可以拖"最直接的提示。
/// 外观由 App.xaml 的 WorkspaceSplitterStyle 提供。
/// </summary>
internal sealed class WorkspaceSplitter : Grid
{
    private readonly Thumb _thumb = new();

    public WorkspaceSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        _thumb.Style = (Style)Application.Current.Resources["WorkspaceSplitterStyle"];
        _thumb.DragStarted += (_, e) => DragStarted?.Invoke(this, e);
        _thumb.DragDelta += (_, e) => DragDelta?.Invoke(this, e);
        _thumb.DoubleTapped += (_, e) => DoubleTapped?.Invoke(this, e);
        Children.Add(_thumb);
    }

    public event EventHandler<DragStartedEventArgs>? DragStarted;

    public event EventHandler<DragDeltaEventArgs>? DragDelta;

    /// <summary>
    /// 双击手势。有意遮蔽 <see cref="UIElement.DoubleTapped"/>：内部 Thumb 铺满命中区
    /// 且拖拽把指针按压标记为已处理，继承的事件在本控件上收不到，这里转发 Thumb 自己的双击。
    /// </summary>
    public new event DoubleTappedEventHandler? DoubleTapped;
}
