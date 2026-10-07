using System;
using UnityEngine;

namespace QamelCapture
{
    /// <summary>Copies the standard Unity listener output without modifying audio.</summary>
    internal sealed class GameAudioRecorder : IDisposable
    {
        readonly Func<double> _now;
        readonly int _seconds;
        AudioCaptureBuffer _buffer;
        GameAudioTap _tap;
        int _rate, _channels;
        double _nextCheck;
        public GameAudioRecorder(Func<double> now, int seconds) { _now = now; _seconds = seconds; }
        public void Tick()
        {
            if (_now() < _nextCheck) return;
            _nextCheck = _now() + 0.5;
            int rate = AudioSettings.outputSampleRate;
            int channels = AudioSettings.speakerMode == AudioSpeakerMode.Mono ? 1 : AudioSettings.speakerMode == AudioSpeakerMode.Stereo ? 2 : 0;
            if (rate < 8000 || rate > 192000 || channels == 0) { Dispose(); _buffer = null; return; }
            if (_buffer == null || rate != _rate || channels != _channels)
            {
                Dispose(); _rate = rate; _channels = channels;
                _buffer = new AudioCaptureBuffer(rate, channels, _seconds);
            }
            if (_tap != null && _tap.TryGetComponent<AudioListener>(out var current) && current.isActiveAndEnabled) return;
            if (_tap != null) { _tap.Stop(); UnityEngine.Object.Destroy(_tap); _tap = null; }
#if UNITY_2022_2_OR_NEWER
            var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
#else
            var listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
#endif
            foreach (var listener in listeners)
                if (listener.isActiveAndEnabled)
                {
                    _tap = listener.gameObject.AddComponent<GameAudioTap>();
                    _tap.hideFlags = HideFlags.HideInInspector;
                    _tap.Initialize(_buffer, _now, _rate);
                    break;
                }
        }
        public CapturedAudio Snapshot(double start, double end) => _buffer?.Snapshot(start, end);
        public void Dispose()
        {
            if (_tap != null) { _tap.Stop(); UnityEngine.Object.Destroy(_tap); _tap = null; }
        }
    }
}
