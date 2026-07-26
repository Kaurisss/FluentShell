using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public sealed class ShellLayoutModeTests
{
    [TestMethod]
    public void Crossing_the_breakpoint_switches_the_pane_display_mode()
    {
        var layout = new ShellLayoutMode();

        var wide = layout.Measure(1000, isPaneOpen: true);
        var narrow = layout.Measure(600, isPaneOpen: true);
        var wideAgain = layout.Measure(1000, isPaneOpen: false);

        Assert.AreEqual(NavigationPaneDisplay.Left, wide.PaneDisplay);
        Assert.IsTrue(wide.PaneStateChanged);
        Assert.AreEqual(NavigationPaneDisplay.LeftMinimal, narrow.PaneDisplay);
        Assert.IsTrue(narrow.IsNarrow);
        Assert.IsFalse(narrow.IsPaneOpen, "进入窄屏时面板必须收起。");
        Assert.AreEqual(NavigationPaneDisplay.Left, wideAgain.PaneDisplay);
    }

    [TestMethod]
    public void Exactly_at_the_breakpoint_is_not_narrow()
    {
        var layout = new ShellLayoutMode();

        var atBreakpoint = layout.Measure(ShellLayoutMode.NarrowBreakpoint, isPaneOpen: true);

        Assert.IsFalse(atBreakpoint.IsNarrow, "720 本身属于宽屏，只有小于 720 才是窄屏。");
    }

    [TestMethod]
    public void Staying_on_the_same_side_of_the_breakpoint_leaves_the_pane_alone()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);

        var second = layout.Measure(900, isPaneOpen: false);

        Assert.IsFalse(
            second.PaneStateChanged,
            "同一断点内的宽度变化不得覆盖用户手动的面板开合。");
    }

    [TestMethod]
    public void Narrow_round_trip_restores_the_pane_state_from_before_going_narrow()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);
        layout.NotePaneClosed();

        layout.Measure(600, isPaneOpen: false);
        var restored = layout.Measure(1000, isPaneOpen: false);

        Assert.IsFalse(restored.IsPaneOpen, "窄屏前面板是收起的，回到宽屏后应保持收起。");
    }

    [TestMethod]
    public void Narrow_round_trip_reopens_a_pane_that_was_open_before_going_narrow()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);
        layout.NotePaneOpened();

        layout.Measure(600, isPaneOpen: true);
        var restored = layout.Measure(1000, isPaneOpen: false);

        Assert.IsTrue(restored.IsPaneOpen, "窄屏前面板是展开的，回到宽屏后应重新展开。");
    }

    [TestMethod]
    public void Pane_changes_caused_by_applying_the_layout_are_not_user_intent()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);
        layout.NotePaneOpened();

        // 进入窄屏时调用方会把面板收起，NavigationView 随即回弹一次 PaneClosing。
        layout.Measure(600, isPaneOpen: true);
        using (layout.BeginApplying()) layout.NotePaneClosed();
        var restored = layout.Measure(1000, isPaneOpen: false);

        Assert.IsTrue(
            restored.IsPaneOpen,
            "应用布局产生的面板收起不得被记成用户意图，否则窄屏往返后无法恢复。");
        Assert.IsFalse(layout.IsSidebarCollapsed);
    }

    [TestMethod]
    public void Pane_changes_while_narrow_are_not_recorded_as_wide_screen_intent()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);
        layout.NotePaneOpened();
        layout.Measure(600, isPaneOpen: true);

        layout.NotePaneClosed();
        var restored = layout.Measure(1000, isPaneOpen: false);

        Assert.IsTrue(restored.IsPaneOpen, "窄屏下的开合不改变回到宽屏时要恢复的状态。");
    }

    [TestMethod]
    public void Pane_changes_before_the_first_measurement_are_ignored()
    {
        var layout = new ShellLayoutMode();

        layout.NotePaneClosed();

        Assert.IsFalse(layout.IsMeasured);
        Assert.IsFalse(
            layout.IsSidebarCollapsed,
            "量过宽度之前无法判断处于哪个断点，此时的面板事件不计为用户意图。");
    }

    [TestMethod]
    public void First_measurement_into_narrow_does_not_invent_a_previous_pane_state()
    {
        var layout = new ShellLayoutMode();

        layout.Measure(600, isPaneOpen: false);
        var wide = layout.Measure(1000, isPaneOpen: false);

        Assert.IsTrue(wide.IsPaneOpen, "启动即窄屏时没有『之前』可记，回到宽屏应使用默认的展开。");
    }

    [TestMethod]
    public void Collapsing_the_sidebar_is_tracked_for_metric_rendering()
    {
        var layout = new ShellLayoutMode();
        layout.Measure(1000, isPaneOpen: true);

        layout.NotePaneClosed();
        Assert.IsTrue(layout.IsSidebarCollapsed);

        layout.NotePaneOpened();
        Assert.IsFalse(layout.IsSidebarCollapsed);
    }

    [TestMethod]
    public void Navigation_is_locked_while_any_session_exists()
    {
        var layout = new ShellLayoutMode();

        Assert.IsFalse(layout.IsNavigationLockedBySessions(0));
        Assert.IsTrue(layout.IsNavigationLockedBySessions(1));
        Assert.IsTrue(layout.IsNavigationLockedBySessions(5));
    }
}
