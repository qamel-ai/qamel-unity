using System;
using UnityEngine;

namespace QamelCapture
{
    internal sealed class GameAudioTap : MonoBehaviour
    {
        AudioCaptureBuffer _buffer;
        Func<double> _now;
        int _rate;
        volatile bool _active;
        public void Initialize(AudioCaptureBuffer buffer, Func<double> now, int rate)
        { _buffer = buffer; _now = now; _rate = rate; _active = true; }
        public void Stop() { _active = false; }
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_active || channels < 1) return;
            double start = Math.Max(0, _now() - (double)data.Length / channels / _rate);
            _buffer.Write(data, channels, start);
        }
    }
}
