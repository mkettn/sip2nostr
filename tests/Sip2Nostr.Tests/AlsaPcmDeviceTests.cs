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
        // P/Invokes libasound.so.2 directly - only present on Linux. Passing
        // trivially here (rather than a hard failure) keeps `dotnet test` on
        // macOS/Windows from failing on a library this project never ships
        // for those platforms in the first place.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var device = new AlsaPcmDevice("null", Log.Logger);

        var frame = device.Read();
        Assert.Equal(AlsaPcmDevice.FrameSamples, frame.Length);

        device.Write(frame);
    }

    [Fact]
    public void Constructor_ThrowsForNonexistentDevice()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => new AlsaPcmDevice("this-device-does-not-exist", Log.Logger));
    }

    [Fact]
    public void DropCapture_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var device = new AlsaPcmDevice("null", Log.Logger);

        device.DropCapture();
    }
}
