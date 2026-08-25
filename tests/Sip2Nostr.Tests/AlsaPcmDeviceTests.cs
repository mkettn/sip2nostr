using Serilog;
using Sip2Nostr.Modem;
using Xunit;

namespace Sip2Nostr.Tests;

// Exercises the real P/Invoke path (open/set_params/readi/writei/close)
// against ALSA's built-in "null" PCM plugin, which always exists without
// real hardware, discards playback and returns synthesized silence on
// capture. This cannot verify behavior against a real modem's audio
// interface (see docs/receiving-modem-calls.md), but it does verify the
// interop signatures/marshaling are correct end-to-end against the real
// libasound.
public class AlsaPcmDeviceTests
{
    [Fact]
    public void OpenReadWriteClose_RoundTripsAgainstNullDevice()
    {
        using var device = new AlsaPcmDevice("null", Log.Logger);

        var frame = device.Read();
        Assert.Equal(AlsaPcmDevice.FrameSamples, frame.Length);

        device.Write(frame);
    }

    [Fact]
    public void Constructor_ThrowsForNonexistentDevice()
    {
        Assert.Throws<InvalidOperationException>(() => new AlsaPcmDevice("this-device-does-not-exist", Log.Logger));
    }
}
