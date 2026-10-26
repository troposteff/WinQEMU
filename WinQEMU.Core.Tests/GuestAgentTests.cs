using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WinQEMU.Core.Models;
using WinQEMU.Core.Qemu;
using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Shared folders mounted inside the guest through qemu-guest-agent: the channel, the protocol, and
/// what the mount script is given.
/// </summary>
public class GuestAgentTests
{
    [Fact]
    public void The_guest_channels_come_last_so_existing_machines_keep_their_slots()
    {
        var args = QemuArgumentBuilder.Build(Fixtures.Vm(), Fixtures.Installation(), 4444, 50000, guestAgentPort: 4555, sharedFoldersPort: 4556);
        var devices = Fixtures.Values(args, "-device");

        Assert.Equal("virtio-serial-pci,id=wq-qga-bus", devices[^3]);
        Assert.Equal("virtserialport,bus=wq-qga-bus.0,chardev=wq-qga,name=org.qemu.guest_agent.0", devices[^2]);
        Assert.Equal("virtserialport,bus=wq-qga-bus.0,chardev=wq-shares,name=org.winqemu.shares.0", devices[^1]);
        Assert.True(devices.IndexOf("qemu-xhci,id=wq-usb") < devices.Count - 3);
        Assert.Contains("socket,id=wq-qga,host=127.0.0.1,port=4555,server=on,wait=off", Fixtures.Values(args, "-chardev"));
        Assert.Contains("socket,id=wq-shares,host=127.0.0.1,port=4556,server=on,wait=off", Fixtures.Values(args, "-chardev"));
    }

    [Fact]
    public void No_port_means_no_guest_channel()
    {
        Assert.DoesNotContain(Fixtures.Values(Fixtures.Build(Fixtures.Vm()), "-device"), d => d.Contains("wq-qga"));
    }

    [Fact]
    public void The_mount_script_opens_the_port_the_shares_are_served_on()
    {
        Assert.Contains("/dev/virtio-ports/" + QemuArgumentBuilder.SharedFoldersPortName, GuestShareMounter.MountScript);
        Assert.Contains("trans=unix,version=9p2000.L", GuestShareMounter.MountScript);
    }

    [Theory]
    [InlineData(13, "", "redémarrez-la")]
    [InlineData(12, "mount: bad option", "bad option")]
    [InlineData(10, "", "aucun compte")]
    public void A_failed_mount_says_why(int exitCode, string error, string expected)
    {
        Assert.Contains(expected, GuestShareMounter.Explain(new GuestExecResult(exitCode, string.Empty, error)));
    }

    [Theory]
    [InlineData(@"C:\Users\user\Music", "", "Music")]
    [InlineData(@"C:\Users\user\Music", "Music (2)", "Music (2)")]
    public void A_folder_is_named_in_the_guest_after_itself_unless_named_otherwise(string hostPath, string name, string expected)
    {
        Assert.Equal(expected, new SharedFolderConfig { HostPath = hostPath, Name = name }.GuestName);
    }

    /// <summary>
    /// A stand-in agent on loopback: it first replays answers left over from earlier commands, as a real
    /// agent does when it starts after commands were written, then answers properly.
    /// </summary>
    private static async Task<(int Port, Task Served)> FakeAgentAsync(Func<JsonObject, JsonObject> answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            listener.Stop();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"return\": {}}\n{\"return\": 12345}\n"));

            while (await reader.ReadLineAsync() is { } line)
            {
                var request = JsonNode.Parse(line)!.AsObject();
                if (request["execute"]!.GetValue<string>() == "guest-sync-delimited")
                {
                    var id = request["arguments"]!["id"]!.GetValue<long>();
                    await stream.WriteAsync(new byte[] { 0xFF });
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"return\": {id}}}\n"));
                    continue;
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(answer(request).ToJsonString() + "\n"));
            }
        });

        await Task.Yield();
        return (port, served);
    }

    [Fact]
    public async Task Synchronising_skips_stale_answers_and_runs_a_program_with_its_input()
    {
        var pids = 0;
        string? input = null;
        var (port, _) = await FakeAgentAsync(request => request["execute"]!.GetValue<string>() switch
        {
            "guest-exec" => Return(new JsonObject { ["pid"] = ++pids }, () =>
                input = Encoding.UTF8.GetString(Convert.FromBase64String(request["arguments"]!["input-data"]!.GetValue<string>()))),
            "guest-exec-status" => new JsonObject
            {
                ["return"] = new JsonObject
                {
                    ["exited"] = true,
                    ["exitcode"] = 0,
                    ["out-data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("/home/steph/Partages/X\n"))
                }
            },
            _ => new JsonObject { ["error"] = new JsonObject { ["desc"] = "unexpected" } }
        });

        await using var agent = await GuestAgentClient.ConnectAsync(port);
        Assert.True(await agent.SynchronizeAsync(TimeSpan.FromSeconds(5)));

        var result = await agent.RunAsync("/bin/sh", new[] { "-c", "true" }, "secret\n", TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/home/steph/Partages/X\n", result.Output);
        Assert.Equal("secret\n", input);
    }

    [Fact]
    public async Task No_agent_in_the_guest_is_a_timeout_not_a_hang()
    {
        // QEMU accepts the connection whether or not an agent listens behind it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        await using var agent = await GuestAgentClient.ConnectAsync(port);
        using var silent = await accepted;

        Assert.False(await agent.SynchronizeAsync(TimeSpan.FromMilliseconds(300)));
        listener.Stop();
    }

    private static JsonObject Return(JsonObject value, Action sideEffect)
    {
        sideEffect();
        return new JsonObject { ["return"] = value };
    }
}
