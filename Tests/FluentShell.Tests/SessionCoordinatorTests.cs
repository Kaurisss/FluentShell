using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public sealed class SessionCoordinatorTests
{
    [TestMethod]
    public void Remove_selected_session_selects_adjacent_session()
    {
        var first = new TestSession(Guid.NewGuid());
        var second = new TestSession(Guid.NewGuid());
        var third = new TestSession(Guid.NewGuid());
        var coordinator = CreateCoordinator(first, second, third);
        coordinator.Select(second);

        var selected = coordinator.Remove(second);

        Assert.AreSame(third, selected);
        Assert.AreEqual(2, coordinator.Count);
    }

    [TestMethod]
    public void Connection_guard_rejects_duplicate_attempt_until_ended()
    {
        var coordinator = new SessionCoordinator<TestSession>(session => session.Id);
        var key = Guid.NewGuid();

        Assert.IsTrue(coordinator.TryBeginConnection(key));
        Assert.IsFalse(coordinator.TryBeginConnection(key));
        coordinator.EndConnection(key);
        Assert.IsTrue(coordinator.TryBeginConnection(key));
    }

    private static SessionCoordinator<TestSession> CreateCoordinator(params TestSession[] sessions)
    {
        var coordinator = new SessionCoordinator<TestSession>(session => session.Id);
        foreach (var session in sessions) coordinator.Add(session);
        return coordinator;
    }

    private sealed record TestSession(Guid Id);
}