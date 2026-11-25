using System.Runtime.InteropServices;

namespace MusicVisualizer.Audio;

/// <summary>
/// Captures the audio of one app (a process and its child processes) with the Windows
/// "process loopback" API (Windows 10 build 20348 / Windows 11 and later). NAudio 2.2 has
/// the plumbing for this but doesn't expose it, so this talks to WASAPI directly.
///
/// Steps: ActivateAudioInterfaceAsync("VAD\Process_Loopback", IAudioClient, params naming the
/// process) → IAudioClient.Initialize(loopback, our own format) → poll IAudioCaptureClient.
/// The format is always 48 kHz stereo float, because process loopback has no mix format to ask for.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly Action<float[], int, int, int> _sink;
    private readonly Thread _thread;
    private volatile bool _stop;

    public int ProcessId { get; }

    /// <summary>False once capturing has ended (the app exited or the audio device changed).</summary>
    public bool IsRunning => _thread.IsAlive;

    private ProcessAudioCapture(int pid, IAudioClient client, IAudioCaptureClient capture, Action<float[], int, int, int> sink)
    {
        ProcessId = pid;
        _client = client;
        _capture = capture;
        _sink = sink;
        _thread = new Thread(Run) { IsBackground = true, Name = $"App capture {pid}" };
    }

    /// <summary>Starts capturing <paramref name="processId"/> and its children.
    /// <paramref name="sink"/> receives (interleaved float buffer, offset, count, channels) on a background thread.</summary>
    public static async Task<ProcessAudioCapture> StartAsync(int processId, Action<float[], int, int, int> sink)
    {
        var client = await ActivateAsync(processId).ConfigureAwait(false);

        var format = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 32,
            nBlockAlign = Channels * 4,
            nAvgBytesPerSec = SampleRate * Channels * 4,
        };
        const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        Check(client.Initialize(0 /* shared */, flags, 2_000_000 /* 200 ms */, 0, ref format, IntPtr.Zero));

        var iid = typeof(IAudioCaptureClient).GUID;
        Check(client.GetService(ref iid, out object svc));
        var capture = (IAudioCaptureClient)svc;

        var result = new ProcessAudioCapture(processId, client, capture, sink);
        Check(client.Start());
        result._thread.Start();
        return result;
    }

    /// <summary>
    /// The capture thread. It also stops and releases the COM objects: they were created on a
    /// thread-pool (MTA) thread and can't be called from the UI thread (that fails with E_NOINTERFACE).
    /// </summary>
    private void Run()
    {
        var buffer = new float[SampleRate / 10 * Channels];
        try
        {
            while (!_stop)
            {
                while (!_stop && _capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint bufFlags, out _, out _) != 0) break;
                    int samples = (int)frames * Channels;
                    if (buffer.Length < samples) buffer = new float[samples];
                    if ((bufFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buffer, 0, samples);
                    else Marshal.Copy(data, buffer, 0, samples);
                    _capture.ReleaseBuffer(frames);
                    if (samples > 0) _sink(buffer, 0, samples, Channels);
                }
                Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The process went away or the audio device changed; the engine starts a new capture if needed.
        }
        finally
        {
            try { _client.Stop(); } catch (Exception ex) when (ex is COMException or InvalidCastException) { }
            Marshal.ReleaseComObject(_capture);
            Marshal.ReleaseComObject(_client);
        }
    }

    /// <summary>Stops capturing. Safe to call from any thread.</summary>
    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }

    // ------------------------------------------------------------------ activation

    private static Task<IAudioClient> ActivateAsync(int processId)
    {
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blob = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(activation, blob, false);

        var variant = new PROPVARIANT { vt = 65 /* VT_BLOB */, cbSize = (uint)size, pBlobData = blob };
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
        Marshal.StructureToPtr(variant, pVariant, false);

        var handler = new CompletionHandler();
        try
        {
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pVariant, handler, out _));
        }
        catch
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(blob);
            throw;
        }
        return handler.Task.ContinueWith(t =>
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(blob);
            return t.Result;
        }, TaskScheduler.Default);
    }

    [ComVisible(true)]
    public sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Task => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                Check(operation.GetActivateResult(out int hr, out object iface));
                Check(hr);
                _tcs.SetResult((IAudioClient)iface);
            }
            catch (Exception ex)
            {
                _tcs.SetException(ex);
            }
            return 0;
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ------------------------------------------------------------------ interop
    // (The COM types are public: COM only calls into interfaces it can see.)

    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marker interface: tells COM our handler can be called from any thread.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
