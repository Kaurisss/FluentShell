using System.Diagnostics;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class SshDirectoryDeletionTests
{
    [TestMethod]
    [DataRow("/")]
    [DataRow("////")]
    [DataRow("/Mod/..")]
    [DataRow("/Mod/.")]
    [DataRow("/Mod/../keep")]
    [DataRow("relative")]
    [DataRow("/Mod\0bad")]
    public async Task Unsafe_paths_are_rejected_before_any_command_is_executed(string path)
    {
        var executed = false;
        await Assert.ThrowsAsync<IOException>(() => SshDirectoryDeletion.DeleteAsync(path, _ =>
        {
            executed = true;
            return Task.FromResult<(int?, string)>((0, ""));
        }));
        Assert.IsFalse(executed);
    }

    [TestMethod]
    public async Task A_directory_is_deleted_with_one_checked_command()
    {
        var commands = new List<string>();
        await SshDirectoryDeletion.DeleteAsync("/Mod/", text =>
        {
            commands.Add(text);
            return Task.FromResult<(int?, string)>((0, ""));
        });
        CollectionAssert.AreEqual(new[] { "rm -r -- '/Mod'" }, commands);
    }

    [TestMethod]
    [DataRow(1, "rm: Permission denied")]
    [DataRow(127, "rm: command not found")]
    [DataRow(null, "")]
    public async Task A_nonzero_or_missing_exit_status_never_reports_success(int? status, string message)
    {
        var error = await Assert.ThrowsAsync<IOException>(() => SshDirectoryDeletion.DeleteAsync("/Mod",
            _ => Task.FromResult((status, message))));
        if (message.Length > 0) Assert.AreEqual(message, error.Message);
        else Assert.Contains("未知", error.Message);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("/Mod/含 空格")]
    [DataRow("/Mod/a'b;$(echo injected)* `echo injected`")]
    [DataRow("/Mod/-rf")]
    [DataRow("/Mod/line\nbreak")]
    public async Task Posix_shell_receives_the_path_as_one_literal_argument(string path)
    {
        // 替换 rm 为只记录参数的函数。这里不会删除任何文件，也不会连接服务器。
        var output = await RunBashAsync("rm() { printf '%s\\0' \"$@\"; }\n" + SshDirectoryDeletion.BuildCommand(path) + "\n");
        CollectionAssert.AreEqual(new[] { "-r", "--", path, "" }, output.Split('\0'));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Local_rm_recursively_removes_contents_and_preserves_symbolic_link_targets()
    {
        // 创建和删除都在同一个 POSIX shell 中，只作用于 mktemp 创建的私有测试目录。
        var output = await RunBashAsync("""
            set -eu
            base=$(cd "${TMPDIR:-/tmp}" && pwd -P)
            fixture=$(mktemp -d "$base/fluentshell-rm-test.XXXXXXXX")
            case "$fixture" in "$base"/fluentshell-rm-test.*) ;; *) exit 12;; esac
            trap 'command rm -r -- "$fixture"' EXIT
            mkdir -p "$fixture/Mod/nested" "$fixture/Mod/empty" "$fixture/outside"
            printf content > "$fixture/Mod/nested/file"
            printf hidden > "$fixture/Mod/.hidden"
            printf keep > "$fixture/outside/keep"
            ln -s ../outside "$fixture/Mod/link"
            target="$fixture/Mod"
            case "$target" in "$fixture"/*) ;; *) exit 13;; esac
            command rm -r -- "$target"
            test ! -e "$target"
            test "$(cat "$fixture/outside/keep")" = keep
            printf verified
            """);
        Assert.AreEqual("verified", output);
    }

    private static async Task<string> RunBashAsync(string script)
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) Assert.Inconclusive("Git Bash is unavailable for the local POSIX argument check.");
        var start = new ProcessStartInfo(bash)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--noprofile");
        start.ArgumentList.Add("--norc");
        start.ArgumentList.Add("-s");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(script + "\n");
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, process.ExitCode, await errors);
        return await output;
    }
}
