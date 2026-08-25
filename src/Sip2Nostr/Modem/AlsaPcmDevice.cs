using System.Runtime.InteropServices;
using Serilog;

namespace Sip2Nostr.Modem;

// Minimal duplex raw-PCM wrapper around libasound (ALSA), P/Invoked
// directly. The only .NET package found for ALSA (Alsa.Net) only exposes a
// file-based play/record API; a live call needs continuous, low-latency
// full-duplex read/write, which libasound's own simplified PCM API
// (https://www.alsa-project.org/alsa-doc/alsa-lib/pcm.html) gives directly
// without pulling in a dependency whose API doesn't fit.
//
// 8 kHz mono 16-bit signed LE matches the rate G.711 (used everywhere else
// in this bridge) already runs at. Whether the configured device accepts
// that format/rate natively is device-specific - see docs/receiving-modem-calls.md.
internal sealed class AlsaPcmDevice : IDisposable
{
    public const int SampleRateHz = 8000;
    public const int FrameSamples = SampleRateHz / 50; // 20ms, matching one RTP packet

    private const uint Channels = 1;
    private const uint LatencyMicroseconds = 40_000; // ~2 frames of headroom against jitter

    private readonly ILogger _logger;
    private readonly string _deviceName;
    private nint _capture;
    private nint _playback;

    public AlsaPcmDevice(string deviceName, ILogger logger)
    {
        _deviceName = deviceName;
        _logger = logger;
        _capture = Open(deviceName, NativeMethods.SndPcmStreamCapture);
        try
        {
            _playback = Open(deviceName, NativeMethods.SndPcmStreamPlayback);
        }
        catch
        {
            // Otherwise the capture handle leaks (no finalizer, Dispose
            // never runs since construction never completed), and a later
            // retry on the same device hits -EBUSY against the leaked
            // handle - turning one transient open failure into a
            // permanently broken modem source.
            NativeMethods.snd_pcm_close(_capture);
            throw;
        }
    }

    private static nint Open(string deviceName, int stream)
    {
        var openResult = NativeMethods.snd_pcm_open(out var handle, deviceName, stream, 0);
        ThrowIfError(openResult, $"snd_pcm_open('{deviceName}', stream={stream})");

        var paramsResult = NativeMethods.snd_pcm_set_params(
            handle,
            NativeMethods.SndPcmFormatS16Le,
            NativeMethods.SndPcmAccessRwInterleaved,
            Channels,
            SampleRateHz,
            1,
            LatencyMicroseconds);
        if (paramsResult < 0)
        {
            NativeMethods.snd_pcm_close(handle);
            ThrowIfError(paramsResult, $"snd_pcm_set_params('{deviceName}', stream={stream})");
        }

        return handle;
    }

    // Blocking read of one frame's worth of mono 16-bit samples. Loops
    // until the full frame is transferred - snd_pcm_readi can return fewer
    // frames than requested (e.g. on signal interruption) even in blocking
    // mode, and treating a partial transfer as a complete one would present
    // whatever garbage is left in the tail of the buffer as audio. Returns
    // an all-zero (silence) frame on a recoverable error (xrun/suspend)
    // rather than throwing, since a single lost frame shouldn't tear down
    // the call.
    public short[] Read()
    {
        var buffer = new short[FrameSamples];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var received = 0;
            while (received < FrameSamples)
            {
                var offset = IntPtr.Add(handle.AddrOfPinnedObject(), received * sizeof(short));
                var framesRead = NativeMethods.snd_pcm_readi(_capture, offset, (nuint)(FrameSamples - received));
                if (framesRead < 0)
                {
                    RecoverOrThrow(_capture, (int)framesRead, "snd_pcm_readi");
                    return new short[FrameSamples];
                }

                received += (int)framesRead;
            }

            return buffer;
        }
        finally
        {
            handle.Free();
        }
    }

    // Loops until every sample is transferred - see Read() for why a
    // partial snd_pcm_writei return can't just be treated as "done"; here
    // that would silently drop the untransferred tail of the frame instead.
    public void Write(short[] samples)
    {
        var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            var sent = 0;
            while (sent < samples.Length)
            {
                var offset = IntPtr.Add(handle.AddrOfPinnedObject(), sent * sizeof(short));
                var framesWritten = NativeMethods.snd_pcm_writei(_playback, offset, (nuint)(samples.Length - sent));
                if (framesWritten < 0)
                {
                    RecoverOrThrow(_playback, (int)framesWritten, "snd_pcm_writei");
                    return;
                }

                sent += (int)framesWritten;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    // Unblocks a thread currently inside a blocking snd_pcm_readi on the
    // capture handle (the standard way to interrupt one from another
    // thread - a cancellation token can't reach into libasound) so shutdown
    // doesn't hang waiting for the capture loop to notice cancellation
    // between reads. Best-effort: the capture handle is about to be closed
    // regardless, so its return code isn't checked.
    public void DropCapture()
    {
        NativeMethods.snd_pcm_drop(_capture);
    }

    private void RecoverOrThrow(nint pcm, int error, string operation)
    {
        var recovered = NativeMethods.snd_pcm_recover(pcm, error, 1);
        if (recovered < 0)
        {
            throw new InvalidOperationException(
                $"ALSA {operation} on '{_deviceName}' failed and could not recover: {DescribeError(recovered)}.");
        }

        _logger.Debug("ALSA {Operation} on '{DeviceName}' recovered from {Error}.", operation, _deviceName, DescribeError(error));
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"ALSA {operation} failed: {DescribeError(result)}.");
        }
    }

    private static string DescribeError(int errnum) =>
        Marshal.PtrToStringAnsi(NativeMethods.snd_strerror(errnum)) ?? $"error {errnum}";

    public void Dispose()
    {
        if (_capture != 0)
        {
            NativeMethods.snd_pcm_close(_capture);
            _capture = 0;
        }

        if (_playback != 0)
        {
            NativeMethods.snd_pcm_close(_playback);
            _playback = 0;
        }
    }

    private static class NativeMethods
    {
        public const int SndPcmStreamCapture = 1;
        public const int SndPcmStreamPlayback = 0;
        public const int SndPcmFormatS16Le = 2;
        public const int SndPcmAccessRwInterleaved = 3;

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int snd_pcm_open(out nint pcm, string name, int stream, int mode);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int snd_pcm_close(nint pcm);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int snd_pcm_set_params(
            nint pcm,
            int format,
            int access,
            uint channels,
            uint rate,
            int softResample,
            uint latency);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint snd_pcm_readi(nint pcm, nint buffer, nuint size);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint snd_pcm_writei(nint pcm, nint buffer, nuint size);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int snd_pcm_recover(nint pcm, int err, int silent);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern int snd_pcm_drop(nint pcm);

        [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint snd_strerror(int errnum);
    }
}
