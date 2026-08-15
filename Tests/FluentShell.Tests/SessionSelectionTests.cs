using FluentShell.Core;

namespace FluentShell.Tests;

[TestClass]
public sealed class SessionSelectionTests
{
    [TestMethod]
    public void AfterRemoval_selects_item_at_removed_index_when_available()
    {
        var remaining = new[] { "first", "third" };

        var selected = SessionSelection.AfterRemoval(remaining, 1, true, "second");

        Assert.AreEqual("third", selected);
    }

    [TestMethod]
    public void AfterRemoval_selects_previous_item_when_last_item_was_removed()
    {
        var remaining = new[] { "first", "second" };

        var selected = SessionSelection.AfterRemoval(remaining, 2, true, "third");

        Assert.AreEqual("second", selected);
    }

    [TestMethod]
    public void AfterRemoval_keeps_selection_when_another_item_was_removed()
    {
        var remaining = new[] { "second", "third" };

        var selected = SessionSelection.AfterRemoval(remaining, 0, false, "third");

        Assert.AreEqual("third", selected);
    }
}