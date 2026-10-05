using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentShell.Core;
using FluentShell.Models;
using FluentShell.Services;

namespace FluentShell.Tests;

[TestClass]
public sealed class FtpConnectionTests
{
    [TestMethod]
    public async Task Failure_on_transfer_connection_releases_the_browsing_connection()
    {
        await using var server = new FtpFixture(rejectSecondLogin: true);
        await using var connection = new FtpConnectionService(new ServerProfile
        {
            Protocol = ConnectionProtocol.Ftp, Host = "127.0.0.1", Port = server.Port, Username = "fixture"
        }, "synthetic");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<FluentFTP.Exceptions.FtpAuthenticationException>(() => connection.ConnectAsync(timeout.Token));
        Assert.AreEqual(2, server.ConnectionCount);
        Assert.IsFalse(connection.IsConnected);
        Assert.IsFalse(connection.SftpClient!.IsConnected);
        Assert.IsFalse(connection.TransferSftpClient!.IsConnected);
    }

    [TestMethod]
    public async Task Ftp_connects_two_channels_and_roundtrips_files_without_recursive_delete()
    {
        await using var server = new FtpFixture();
        await using var connection = new FtpConnectionService(new ServerProfile
        {
            Protocol = ConnectionProtocol.Ftp, Host = "127.0.0.1", Port = server.Port, Username = "fixture"
        }, "synthetic");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await connection.ConnectAsync(timeout.Token);
        Assert.IsTrue(connection.IsConnected);
        Assert.AreEqual(2, server.ConnectionCount);
        var browse = new SftpFileService(() => connection.SftpClient);
        var transfer = new SftpFileService(() => connection.TransferSftpClient);
        var bytes = Encoding.UTF8.GetBytes("文件内容\r\nFTP roundtrip");
        using var upload = new ByteCountingStream(new MemoryStream(bytes), _ => { });
        await transfer.UploadAsync(upload, "/test.txt", timeout.Token);
        var listing = await browse.ListDirectoryAsync("/");
        Assert.IsTrue(listing.Any(item => item.Name == "test.txt" && item.SizeBytes == bytes.Length));
        using var downloaded = new MemoryStream();
        await transfer.DownloadAsync("/test.txt", downloaded, timeout.Token);
        CollectionAssert.AreEqual(bytes, downloaded.ToArray());
        await browse.RenameAsync("/test.txt", "/renamed.txt");
        Assert.IsTrue(await browse.ExistsAsync("/renamed.txt"));
        await browse.CreateDirectoryAsync("/folder");
        await Assert.ThrowsAsync<IOException>(() => browse.DeleteAsync(new RemoteFileItem { IsDirectory = true, FullPath = "/folder" }));
        Assert.IsTrue(server.Commands.Contains("RMD"));
        Assert.IsFalse(server.Commands.Contains("DELE"), "Nonempty directory deletion must not delete child files.");
        await browse.DeleteAsync(new RemoteFileItem { FullPath = "/renamed.txt" });
        Assert.IsFalse(await browse.ExistsAsync("/renamed.txt"));
        await connection.DisposeAsync();
        Assert.IsFalse(connection.IsConnected);
    }

    // Loopback-only protocol fixture: synthetic credentials, in-memory files, no user data or external servers.
    private sealed class FtpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, byte[]> _files = new();
        private readonly List<Task> _clients = [];
        private readonly Task _accept;
        private int _connections;
        private readonly bool _rejectSecondLogin;
        public ConcurrentBag<string> Commands { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ConnectionCount => _connections;

        public FtpFixture(bool rejectSecondLogin = false)
        {
            _rejectSecondLogin = rejectSecondLogin;
            _listener.Start();
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var index = Interlocked.Increment(ref _connections);
                    _clients.Add(ServeAsync(client, _rejectSecondLogin && index == 2));
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task ServeAsync(TcpClient socket, bool rejectLogin)
        {
            using var client = socket;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            TcpListener? passive = null;
            string? rename = null;
            try
            {
                await writer.WriteLineAsync("220 FTP fixture");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var split = line.Split(' ', 2);
                    var command = split[0];
                    var path = split.Length > 1 ? split[1] : "";
                    Commands.Add(command); // Never record command arguments (PASS).
                    switch (command)
                    {
                        case "USER": await writer.WriteLineAsync("331 Password required"); break;
                        case "PASS": await writer.WriteLineAsync(rejectLogin ? "530 Login rejected" : "230 Logged in"); break;
                        case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8"); break;
                        case "FEAT": await writer.WriteLineAsync("211-Features\r\n UTF8\r\n SIZE\r\n MDTM\r\n211 End"); break;
                        case "PWD": await writer.WriteLineAsync("257 \"/\" is current directory"); break;
                        case "TYPE": case "OPTS": case "NOOP": await writer.WriteLineAsync("200 OK"); break;
                        case "CWD": await writer.WriteLineAsync(path is "/" or "/folder" ? "250 OK" : "550 Not found"); break;
                        case "SIZE": await writer.WriteLineAsync(_files.TryGetValue(path, out var sized) ? $"213 {sized.Length}" : "550 Not found"); break;
                        case "MDTM": await writer.WriteLineAsync("213 20260101000000"); break;
                        case "EPSV": case "PASV":
                            passive?.Stop();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            await writer.WriteLineAsync(command == "EPSV" ? $"229 Entering Extended Passive Mode (|||{port}|)" : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "STOR": case "RETR": case "LIST": case "NLST":
                            await writer.WriteLineAsync("150 Opening data connection");
                            using (var data = await passive!.AcceptTcpClientAsync(_stop.Token))
                            {
                                if (command == "STOR")
                                {
                                    using var memory = new MemoryStream();
                                    await data.GetStream().CopyToAsync(memory, _stop.Token);
                                    _files[path] = memory.ToArray();
                                }
                                else if (command == "RETR") await data.GetStream().WriteAsync(_files[path], _stop.Token);
                                else
                                {
                                    var listing = string.Concat(_files.Select(pair => command == "NLST" ? pair.Key + "\r\n" :
                                        $"-rw-r--r-- 1 fixture group {pair.Value.Length} Jan 01 2026 {pair.Key.TrimStart('/')}\r\n"));
                                    await data.GetStream().WriteAsync(Encoding.UTF8.GetBytes(listing), _stop.Token);
                                }
                            }
                            passive.Stop();
                            await writer.WriteLineAsync("226 Transfer complete");
                            break;
                        case "RNFR": rename = path; await writer.WriteLineAsync("350 Ready"); break;
                        case "RNTO":
                            if (_files.TryRemove(rename!, out var renamed)) _files[path] = renamed;
                            await writer.WriteLineAsync("250 Renamed"); break;
                        case "MKD": await writer.WriteLineAsync("257 Directory created"); break;
                        case "RMD": await writer.WriteLineAsync("550 Directory not empty"); break;
                        case "DELE": _files.TryRemove(path, out _); await writer.WriteLineAsync("250 Deleted"); break;
                        case "QUIT": await writer.WriteLineAsync("221 Goodbye"); return;
                        default: await writer.WriteLineAsync("502 Unsupported"); break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
            finally { passive?.Stop(); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
