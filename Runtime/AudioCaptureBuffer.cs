using System;
using System.IO;
using System.Text;
using System.Threading;

namespace QamelCapture
{
    internal sealed class CapturedAudio
    {
        public double StartT;
        public int SampleRate;
        public int Channels;
        public short[] Samples;
        public byte[] Wav()
        {
            using (var stream = new MemoryStream(44 + Samples.Length * 2))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + Samples.Length * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
                writer.Write((short)Channels); writer.Write(SampleRate); writer.Write(SampleRate * Channels * 2);
                writer.Write((short)(Channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(Samples.Length * 2);
                foreach (short value in Samples) writer.Write(value);
                return stream.ToArray();
            }
        }
        public string Metadata()
        {
            return new QamelJson().Begin().Int("version", 1).Num("start_t", StartT)
                .Int("sample_rate", SampleRate).Int("channels", Channels)
                .Int("sample_frames", Samples.Length / Channels).End();
        }
    }

    /// <summary>Preallocated PCM ring. Audio callbacks never wait for snapshots.</summary>
    internal sealed class AudioCaptureBuffer
    {
        readonly object _gate = new object();
        readonly short[] _samples;
        readonly int _rate, _channels, _capacityFrames;
        long _first = -1, _end;
        public AudioCaptureBuffer(int rate, int channels, int seconds)
        {
            _rate = rate; _channels = channels;
            _capacityFrames = Math.Max(1, Math.Min(rate * Math.Max(1, seconds), 16 * 1024 * 1024 / channels));
            _samples = new short[_capacityFrames * channels];
        }
        public void Write(float[] data, int channels, double startT)
        {
            if (channels != _channels || double.IsNaN(startT) || double.IsInfinity(startT) || startT < 0 || !Monitor.TryEnter(_gate)) return;
            try
            {
                long start = (long)Math.Round(startT * _rate);
                int frames = data.Length / channels;
                // Smooth small callback jitter while retaining real pauses/gaps.
                if (_first >= 0 && Math.Abs(start - _end) < _rate / 50) start = _end;
                if (_first >= 0 && start < _end) return;
                if (_first < 0) { _first = start; _end = start; }
                for (long frame = Math.Max(_end, start - _capacityFrames); frame < start; frame++)
                    for (int c = 0; c < channels; c++) _samples[(frame % _capacityFrames) * channels + c] = 0;
                for (int i = Math.Max(0, frames - _capacityFrames); i < frames; i++)
                    for (int c = 0; c < channels; c++)
                    {
                        float value = data[i * channels + c];
                        _samples[((start + i) % _capacityFrames) * channels + c] =
                            float.IsNaN(value) ? (short)0 : (short)(Math.Max(-1f, Math.Min(1f, value)) * 32767);
                    }
                _end = start + frames;
                _first = Math.Max(_first, _end - _capacityFrames);
            }
            finally { Monitor.Exit(_gate); }
        }
        public CapturedAudio Snapshot(double startT, double endT)
        {
            lock (_gate)
            {
                long start = Math.Max(_first, (long)Math.Ceiling(Math.Max(0, startT) * _rate));
                long end = Math.Min(_end, (long)Math.Floor(endT * _rate));
                if (_first < 0 || end <= start) return null;
                var samples = new short[(end - start) * _channels];
                for (long i = start; i < end; i++)
                    for (int c = 0; c < _channels; c++) samples[(i - start) * _channels + c] = _samples[(i % _capacityFrames) * _channels + c];
                return new CapturedAudio { StartT = (double)start / _rate, SampleRate = _rate, Channels = _channels, Samples = samples };
            }
        }
    }
}
