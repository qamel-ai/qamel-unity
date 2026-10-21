using UnityEngine;
#if QAMEL_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace QamelCapture
{
    /// <summary>
    /// Hotkey polling that works with either Unity input backend. Settings store a
    /// legacy KeyCode; when only the Input System package is active, the KeyCode is
    /// mapped to an Input System Key by name.
    /// </summary>
    internal static class CompatInput
    {
        public static bool GetKeyDown(KeyCode key)
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            return Input.GetKeyDown(key);
#elif QAMEL_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            if (!TryMap(key, out var mapped)) return false;
            var control = keyboard[mapped];
            return control != null && control.wasPressedThisFrame;
#else
            return false;
#endif
        }

#if QAMEL_INPUT_SYSTEM
        internal static bool TryMap(KeyCode key, out Key mapped)
        {
            switch (key)
            {
                case KeyCode.Return: mapped = Key.Enter; return true;
                case KeyCode.KeypadEnter: mapped = Key.NumpadEnter; return true;
                case KeyCode.Escape: mapped = Key.Escape; return true;
                case KeyCode.Backspace: mapped = Key.Backspace; return true;
                case KeyCode.BackQuote: mapped = Key.Backquote; return true;
                case KeyCode.LeftControl: mapped = Key.LeftCtrl; return true;
                case KeyCode.RightControl: mapped = Key.RightCtrl; return true;
                case KeyCode.LeftCommand: mapped = Key.LeftMeta; return true;
                case KeyCode.RightCommand: mapped = Key.RightMeta; return true;
                case KeyCode.Print: mapped = Key.PrintScreen; return true;
            }

            string name = key.ToString();
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                name = "Digit" + name.Substring("Alpha".Length);
            else if (name.StartsWith("Keypad", System.StringComparison.Ordinal))
                name = "Numpad" + name.Substring("Keypad".Length);
            // Covers F1-F12, letters, Space, arrows etc. whose names match.
            return System.Enum.TryParse(name, true, out mapped) && mapped != Key.None;
        }
#endif
    }
}
