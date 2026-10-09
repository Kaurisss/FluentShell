using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentShell.Views.Shell;

// DesktopAcrylicBackdrop does not expose Kind. Use the native controller while
// retaining SystemBackdrop's theme, activation and accessibility configuration.
public sealed class ThinAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private ICompositionSupportsSystemBackdrop? _target;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        if (_target is not null) throw new InvalidOperationException("Each window needs its own backdrop instance.");
        // The default configuration keeps a weak target reference. Retain the
        // projected target for the entire connection, as the built-in backdrops do.
        _target = connectedTarget;
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;

        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _target = null;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // WinUI updates the configuration already assigned to our controller.
        // No reconfiguration is needed, including late notifications after a
        // target disconnects (whose weak target may have already expired).
    }
}
