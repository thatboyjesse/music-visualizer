using MusicVisualizer.Audio;
using NAudio.Dsp;

namespace MusicVisualizer.Visualizer;

/// <summary>Turns raw samples into smoothed, log-spaced frequency bands.</summary>
public sealed class SpectrumAnalyzer
{
    private const int N = AudioEngine.FftSize;
    private const int Log2N = 11; // 2^11 = 2048
    private const double MinFreq = 40, MaxFreq = 16000;
    private const float MinDb = -72f, MaxDb = -12f;

    private readonly Complex[] _fft = new Complex[N];
    private readonly float[] _window = new float[N];
    private readonly int[] _bandLo, _bandHi;
    private readonly float[] _bandFreq, _bandTilt, _peakHold;
    private int _sampleRate;

    public float[] Bands { get; }
    public float[] Peaks { get; }
    public float[] Samples { get; } = new float[N];
    public float Bass { get; private set; }
    public float Level { get; private set; }

    public SpectrumAnalyzer(int bandCount)
    {
        Bands = new float[bandCount];
        Peaks = new float[bandCount];
        _peakHold = new float[bandCount];
        _bandLo = new int[bandCount];
        _bandHi = new int[bandCount];
        _bandFreq = new float[bandCount];
        _bandTilt = new float[bandCount];

        for (int i = 0; i < N; i++)
            _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (N - 1)));
    }

    private void BuildBands(int sampleRate)
    {
        _sampleRate = sampleRate;
        int half = N / 2;
        int count = Bands.Length;
        double ratio = MaxFreq / MinFreq;

        for (int b = 0; b < count; b++)
        {
            double f0 = MinFreq * Math.Pow(ratio, (double)b / count);
            double f1 = MinFreq * Math.Pow(ratio, (double)(b + 1) / count);
            int lo = Math.Clamp((int)Math.Floor(f0 * N / sampleRate), 1, half - 1);
            int hi = Math.Clamp((int)Math.Ceiling(f1 * N / sampleRate), lo + 1, half);
            _bandLo[b] = lo;
            _bandHi[b] = hi;

            double center = Math.Sqrt(f0 * f1);
            _bandFreq[b] = (float)center;
            // Music energy falls off with frequency; tilt so highs are visible.
            _bandTilt[b] = (float)(3.0 * Math.Log2(Math.Max(center, 100) / 100.0));
        }
    }

    public void Update(AudioEngine? engine, double dt)
    {
        int sr = engine?.SampleRate ?? 48000;
        if (sr != _sampleRate) BuildBands(sr);

        if (engine is not null) engine.GetSamples(Samples);
        else Array.Clear(Samples);

        double sumSq = 0;
        for (int i = 0; i < N; i++)
        {
            float s = Samples[i];
            sumSq += s * s;
            _fft[i].X = s * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, Log2N, _fft);

        float rise = (float)(1 - Math.Exp(-dt * 35));
        float fall = (float)(1 - Math.Exp(-dt * 7));
        float bassSum = 0;
        int bassCount = 0;

        for (int b = 0; b < Bands.Length; b++)
        {
            float max = 0;
            for (int k = _bandLo[b]; k < _bandHi[b]; k++)
            {
                float re = _fft[k].X, im = _fft[k].Y;
                float m = re * re + im * im;
                if (m > max) max = m;
            }

            float db = 10f * MathF.Log10(max + 1e-12f) + _bandTilt[b];
            float target = Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0f, 1f);

            float cur = Bands[b];
            cur += (target - cur) * (target > cur ? rise : fall);
            Bands[b] = cur;

            if (cur >= Peaks[b])
            {
                Peaks[b] = cur;
                _peakHold[b] = 0.35f;
            }
            else if ((_peakHold[b] -= (float)dt) <= 0)
            {
                Peaks[b] = Math.Max(cur, Peaks[b] - (float)dt * 0.5f);
            }

            if (_bandFreq[b] < 150)
            {
                bassSum += cur;
                bassCount++;
            }
        }

        float bassTarget = bassCount > 0 ? bassSum / bassCount : 0;
        Bass += (bassTarget - Bass) * (bassTarget > Bass ? rise : fall);
        Level = (float)Math.Sqrt(sumSq / N);
    }
}
