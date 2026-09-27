using FluentShell.Views.Session;

namespace FluentShell.Tests;

[TestClass]
public sealed class LocalPaneItemTests
{
    [TestMethod]
    public void Parent_entry_navigates_to_parent_and_has_no_timestamp()
    {
        var path = Path.Combine(Path.GetTempPath(), "child");
        var item = SftpWorkspaceView.LocalPaneItem.ParentOf(path)!;
        Assert.AreEqual("..", item.Name);
        Assert.AreEqual(Directory.GetParent(path)!.FullName, item.FullPath);
        Assert.IsTrue(item.IsDirectory);
        Assert.AreEqual(string.Empty, item.ModifiedLabel);
    }

    [TestMethod]
    public void Drive_root_has_no_parent_entry()
    {
        Assert.IsNull(SftpWorkspaceView.LocalPaneItem.ParentOf(Path.GetPathRoot(Path.GetTempPath())!));
    }
}
