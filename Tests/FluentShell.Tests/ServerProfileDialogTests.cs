using FluentShell.Views.Dialogs;

namespace FluentShell.Tests;

[TestClass]
public sealed class ServerProfileDialogTests
{
    [TestMethod]
    public void HasPrivateKeyPath_does_not_validate_an_empty_selection()
    {
        Assert.IsFalse(ServerProfileDialog.HasPrivateKeyPath(null));
        Assert.IsFalse(ServerProfileDialog.HasPrivateKeyPath(string.Empty));
        Assert.IsFalse(ServerProfileDialog.HasPrivateKeyPath("   "));
    }

    [TestMethod]
    public void HasPrivateKeyPath_validates_a_selected_path()
    {
        Assert.IsTrue(ServerProfileDialog.HasPrivateKeyPath("C:\\Users\\test\\.ssh\\id_ed25519"));
    }
}
