using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class JumpHostResolverTests
{
    [TestMethod]
    public void Direct_profile_has_no_jump()
    {
        var target = NewProfile("目标");
        Assert.IsNull(JumpHostResolver.Resolve(target, []));
    }

    [TestMethod]
    public void Resolves_a_separate_direct_profile()
    {
        var jump = NewProfile("跳板");
        var target = NewProfile("目标");
        target.JumpProfileId = jump.Id;
        Assert.AreSame(jump, JumpHostResolver.Resolve(target, [jump, target]));
    }

    [TestMethod]
    public void Missing_jump_fails_instead_of_falling_back_to_direct()
    {
        var target = NewProfile("目标");
        target.JumpProfileId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => JumpHostResolver.Resolve(target, [target]));
    }

    [TestMethod]
    public void Rejects_self_reference_and_nested_jump()
    {
        var target = NewProfile("目标");
        target.JumpProfileId = target.Id;
        Assert.Throws<InvalidOperationException>(() => JumpHostResolver.Resolve(target, [target]));

        var jump = NewProfile("跳板");
        jump.JumpProfileId = Guid.NewGuid();
        target.JumpProfileId = jump.Id;
        Assert.Throws<InvalidOperationException>(() => JumpHostResolver.Resolve(target, [jump, target]));
    }

    private static ServerProfile NewProfile(string name) =>
        new() { Name = name, Host = name, Username = "user" };
}
