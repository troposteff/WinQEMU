using System.Text;
using WinQEMU.Core.Qemu;

namespace WinQEMU.Core.Tests;

/// <summary>
/// Reading the guest resolution out of a screendump. This is the only place QEMU states the framebuffer
/// size outright, and the console's letterboxing is built on it: a size read wrong distorts the image
/// just as surely as not letterboxing at all.
/// </summary>
public class PpmHeaderTests
{
    private static byte[] Header(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void The_dimensions_are_read_from_a_normal_header()
    {
        // Exactly what QEMU 11.1.0 wrote for a 720x400 text mode, measured.
        Assert.True(VmSession.TryParsePpmSize(Header("P6\n720 400\n255\n\xff\xff\xff"), out var w, out var h));
        Assert.Equal(720, w);
        Assert.Equal(400, h);
    }

    [Theory]
    [InlineData("P6\n1920 1080\n255\n")]
    [InlineData("P6 1920 1080 255 ")]
    [InlineData("P6\r\n1920\r\n1080\r\n255\r\n")]
    [InlineData("P6\n\n  1920   1080  \n255\n")]
    public void Any_whitespace_between_the_fields_is_accepted(string header)
    {
        // The format allows any whitespace run, and different writers use different ones.
        Assert.True(VmSession.TryParsePpmSize(Header(header), out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void A_comment_between_the_fields_is_skipped()
    {
        Assert.True(VmSession.TryParsePpmSize(Header("P6\n# written by something\n640 480\n255\n"), out var w, out var h));
        Assert.Equal(640, w);
        Assert.Equal(480, h);
    }

    [Fact]
    public void A_format_other_than_binary_ppm_is_refused()
    {
        // P3 is the ASCII variant and PNG is not a PPM at all; neither is what screendump produces.
        Assert.False(VmSession.TryParsePpmSize(Header("P3\n640 480\n255\n"), out _, out _));
        Assert.False(VmSession.TryParsePpmSize(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, out _, out _));
    }

    [Fact]
    public void A_truncated_header_is_refused_rather_than_guessed()
    {
        // Only the first bytes of the file are read, so a number may be cut in half: taking "10" from a
        // truncated "1080" would letterbox to a resolution the guest never had.
        Assert.False(VmSession.TryParsePpmSize(Header("P6\n1920 10"), out _, out _));
        Assert.False(VmSession.TryParsePpmSize(Header("P6\n192"), out _, out _));
        Assert.False(VmSession.TryParsePpmSize(Header("P6\n"), out _, out _));
    }

    [Fact]
    public void An_empty_buffer_is_refused()
    {
        Assert.False(VmSession.TryParsePpmSize(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void A_zero_dimension_is_refused()
    {
        // Dividing by it would be the next thing that happened.
        Assert.False(VmSession.TryParsePpmSize(Header("P6\n0 480\n255\n"), out _, out _));
        Assert.False(VmSession.TryParsePpmSize(Header("P6\n640 0\n255\n"), out _, out _));
    }

    [Fact]
    public void Junk_where_a_dimension_belongs_is_refused()
    {
        Assert.False(VmSession.TryParsePpmSize(Header("P6\nlarge 480\n255\n"), out _, out _));
    }
}
