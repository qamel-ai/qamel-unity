using UnityEngine;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using System.Collections.Generic;
using UnityEngine.InputSystem;
#endif

namespace QamelCapture.TestAuthoring
{
    internal sealed class TestLabSimulationPause
    {
        float _previousTimeScale;
        bool _previousAudioPause;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        readonly List<InputDevice> _disabledDevices = new List<InputDevice>();
#endif

        public bool IsPaused { get; private set; }

        public void Sync(bool shouldPause)
        {
            if (shouldPause == IsPaused)
                return;
            if (!shouldPause)
            {
                Release();
                return;
            }

            _previousTimeScale = Time.timeScale;
            _previousAudioPause = AudioListener.pause;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            IsPaused = true;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            // Update still runs at timeScale zero. Mouse look and button edges
            // can mutate a fresh scene even when movement time is paused.
            foreach (var device in InputSystem.devices)
                PauseDevice(device);
            InputSystem.onDeviceChange += OnDeviceChanged;
#endif
        }

        public void Release()
        {
            if (!IsPaused)
                return;
            IsPaused = false;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            InputSystem.onDeviceChange -= OnDeviceChanged;
            foreach (var device in _disabledDevices)
                if (device.added) InputSystem.EnableDevice(device);
            _disabledDevices.Clear();
#endif
            Time.timeScale = _previousTimeScale;
            AudioListener.pause = _previousAudioPause;
        }

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        void OnDeviceChanged(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Enabled ||
                change == InputDeviceChange.Reconnected)
                PauseDevice(device);
        }

        void PauseDevice(InputDevice device)
        {
            if (!device.enabled || !(device is Keyboard || device is Mouse || device is Gamepad))
                return;
            if (!_disabledDevices.Contains(device)) _disabledDevices.Add(device);
            InputSystem.ResetDevice(device);
            // Use the supported device gate. Merely marking events handled no
            // longer suppresses state updates in recent Input System versions.
            // Frontend-only disabling still permits state updates in recent
            // Input System versions, so use the full device-disable boundary.
            InputSystem.DisableDevice(device);
        }
#endif
    }
}
