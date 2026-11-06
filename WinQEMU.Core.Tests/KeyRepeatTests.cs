using WinQEMU.Core.Input;
using Xunit;

namespace WinQEMU.Core.Tests;

public class KeyRepeatTests
{
    [Theory]
    [InlineData(0x50 | 0x80)] // Down arrow (extended): archinstall's menus
    [InlineData(0x0E)]        // Backspace
    [InlineData(0x1E)]        // A
    [InlineData(0x39)]        // Space
    public void Ordinary_keys_repeat(int scancode) => Assert.True(KeyRepeat.Repeats(scancode));

    [Theory]
    [InlineData(0x2A)]                    // Left Shift
    [InlineData(AltGrFilter.LeftCtrl)]
    [InlineData(AltGrFilter.RightAlt)]
    [InlineData(0x5B | 0x80)]             // Left Windows
    [InlineData(0x3A)]                    // Caps Lock
    public void Modifiers_and_locks_do_not(int scancode) => Assert.False(KeyRepeat.Repeats(scancode));
}
