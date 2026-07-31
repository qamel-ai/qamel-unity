using System;
using System.Collections.Generic;
using UnityEngine;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace QamelCapture
{
    /// <summary>
    /// Records keyboard and mouse input as repro-step events in the session stream.
    /// Works with either Unity input backend. Only raw key identities are recorded,
    /// never assembled text; can be disabled entirely in settings.
    /// </summary>
    internal sealed class InputRecorder
    {
        const float MousePosInterval = 0.2f;
        const float MousePosMinDelta = 0.01f;

        readonly QamelSettings _settings;
        readonly ISessionSink _sink;
        readonly Func<double> _now;
        float _nextMouseSampleAt;
        Vector2 _lastMousePos = new Vector2(-1f, -1f);

#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
        static readonly KeyCode[] PollableKeyCodes = BuildKeyCodes();
        readonly List<KeyCode> _held = new List<KeyCode>(16);

        static KeyCode[] BuildKeyCodes()
        {
            var list = new List<KeyCode>(340);
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                if (key == KeyCode.None) continue;
                if (key >= KeyCode.JoystickButton0) break; // gamepads deferred
                list.Add(key);
            }
            return list.ToArray();
        }
#endif

        public InputRecorder(QamelSettings settings, ISessionSink sink, Func<double> now)
        {
            _settings = settings;
            _sink = sink;
            _now = now;
        }

        /// <summary>Called from the runner's Update on the main thread.</summary>
        public void Tick()
        {
            if (!_settings.captureInput) return;
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            TickLegacy();
#elif QAMEL_INPUT_SYSTEM
            TickInputSystem();
#endif
        }

#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
        void TickLegacy()
        {
            if (Input.anyKeyDown)
            {
                for (int i = 0; i < PollableKeyCodes.Length; i++)
                {
                    var key = PollableKeyCodes[i];
                    if (!Input.GetKeyDown(key)) continue;
                    Emit("key_down", KeyName(key));
                    if (!_held.Contains(key)) _held.Add(key);
                }
            }

            for (int i = _held.Count - 1; i >= 0; i--)
            {
                var key = _held[i];
                // "!GetKey" also releases keys swallowed while focus was lost.
                if (Input.GetKeyUp(key) || !Input.GetKey(key))
                {
                    Emit("key_up", KeyName(key));
                    _held.RemoveAt(i);
                }
            }

            SampleMouse(Input.mousePosition);
        }

        static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Mouse0: return "mouse_left";
                case KeyCode.Mouse1: return "mouse_right";
                case KeyCode.Mouse2: return "mouse_middle";
                default: return key.ToString();
            }
        }
#endif

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        void TickInputSystem()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var keys = keyboard.allKeys;
                for (int i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    if (key == null) continue;
                    if (key.wasPressedThisFrame) Emit("key_down", key.keyCode.ToString());
                    if (key.wasReleasedThisFrame) Emit("key_up", key.keyCode.ToString());
                }
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                EmitButton(mouse.leftButton, "mouse_left");
                EmitButton(mouse.rightButton, "mouse_right");
                EmitButton(mouse.middleButton, "mouse_middle");
                SampleMouse(mouse.position.ReadValue());
            }
        }

        void EmitButton(ButtonControl button, string name)
        {
            if (button.wasPressedThisFrame) Emit("key_down", name);
            if (button.wasReleasedThisFrame) Emit("key_up", name);
        }
#endif

        void SampleMouse(Vector2 position)
        {
            if (!_settings.captureMousePosition) return;
            if (Time.unscaledTime < _nextMouseSampleAt) return;
            if (Screen.width <= 0 || Screen.height <= 0) return;

            var normalized = new Vector2(position.x / Screen.width, position.y / Screen.height);
            if ((normalized - _lastMousePos).sqrMagnitude < MousePosMinDelta * MousePosMinDelta) return;

            _nextMouseSampleAt = Time.unscaledTime + MousePosInterval;
            _lastMousePos = normalized;
            double t = _now();
            _sink.AddEvent(t, SessionEvents.MousePos(t, normalized.x, normalized.y));
        }

        /// <summary>
        /// Records one key/mouse action. Used by the live pollers; also callable
        /// from tests so we can lock buffer wiring without faking Unity input.
        /// </summary>
        internal void Emit(string action, string key)
        {
            if (!_settings.captureInput) return;
            double t = _now();
            _sink.AddEvent(t, SessionEvents.Input(t, action, key));
        }
    }
}
