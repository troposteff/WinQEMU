using WinQEMU.Core.Models;

namespace WinQEMU.Core.Tests;

public class AutoStartTests
{
    private static VmConfig Machine(string name, bool autoStart, int delay = 0) =>
        new() { Name = name, AutoStart = autoStart, AutoStartDelaySeconds = delay };

    [Fact]
    public void Only_the_machines_asked_for_start_and_the_shortest_delay_first()
    {
        var plan = AutoStartPlan.For(new[]
        {
            Machine("Serveur", true, 30),
            Machine("Poste", false),
            Machine("Base", true, 0),
            Machine("Proxy", true, 10)
        });

        Assert.Equal(new[] { "Base", "Proxy", "Serveur" }, plan.Select(e => e.Machine.Name));
        Assert.Equal(new[] { 0, 10, 30 }, plan.Select(e => (int)e.After.TotalSeconds));
    }

    [Fact]
    public void A_template_never_starts_with_winqemu()
    {
        var template = Machine("Modèle", true, 0);
        template.IsTemplate = true;

        Assert.Equal(new[] { "Serveur" }, AutoStartPlan.For(new[] { template, Machine("Serveur", true, 5) }).Select(e => e.Machine.Name));
    }

    [Fact]
    public void Equal_delays_keep_the_library_order()
    {
        var plan = AutoStartPlan.For(new[] { Machine("B", true, 5), Machine("A", true, 5) });

        Assert.Equal(new[] { "B", "A" }, plan.Select(e => e.Machine.Name));
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99999, 3600)]
    public void A_delay_stays_within_an_hour(int set, int expected)
    {
        Assert.Equal(expected, new VmConfig { AutoStartDelaySeconds = set }.AutoStartDelaySeconds);
    }

    [Fact]
    public void A_descriptor_without_the_setting_does_not_start_by_itself()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<VmConfig>("""{ "Name": "Ancienne" }""")!;

        Assert.False(config.AutoStart);
        Assert.Empty(AutoStartPlan.For(new[] { config }));
    }
}
