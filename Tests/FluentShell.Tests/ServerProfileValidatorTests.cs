using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class ServerProfileValidatorTests
{
    private readonly ServerProfileValidator _validator = new();

    [TestMethod]
    public void CheckForDuplicate_finds_matching_host_port_and_username()
    {
        var existing = Profile("生产服务器", "prod.example.com", 22, "deploy");
        var candidate = Profile("新配置", "prod.example.com", 22, "deploy");

        var result = _validator.CheckForDuplicate([existing], candidate);

        Assert.IsTrue(result.IsDuplicate);
        Assert.AreEqual("生产服务器", result.ExistingProfileName);
    }

    [TestMethod]
    public void CheckForDuplicate_normalizes_host_case_and_whitespace()
    {
        var existing = Profile("生产服务器", " PROD.EXAMPLE.COM ", 22, "deploy");
        var candidate = Profile("新配置", "prod.example.com", 22, "deploy");

        var result = _validator.CheckForDuplicate([existing], candidate);

        Assert.IsTrue(result.IsDuplicate);
    }

    [TestMethod]
    public void CheckForDuplicate_keeps_usernames_case_sensitive()
    {
        var existing = Profile("生产服务器", "prod.example.com", 22, "Deploy");
        var candidate = Profile("新配置", "prod.example.com", 22, "deploy");

        var result = _validator.CheckForDuplicate([existing], candidate);

        Assert.IsFalse(result.IsDuplicate);
        Assert.IsNull(result.ExistingProfileName);
    }

    [TestMethod]
    public void CheckForDuplicate_excludes_the_profile_being_edited()
    {
        var existing = Profile("生产服务器", "prod.example.com", 22, "deploy");
        var candidate = Profile("生产服务器", "prod.example.com", 22, "deploy");

        var result = _validator.CheckForDuplicate([existing], candidate, existing.Id);

        Assert.IsFalse(result.IsDuplicate);
    }

    [TestMethod]
    public void CheckForDuplicate_ignores_different_hosts_ports_and_empty_lists()
    {
        var existing = Profile("生产服务器", "prod.example.com", 22, "deploy");
        var differentHost = Profile("新配置", "staging.example.com", 22, "deploy");
        var differentPort = Profile("新配置", "prod.example.com", 2222, "deploy");

        Assert.IsFalse(_validator.CheckForDuplicate([existing], differentHost).IsDuplicate);
        Assert.IsFalse(_validator.CheckForDuplicate([existing], differentPort).IsDuplicate);
        Assert.IsFalse(_validator.CheckForDuplicate([], existing).IsDuplicate);
        Assert.IsFalse(_validator.CheckForDuplicate(null, existing).IsDuplicate);
    }

    private static ServerProfile Profile(string name, string host, int port, string username) => new()
    {
        Name = name,
        Host = host,
        Port = port,
        Username = username
    };
}
