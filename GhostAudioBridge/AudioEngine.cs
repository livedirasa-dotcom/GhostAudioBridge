using System.Buffers.Binary;
using NAudio.Wave;

namespace GhostAudioBridge;

/// <summary>
/// MME/WaveIn MVP: captures a two-channel interface and mono microphone,
/// applies independent gain/pan, then fans the mix to monitor and virtual cable.
/// </summary>
internal sealed class AudioEngine : IDisposable
{
    private const int Rate = 48000;
    private readonly int _bufferFrames;
    private readonly int _inputBufferMilliseconds;
    private readonly int _outputLatencyMilliseconds;
    private int FloatSamplesPerBlock => _bufferFrames * 2;
    private static readonly WaveFormat FloatStereo = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    private static readonly WaveFormat PcmStereo = new(Rate, 16, 2);

    private readonly int _lineDevice;
    private readonly int _micDevice;
    private readonly int? _monitorDevice;
    private readonly int? _virtualDevice;
    private WaveInEvent? _lineIn;
    private WaveInEvent? _micIn;
    private WaveOutEvent? _monitorOut;
    private WaveOutEvent? _virtualOut;
    private BufferedWaveProvider? _line1Buffer;
    private BufferedWaveProvider? _line2Buffer;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _monitorBuffer;
    private BufferedWaveProvider? _virtualBuffer;
    private CancellationTokenSource? _cts;
    private Task? _mixTask;
    private int _disposed;

    private volatile float _line1Gain = 1f, _line2Gain = 1f, _micGain = 1f, _masterGain = .8f;
    private volatile float _line1Pan = -1f, _line2Pan = 1f;

    public float Line1Gain { get => _line1Gain; set => _line1Gain = value; }
    public float Line2Gain { get => _line2Gain; set => _line2Gain = value; }
    public float MicGain { get => _micGain; set => _micGain = value; }
    public float MasterGain { get => _masterGain; set => _masterGain = value; }
    public float Line1Pan { get => _line1Pan; set => _line1Pan = value; }
    public float Line2Pan { get => _line2Pan; set => _line2Pan = value; }

    public AudioEngine(int lineDevice, int micDevice, int? monitorDevice, int? virtualDevice, int bufferFrames)
    {
        _lineDevice = lineDevice;
        _micDevice = micDevice;
        _monitorDevice = monitorDevice;
        _virtualDevice = virtualDevice;
        _bufferFrames = Math.Clamp(bufferFrames, 64, 2048);

        // MME takes milliseconds rather than an exact ASIO sample count.
        _inputBufferMilliseconds = Math.Max(3,
            (int)Math.Ceiling(_bufferFrames * 1000.0 / Rate));
        _outputLatencyMilliseconds = Math.Clamp(
            (int)Math.Ceiling(_bufferFrames * 1000.0 / Rate * 8.0), 20, 80);
    }

    public void Start()
    {
        ThrowIfDisposed();
        _line1Buffer = NewFloatBuffer();
        _line2Buffer = NewFloatBuffer();
        _micBuffer = NewFloatBuffer();
        if (_monitorDevice.HasValue) _monitorBuffer = NewPcmBuffer();
        if (_virtualDevice.HasValue) _virtualBuffer = NewPcmBuffer();

        if (_monitorDevice.HasValue)
        {
            _monitorOut = new WaveOutEvent { DeviceNumber = _monitorDevice.Value, DesiredLatency = _outputLatencyMilliseconds, NumberOfBuffers = 3 };
            _monitorOut.Init(_monitorBuffer!);
            _monitorOut.Play();
        }
        if (_virtualDevice.HasValue)
        {
            _virtualOut = new WaveOutEvent { DeviceNumber = _virtualDevice.Value, DesiredLatency = _outputLatencyMilliseconds, NumberOfBuffers = 3 };
            _virtualOut.Init(_virtualBuffer!);
            _virtualOut.Play();
        }

        _lineIn = new WaveInEvent
        {
            DeviceNumber = _lineDevice,
            WaveFormat = new WaveFormat(Rate, 16, 2),
            BufferMilliseconds = _inputBufferMilliseconds,
            NumberOfBuffers = 3
        };
        _lineIn.DataAvailable += OnLineData;
        _lineIn.StartRecording();

        _micIn = new WaveInEvent
        {
            DeviceNumber = _micDevice,
            WaveFormat = new WaveFormat(Rate, 16, 1),
            BufferMilliseconds = _inputBufferMilliseconds,
            NumberOfBuffers = 3
        };
        _micIn.DataAvailable += OnMicData;
        _micIn.StartRecording();

        _cts = new CancellationTokenSource();
        _mixTask = Task.Run(() => MixLoop(_cts.Token));
    }

    private static BufferedWaveProvider NewFloatBuffer() => new(FloatStereo)
    {
        BufferDuration = TimeSpan.FromMilliseconds(100),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    private static BufferedWaveProvider NewPcmBuffer() => new(PcmStereo)
    {
        BufferDuration = TimeSpan.FromMilliseconds(100),
        DiscardOnBufferOverflow = true,
        ReadFully = true
    };

    private void OnLineData(object? sender, WaveInEventArgs e)
    {
        if (_line1Buffer is null || _line2Buffer is null) return;
        int frames = e.BytesRecorded / 4;
        if (frames <= 0) return;
        byte[] ch1 = new byte[frames * 8];
        byte[] ch2 = new byte[frames * 8];
        int p1 = 0, p2 = 0;
        float g1 = _line1Gain, g2 = _line2Gain;
        float pan1 = _line1Pan, pan2 = _line2Pan;

        for (int i = 0; i < frames; i++)
        {
            short s1 = BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i * 4, 2));
            short s2 = BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i * 4 + 2, 2));
            float v1 = s1 / 32768f * g1;
            float v2 = s2 / 32768f * g2;
            WriteFloatStereo(ch1, ref p1, v1 * PanLeft(pan1), v1 * PanRight(pan1));
            WriteFloatStereo(ch2, ref p2, v2 * PanLeft(pan2), v2 * PanRight(pan2));
        }
        _line1Buffer.AddSamples(ch1, 0, ch1.Length);
        _line2Buffer.AddSamples(ch2, 0, ch2.Length);
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_micBuffer is null) return;
        int samples = e.BytesRecorded / 2;
        if (samples <= 0) return;
        byte[] stereo = new byte[samples * 8];
        int p = 0;
        float gain = _micGain;
        for (int i = 0; i < samples; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i * 2, 2));
            float v = s / 32768f * gain * .7071f;
            WriteFloatStereo(stereo, ref p, v, v);
        }
        _micBuffer.AddSamples(stereo, 0, stereo.Length);
    }

    private static void WriteFloatStereo(byte[] dest, ref int offset, float left, float right)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(left));
        BinaryPrimitives.WriteInt32LittleEndian(dest.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(right));
        offset += 8;
    }

    private static float PanLeft(float pan) => pan > 0 ? 1f - pan : 1f;
    private static float PanRight(float pan) => pan < 0 ? 1f + pan : 1f;

    private async Task MixLoop(CancellationToken token)
    {
        var line1 = _line1Buffer!.ToSampleProvider();
        var line2 = _line2Buffer!.ToSampleProvider();
        var mic = _micBuffer!.ToSampleProvider();
        int blockSamples = FloatSamplesPerBlock;
        float[] a = new float[blockSamples];
        float[] b = new float[blockSamples];
        float[] c = new float[blockSamples];
        byte[] pcm = new byte[blockSamples * 2];
        int blockDelayMs = Math.Max(1, (int)Math.Round(_bufferFrames * 1000.0 / Rate));

        while (!token.IsCancellationRequested)
        {
            Array.Clear(a);
            Array.Clear(b);
            Array.Clear(c);
            line1.Read(a, 0, a.Length);
            line2.Read(b, 0, b.Length);
            mic.Read(c, 0, c.Length);
            float master = _masterGain;

            for (int i = 0; i < blockSamples; i++)
            {
                float mixed = Math.Clamp((a[i] + b[i] + c[i]) * master, -1f, 1f);
                short sample = (short)Math.Clamp((int)MathF.Round(mixed * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
            }

            _monitorBuffer?.AddSamples(pcm, 0, pcm.Length);
            _virtualBuffer?.AddSamples(pcm, 0, pcm.Length);
            try { await Task.Delay(blockDelayMs, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts?.Cancel(); _mixTask?.Wait(500); } catch { }
        try { _lineIn?.StopRecording(); } catch { }
        try { _micIn?.StopRecording(); } catch { }
        _lineIn?.Dispose();
        _micIn?.Dispose();
        _monitorOut?.Stop();
        _virtualOut?.Stop();
        _monitorOut?.Dispose();
        _virtualOut?.Dispose();
        _cts?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AudioEngine));
    }
}
