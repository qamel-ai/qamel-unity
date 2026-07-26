using System;
using UnityEngine;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace QamelCapture
{
    /// <summary>
    /// Minimal in-game report form drawn with IMGUI (no render-pipeline or UI-stack
    /// dependencies). With the legacy input backend it is a regular IMGUI form.
    /// With the Input System-only backend, where IMGUI receives no input in player
    /// builds, text is collected via Keyboard.onTextInput and Enter/Esc handle
    /// submit/cancel instead of buttons.
    /// </summary>
    internal sealed class ReportOverlay
    {
        const int MaxTextChars = 500;
        const int PanelWidth = 460;
        const int PanelHeight = 220;

        public bool IsOpen { get; private set; }

        /// <summary>Raised with the (possibly empty) tester text when the form is submitted.</summary>
        public event Action<string> Submitted;

        string _text = "";
        CursorLockMode _previousLock;
        bool _previousCursorVisible;
        bool _focusPending;
        static Texture2D _dimTexture;

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _text = "";
            _focusPending = true;
            _previousLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = Keyboard.current;
            if (keyboard != null) keyboard.onTextInput += OnTextInput;
#endif
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Cursor.lockState = _previousLock;
            Cursor.visible = _previousCursorVisible;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = Keyboard.current;
            if (keyboard != null) keyboard.onTextInput -= OnTextInput;
#endif
        }

        /// <summary>Called from the runner's Update on the main thread.</summary>
        public void Tick()
        {
            if (!IsOpen) return;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Close();
                return;
            }
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Submit();
                return;
            }
            if (keyboard.backspaceKey.wasPressedThisFrame && _text.Length > 0)
                _text = _text.Substring(0, _text.Length - 1);
#endif
        }

        public void OnGUI()
        {
            if (!IsOpen) return;

            GUI.depth = -1000;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), DimTexture);

            var panel = new Rect(
                (Screen.width - PanelWidth) / 2f,
                (Screen.height - PanelHeight) / 2f,
                PanelWidth, PanelHeight);

            GUILayout.BeginArea(panel, GUI.skin.window);
            GUILayout.Label("Report a bug");
            GUILayout.Label("The last minutes of gameplay, logs and input will be attached.", GUI.skin.label);
            GUILayout.Space(4);

#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            var e = Event.current;
            bool escapePressed = e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape;

            GUI.SetNextControlName("qamel_report_text");
            _text = GUILayout.TextArea(_text, MaxTextChars, GUILayout.MinHeight(90), GUILayout.ExpandHeight(true));
            if (_focusPending)
            {
                GUI.FocusControl("qamel_report_text");
                _focusPending = false;
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Esc to cancel");
            GUILayout.FlexibleSpace();
            bool cancelClicked = GUILayout.Button("Cancel", GUILayout.Width(90));
            bool sendClicked = GUILayout.Button("Send report", GUILayout.Width(120));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (escapePressed || cancelClicked) Close();
            else if (sendClicked) Submit();
#else
            // Input System-only: IMGUI gets no events in players, so the text is
            // collected via onTextInput (see Tick) and only rendered here.
            GUILayout.TextArea(_text.Length < MaxTextChars ? _text + "|" : _text,
                GUILayout.MinHeight(90), GUILayout.ExpandHeight(true));
            GUILayout.Space(6);
            GUILayout.Label("Type a short description. Enter = send report, Esc = cancel.");
            GUILayout.EndArea();
#endif
        }

        void Submit()
        {
            string text = (_text ?? "").Trim();
            Close();
            Submitted?.Invoke(text);
        }

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        void OnTextInput(char c)
        {
            if (!IsOpen) return;
            if (c == '\b' || c == '\n' || c == '\r' || c == 27) return;
            if (_text.Length < MaxTextChars) _text += c;
        }
#endif

        static Texture2D DimTexture
        {
            get
            {
                if (_dimTexture == null)
                {
                    _dimTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    _dimTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
                    _dimTexture.Apply();
                }
                return _dimTexture;
            }
        }
    }
}
