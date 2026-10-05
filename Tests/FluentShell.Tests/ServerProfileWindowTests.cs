using FluentShell.Views;

namespace FluentShell.Tests;

[TestClass]
public sealed class ServerProfileWindowTests
{
    [TestMethod]
    public void HasPrivateKeyPath_does_not_validate_an_empty_selection()
    {
        Assert.IsFalse(ServerProfileWindow.HasPrivateKeyPath(null));
        Assert.IsFalse(ServerProfileWindow.HasPrivateKeyPath(string.Empty));
        Assert.IsFalse(ServerProfileWindow.HasPrivateKeyPath("   "));
    }

    [TestMethod]
    public void HasPrivateKeyPath_validates_a_selected_path()
    {
        Assert.IsTrue(ServerProfileWindow.HasPrivateKeyPath("C:\\Users\\test\\.ssh\\id_ed25519"));
    }
}
