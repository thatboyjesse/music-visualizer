using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace MusicVisualizer.Audio;

public enum AudioSource { None, File, Loopback }

/// <summary>
/// Plays an audio file or captures system audio, and keeps the most recent
/// mono samples in a ring buffer for the visualizer to analyse.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int FftSize = 2048;

    private readonly float[] _ring = new float[FftSize];
    private readonly object _lock = new();
    private int _ringPos;
    private long _lastWrite;
    private float[] _scratch = Array.Empty<float>();

    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private WasapiLoopbackCapture? _loop;

    public event EventHandler? StateChanged;

    public AudioSource Source { get; private set; }
    public string? TrackName { get; private set; }
    public int SampleRate { get; private set; } = 48000;

    public bool IsPlaying => Source switch
    {
        AudioSource.Loopback => true,
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
            output.Init(new TapSampleProvider(reader, AddSamples));
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

        lock (_lock)
        {
            Array.Clear(_ring);
            _ringPos = 0;
        }

        Source = AudioSource.None;
        TrackName = null;
        RaiseStateChanged();
    }

    /// <summary>Copies the latest <see cref="FftSize"/> samples, oldest first.
    /// Returns silence if no audio has arrived recently (paused, nothing playing).</summary>
    public void GetSamples(float[] dest)
    {
        lock (_lock)
        {
            double age = (Stopwatch.GetTimestamp() - _lastWrite) / (double)Stopwatch.Frequency;
            if (age > 0.15)
            {
                Array.Clear(dest);
                return;
            }
            for (int i = 0; i < FftSize; i++)
                dest[i] = _ring[(_ringPos + i) % FftSize];
        }
    }

    private void AddSamples(float[] buffer, int offset, int count, int channels)
    {
        lock (_lock)
        {
            int end = offset + count;
            for (int i = offset; i + channels <= end; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += buffer[i + c];
                _ring[_ringPos] = sum / channels;
                _ringPos = (_ringPos + 1) % FftSize;
            }
            _lastWrite = Stopwatch.GetTimestamp();
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
            AddSamples(_scratch, 0, n, fmt.Channels);
        }
        else if (fmt.BitsPerSample == 16)
        {
            int n = e.BytesRecorded / 2;
            if (_scratch.Length < n) _scratch = new float[n];
            for (int i = 0; i < n; i++)
                _scratch[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            AddSamples(_scratch, 0, n, fmt.Channels);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();

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
