using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace MusicVisualizer.Audio;

public enum AudioSource { None, File, Loopback, Apps }

/// <summary>
/// Plays an audio file, captures all system audio, or captures only chosen apps, and keeps
/// the most recent mono samples for the visualizer to analyse.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int FftSize = 2048;

    private readonly object _lock = new();
    private readonly SampleRing _main = new();
    private float[] _scratch = Array.Empty<float>();

    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private WasapiLoopbackCapture? _loop;

    // App capture: one capture (and ring buffer) per top-level process of each chosen app.
    private readonly Dictionary<int, (ProcessAudioCapture? Capture, SampleRing Ring)> _apps = new();
    private string[] _appNames = Array.Empty<string>();
    private System.Threading.Timer? _appTimer;
    private int _appGeneration;

    public event EventHandler? StateChanged;

    public AudioSource Source { get; private set; }
    public string? TrackName { get; private set; }
    public int SampleRate { get; private set; } = 48000;

    /// <summary>Last error from starting an app capture (e.g. Windows too old), for the settings window.</summary>
    public string? AppCaptureError { get; private set; }

    public bool IsPlaying => Source switch
    {
        AudioSource.Loopback or AudioSource.Apps => true,
        AudioSource.File => _out?.PlaybackState == PlaybackState.Playing,
        _ => false,
    };

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public void OpenFile(string path)
    {
        Stop();
        var reader = new AudioFileReader(path);
        try
        {
            var output = new WaveOutEvent { DesiredLatency = 100 };
            output.Init(new TapSampleProvider(reader, (b, o, c, ch) => _main.Add(b, o, c, ch)));
            output.PlaybackStopped += OnPlaybackStopped;
            _reader = reader;
            _out = output;
            SampleRate = reader.WaveFormat.SampleRate;
            Source = AudioSource.File;
            TrackName = Path.GetFileNameWithoutExtension(path);
            output.Play();
        }
        catch
        {
            reader.Dispose();
            throw;
        }
        RaiseStateChanged();
    }

    /// <summary>Captures everything the default output device plays.</summary>
    public void StartLoopback()
    {
        Stop();
        var capture = new WasapiLoopbackCapture();
        capture.DataAvailable += OnLoopbackData;
        _loop = capture;
        SampleRate = capture.WaveFormat.SampleRate;
        Source = AudioSource.Loopback;
        TrackName = "System audio";
        capture.StartRecording();
        RaiseStateChanged();
    }

    /// <summary>
    /// Captures only the named apps (process names without ".exe"). Apps that aren't running yet
    /// are picked up when they start: the process list is re-checked every 2 seconds.
    /// </summary>
    public void StartApps(IReadOnlyCollection<string> appNames)
    {
        Stop();
        _appNames = appNames.ToArray();
        SampleRate = ProcessAudioCapture.SampleRate;
        Source = AudioSource.Apps;
        TrackName = appNames.Count == 1 ? appNames.First() : $"{appNames.Count} apps";
        AppCaptureError = null;
        int generation = ++_appGeneration;
        _appTimer = new System.Threading.Timer(_ => RefreshApps(generation), null, 0, 2000);
        RaiseStateChanged();
    }

    private void RefreshApps(int generation)
    {
        var wanted = new HashSet<int>();
        foreach (var name in _appNames)
            foreach (int pid in AudioApps.RootProcessIds(name)) wanted.Add(pid);

        lock (_lock)
        {
            if (generation != _appGeneration) return; // Stop() or a newer StartApps() happened

            // Drop apps that exited and captures that ended; ended ones restart below.
            foreach (var pid in _apps.Keys.Where(p => !wanted.Contains(p) || _apps[p].Capture is { IsRunning: false }).ToList())
            {
                _apps[pid].Capture?.Dispose();
                _apps.Remove(pid);
            }
            foreach (int pid in wanted.Where(p => !_apps.ContainsKey(p)))
            {
                var ring = new SampleRing();
                _apps[pid] = (null, ring); // reserve the slot so we don't start twice
                _ = StartAppCapture(pid, ring, generation);
            }
        }
    }

    private async Task StartAppCapture(int pid, SampleRing ring, int generation)
    {
        try
        {
            var capture = await ProcessAudioCapture.StartAsync(pid, ring.Add);
            lock (_lock)
            {
                if (generation == _appGeneration && _apps.ContainsKey(pid))
                {
                    _apps[pid] = (capture, ring);
                    return;
                }
            }
            capture.Dispose(); // stopped while we were starting
        }
        catch (Exception ex)
        {
            AppCaptureError = ex.Message;
            lock (_lock)
            {
                if (generation == _appGeneration) _apps.Remove(pid); // try again on the next refresh
            }
        }
    }

    public void TogglePlay()
    {
        if (_out is null || _reader is null) return;
        if (_out.PlaybackState == PlaybackState.Playing)
        {
            _out.Pause();
        }
        else
        {
            if (_reader.Position >= _reader.Length) _reader.Position = 0;
            _out.Play();
        }
        RaiseStateChanged();
    }

    public void Seek(double fraction)
    {
        if (_reader is null) return;
        fraction = Math.Clamp(fraction, 0, 1);
        _reader.CurrentTime = TimeSpan.FromSeconds(_reader.TotalTime.TotalSeconds * fraction);
    }

    public void Stop()
    {
        if (_out is not null)
        {
            _out.PlaybackStopped -= OnPlaybackStopped;
            _out.Stop();
            _out.Dispose();
            _out = null;
        }
        _reader?.Dispose();
        _reader = null;

        if (_loop is not null)
        {
            _loop.DataAvailable -= OnLoopbackData;
            _loop.StopRecording();
            _loop.Dispose();
            _loop = null;
        }

        _appTimer?.Dispose();
        _appTimer = null;
        lock (_lock)
        {
            _appGeneration++;
            foreach (var (capture, _) in _apps.Values) capture?.Dispose();
            _apps.Clear();
        }
        _main.Clear();

        Source = AudioSource.None;
        TrackName = null;
        RaiseStateChanged();
    }

    /// <summary>
    /// Seconds since any source last delivered audible sound (not just silence). Very large when
    /// nothing is being captured. Used to fall back to the normal wallpaper when nothing plays.
    /// </summary>
    public double SecondsSinceSound
    {
        get
        {
            double best = _main.SecondsSinceSound;
            lock (_lock)
            {
                foreach (var (_, ring) in _apps.Values) best = Math.Min(best, ring.SecondsSinceSound);
            }
            return best;
        }
    }

    /// <summary>Copies the latest <see cref="FftSize"/> samples, oldest first, summing all sources.
    /// A source that hasn't delivered audio recently (paused, silent app) contributes silence.</summary>
    public void GetSamples(float[] dest)
    {
        Array.Clear(dest);
        _main.AddTo(dest);
        lock (_lock)
        {
            foreach (var (_, ring) in _apps.Values) ring.AddTo(dest);
        }
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        var fmt = _loop?.WaveFormat;
        if (fmt is null || e.BytesRecorded == 0) return;

        if (fmt.BitsPerSample == 32)
        {
            int n = e.BytesRecorded / 4;
            if (_scratch.Length < n) _scratch = new float[n];
            Buffer.BlockCopy(e.Buffer, 0, _scratch, 0, n * 4);
            _main.Add(_scratch, 0, n, fmt.Channels);
        }
        else if (fmt.BitsPerSample == 16)
        {
            int n = e.BytesRecorded / 2;
            if (_scratch.Length < n) _scratch = new float[n];
            for (int i = 0; i < n; i++)
                _scratch[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            _main.Add(_scratch, 0, n, fmt.Channels);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();

    /// <summary>The latest <see cref="FftSize"/> mono samples from one source. Thread-safe.</summary>
    private sealed class SampleRing
    {
        private readonly float[] _ring = new float[FftSize];
        private int _pos;
        private long _lastWrite;
        private long _lastSound;

        /// <summary>Peak level below which a block counts as silence (about −66 dBFS).</summary>
        private const float Audible = 0.0005f;

        public double SecondsSinceSound
        {
            get
            {
                lock (_ring)
                    return _lastSound == 0 ? double.MaxValue : (Stopwatch.GetTimestamp() - _lastSound) / (double)Stopwatch.Frequency;
            }
        }

        public void Add(float[] buffer, int offset, int count, int channels)
        {
            lock (_ring)
            {
                int end = offset + count;
                float peak = 0;
                for (int i = offset; i + channels <= end; i += channels)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += buffer[i + c];
                    float v = sum / channels;
                    _ring[_pos] = v;
                    _pos = (_pos + 1) % FftSize;
                    if (Math.Abs(v) > peak) peak = Math.Abs(v);
                }
                _lastWrite = Stopwatch.GetTimestamp();
                if (peak > Audible) _lastSound = _lastWrite;
            }
        }

        public void AddTo(float[] dest)
        {
            lock (_ring)
            {
                double age = (Stopwatch.GetTimestamp() - _lastWrite) / (double)Stopwatch.Frequency;
                if (age > 0.15) return;
                for (int i = 0; i < FftSize; i++)
                    dest[i] += _ring[(_pos + i) % FftSize];
            }
        }

        public void Clear()
        {
            lock (_ring)
            {
                Array.Clear(_ring);
                _pos = 0;
                _lastWrite = 0;
                _lastSound = 0;
            }
        }
    }

    /// <summary>Passes samples through unchanged while copying them to a callback.</summary>
    private sealed class TapSampleProvider(ISampleProvider source, Action<float[], int, int, int> tap) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (read > 0) tap(buffer, offset, read, WaveFormat.Channels);
            return read;
        }
    }
}
