using System.Runtime.InteropServices;
using WinQEMU.Core.Input;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Text typed into a guest as key positions, with the French and US layouts that ship with every Windows.
/// A layout this PC did not have is unloaded afterwards, so the user's input languages stay as they were.
/// </summary>
// Never beside another class that loads keyboard layouts: one unloading a layout under the other failed it.
[Collection("Keyboard layouts")]
public sealed class KeyboardTextTests : IDisposable
{
    private readonly List<IntPtr> _loaded = new();

    private IntPtr Layout(string id)
    {
        var before = Layouts();
        var layout = LoadKeyboardLayout(id, 0x80); // KLF_NOTELLSHELL
        Assert.NotEqual(IntPtr.Zero, layout);
        if (!before.Contains(layout))
            _loaded.Add(layout);
        return layout;
    }

    public void Dispose()
    {
        foreach (var layout in _loaded)
            UnloadKeyboardLayout(layout);
    }

    private static (int, bool)[] Keys(string text, IntPtr layout) =>
        KeyboardText.ToKeystrokes(text, layout).SelectMany(k => k).ToArray();

    [Fact]
    public void A_letter_is_its_key_where_the_layout_puts_it()
    {
        Assert.Equal(new[] { (0x1E, true), (0x1E, false) }, Keys("a", Layout("00000409")));
        // AZERTY: A sits where QWERTY has Q.
        Assert.Equal(new[] { (0x10, true), (0x10, false) }, Keys("a", Layout("0000040C")));
    }

    [Fact]
    public void Shift_is_held_around_a_capital()
    {
        Assert.Equal(new[] { (0x2A, true), (0x1E, true), (0x1E, false), (0x2A, false) }, Keys("A", Layout("00000409")));
    }

    [Fact]
    public void A_character_behind_AltGr_is_typed_with_the_right_Alt()
    {
        var french = Layout("0000040C");
        // | is AltGr+6 and \ AltGr+8 on a French keyboard.
        Assert.Equal(new[] { (KeyboardText.RightAlt, true), (0x07, true), (0x07, false), (KeyboardText.RightAlt, false) }, Keys("|", french));
        Assert.Equal(new[] { (KeyboardText.RightAlt, true), (0x09, true), (0x09, false), (KeyboardText.RightAlt, false) }, Keys("\\", french));
    }

    [Fact]
    public void A_dead_key_is_followed_by_a_space()
    {
        // ~ is AltGr+2, a dead key, on a French keyboard.
        var keys = Keys("~", Layout("0000040C"));
        Assert.Equal((KeyboardText.Space, true), keys[^2]);
        Assert.Equal((KeyboardText.Space, false), keys[^1]);
    }

    [Fact]
    public void A_letter_no_key_carries_is_its_accents_dead_key_then_the_letter()
    {
        var french = Layout("0000040C");
        // ê: the ^ key (right of P), then E, with no space between them.
        Assert.Equal(new[] { (0x1A, true), (0x1A, false), (0x12, true), (0x12, false) }, Keys("ê", french));
        // ñ: AltGr+2 (the dead ~), then N.
        Assert.Equal(new[] { (KeyboardText.RightAlt, true), (0x03, true), (0x03, false), (KeyboardText.RightAlt, false), (0x31, true), (0x31, false) },
            Keys("ñ", french));
        // A French keyboard has no dead acute accent.
        Assert.Throws<ArgumentException>(() => KeyboardText.ToKeystrokes("É", french));
    }

    [Fact]
    public void A_line_break_is_Enter_and_each_character_comes_apart()
    {
        var strokes = KeyboardText.ToKeystrokes("ls\n", Layout("00000409"));
        Assert.Equal(3, strokes.Count);
        Assert.Equal(new[] { (KeyboardText.Enter, true), (KeyboardText.Enter, false) }, strokes[2]);
    }

    [Fact]
    public void A_character_without_a_key_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => KeyboardText.ToKeystrokes("日", Layout("00000409")));
        Assert.Contains("日", error.Message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string id, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnloadKeyboardLayout(IntPtr layout);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, IntPtr[]? layouts);

    private static HashSet<IntPtr> Layouts()
    {
        var count = GetKeyboardLayoutList(0, null);
        var layouts = new IntPtr[count];
        GetKeyboardLayoutList(count, layouts);
        return layouts.ToHashSet();
    }
}
