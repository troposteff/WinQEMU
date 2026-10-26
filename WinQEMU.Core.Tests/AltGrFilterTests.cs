using WinQEMU.Core.Input;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Windows reports AltGr as a phantom Left Ctrl followed by Right Alt. Forwarded as is, the guest sees
/// Ctrl+AltGr and no AltGr character (| [ @ ...) ever appears.
/// </summary>
public class AltGrFilterTests
{
    private const int Ctrl = AltGrFilter.LeftCtrl;
    private const int AltGr = AltGrFilter.RightAlt;
    private const int Key6 = 0x07;   // "-" / "6" row on AZERTY: AltGr gives "|"
    private const int KeyC = 0x2E;

    private static List<(int, bool)> Run(AltGrFilter filter, params (int, bool)[] edges) =>
        edges.SelectMany(edge => filter.Process(edge.Item1, edge.Item2)).ToList();

    [Fact]
    public void AltGr_reaches_the_guest_without_its_phantom_ctrl()
    {
        // The exact order Windows produces for AltGr+6 on AZERTY.
        var sent = Run(new AltGrFilter(),
            (Ctrl, true), (AltGr, true), (Key6, true), (Key6, false), (Ctrl, false), (AltGr, false));

        Assert.Equal(new List<(int, bool)> { (AltGr, true), (Key6, true), (Key6, false), (AltGr, false) }, sent);
    }

    [Fact]
    public void Phantom_ctrl_release_is_dropped_whichever_comes_first()
    {
        var sent = Run(new AltGrFilter(),
            (Ctrl, true), (AltGr, true), (Key6, true), (Key6, false), (AltGr, false), (Ctrl, false));

        Assert.DoesNotContain((Ctrl, false), sent);
        Assert.DoesNotContain((Ctrl, true), sent);
    }

    [Fact]
    public void A_real_ctrl_shortcut_is_delivered_in_order()
    {
        var sent = Run(new AltGrFilter(),
            (Ctrl, true), (KeyC, true), (KeyC, false), (Ctrl, false));

        Assert.Equal(new List<(int, bool)> { (Ctrl, true), (KeyC, true), (KeyC, false), (Ctrl, false) }, sent);
    }

    [Fact]
    public void A_ctrl_held_alone_is_released_by_the_timeout()
    {
        // Ctrl held on its own, e.g. for a Ctrl+click: nothing follows, so the caller's timer flushes it.
        var filter = new AltGrFilter();

        Assert.Empty(filter.Process(Ctrl, true));
        Assert.True(filter.HasPending);
        Assert.Equal(new[] { (Ctrl, true) }, filter.Flush());
        Assert.False(filter.HasPending);
        Assert.Equal(new[] { (Ctrl, false) }, filter.Process(Ctrl, false));
    }

    [Fact]
    public void A_quick_ctrl_tap_is_not_lost()
    {
        var sent = Run(new AltGrFilter(), (Ctrl, true), (Ctrl, false));

        Assert.Equal(new List<(int, bool)> { (Ctrl, true), (Ctrl, false) }, sent);
    }

    [Fact]
    public void Right_alt_alone_passes_through()
    {
        var sent = Run(new AltGrFilter(), (AltGr, true), (AltGr, false));

        Assert.Equal(new List<(int, bool)> { (AltGr, true), (AltGr, false) }, sent);
    }

    [Fact]
    public void Reset_forgets_a_swallowed_phantom()
    {
        // After the console loses focus mid-AltGr, a later real Ctrl release must not be swallowed.
        var filter = new AltGrFilter();
        Run(filter, (Ctrl, true), (AltGr, true));
        filter.Reset();

        Assert.Equal(new[] { (Ctrl, false) }, filter.Process(Ctrl, false));
    }
}
