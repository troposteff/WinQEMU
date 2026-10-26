using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Quoting and tokenising. A comma is a field separator to QEMU, so any value carrying one has to be
/// doubled — Windows paths make that a routine case, not an edge one.
/// </summary>
public class EscapingTests
{
    [Fact]
    public void A_comma_in_a_value_is_doubled()
    {
        Assert.Equal(@"C:\Program Files,, Inc\qemu", QemuArgumentBuilder.EscapeOptionValue(@"C:\Program Files, Inc\qemu"));
    }

    [Fact]
    public void A_value_without_a_comma_is_untouched()
    {
        Assert.Equal(@"C:\qemu", QemuArgumentBuilder.EscapeOptionValue(@"C:\qemu"));
    }

    [Fact]
    public void A_comma_in_the_machine_name_is_escaped()
    {
        // -name takes sub-options, so a raw comma would start one.
        var args = Fixtures.Build(Fixtures.Vm("Debian, testing"));

        Assert.Equal("Debian,, testing", Fixtures.Value(args, "-name"));
    }

    [Fact]
    public void A_comma_in_a_disk_path_is_escaped()
    {
        var vm = Fixtures.Vm();
        vm.FolderPath = @"C:\VMs\Machine, one";

        Assert.Contains(@"C:\VMs\Machine,, one\disk0.qcow2", Fixtures.Drive(Fixtures.Build(vm), "disk0")!);
    }

    [Fact]
    public void Extra_arguments_are_split_on_whitespace()
    {
        Assert.Equal(new[] { "-device", "usb-host,vendorid=0x1234" },
            QemuArgumentBuilder.TokenizeExtraArguments("-device usb-host,vendorid=0x1234"));
    }

    [Fact]
    public void Quoted_extra_arguments_stay_in_one_token()
    {
        Assert.Equal(new[] { "-drive", @"file=C:\My Images\extra.qcow2" },
            QemuArgumentBuilder.TokenizeExtraArguments("-drive \"file=C:\\My Images\\extra.qcow2\""));
    }

    [Fact]
    public void Empty_extra_arguments_produce_nothing()
    {
        Assert.Empty(QemuArgumentBuilder.TokenizeExtraArguments(""));
        Assert.Empty(QemuArgumentBuilder.TokenizeExtraArguments("   "));
    }

    [Fact]
    public void Extra_arguments_are_appended_after_everything_the_builder_emits()
    {
        // The escape hatch has the last word, so it can override an earlier flag.
        var vm = Fixtures.Vm();
        vm.ExtraArguments = "-no-reboot";

        var args = Fixtures.Build(vm);

        Assert.Equal("-no-reboot", args[^1]);
    }

    [Fact]
    public void The_displayed_command_line_quotes_the_arguments_that_need_it()
    {
        var display = QemuArgumentBuilder.ToDisplayString(
            @"C:\Program Files\qemu\qemu-system-x86_64.exe",
            new[] { "-name", "Test", "-drive", @"file=C:\My Images\a.qcow2" });

        Assert.Contains(@"""C:\Program Files\qemu\qemu-system-x86_64.exe""", display);
        Assert.Contains(@"""file=C:\My Images\a.qcow2""", display);
        // A value with no space needs no quoting, and adding it would only add noise.
        Assert.Contains(" -name Test ", display);
    }
}
