using WinQEMU.Core.Interop;

namespace WinQEMU.Core.Tests;

/// <summary>The guest's sound card, and the host side of its sound.</summary>
public class AudioTests
{
    [Fact]
    public void Sound_is_output_only_unless_the_microphone_is_asked_for()
    {
        var config = Fixtures.Vm();
        config.EnableAudio = true;

        var devices = Fixtures.Values(Fixtures.Build(config), "-device");

        Assert.Contains("intel-hda", devices);
        Assert.Contains("hda-output,audiodev=snd0", devices);
        Assert.DoesNotContain(devices, d => d.StartsWith("hda-duplex", StringComparison.Ordinal));
        Assert.Equal("dsound,id=snd0", Fixtures.Value(Fixtures.Build(config), "-audiodev"));
    }

    [Fact]
    public void The_microphone_gives_the_guest_a_line_in()
    {
        var config = Fixtures.Vm();
        config.EnableAudio = true;
        config.EnableMicrophone = true;

        var devices = Fixtures.Values(Fixtures.Build(config), "-device");

        Assert.Contains("hda-duplex,audiodev=snd0", devices);
        Assert.DoesNotContain(devices, d => d.StartsWith("hda-output", StringComparison.Ordinal));
    }

    [Fact]
    public void No_sound_card_means_no_audio_backend_either()
    {
        var config = Fixtures.Vm();
        config.EnableAudio = false;
        config.EnableMicrophone = true;

        var args = Fixtures.Build(config);

        Assert.DoesNotContain("-audiodev", args);
        Assert.DoesNotContain(Fixtures.Values(args, "-device"), d => d.StartsWith("hda-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(40, 40)]
    public void A_volume_stays_between_0_and_100(int set, int expected)
    {
        Assert.Equal(expected, new Models.VmConfig { AudioVolume = set }.AudioVolume);
    }

    [Fact]
    public void A_process_without_sound_has_no_session_and_nothing_is_set()
    {
        // This test's own process plays nothing. A PC without any output (a build server) lists none.
        var applied = new HashSet<string>();
        Assert.False(HostAudio.SetProcessVolume(Environment.ProcessId, 0.5f, true, applied));
        Assert.Empty(applied);
        Assert.All(HostAudio.ListOutputs(), output => Assert.False(string.IsNullOrEmpty(output.Name)));
    }
}
