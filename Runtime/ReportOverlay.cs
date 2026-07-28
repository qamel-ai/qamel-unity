using System;
using UnityEngine;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace QamelCapture
{
    /// <summary>
    /// Minimal in-game report form drawn with IMGUI (no render-pipeline or UI-stack
    /// dependencies).
    ///
    /// The text field has exactly one owner, decided by whether IMGUI actually
    /// receives input. Wherever it does -- always with the legacy backend, and in
    /// the editor on either backend -- IMGUI owns the field and its edited string
    /// is written straight back. In Input System-only player builds IMGUI gets no
    /// events, so Qamel assembles the text from Keyboard.onTextInput into a
    /// <see cref="ReportTextEditor"/> and draws it read-only. Mixing the two (an
    /// editable control drawing a string it cannot write to) is what used to make
    /// deleted text reappear on the next keystroke.
    /// </summary>
    internal sealed class ReportOverlay
    {
        const int MaxTextChars = 500;
        const int PanelWidth = 460;
        const int PanelHeight = 220;
        const string TextControlName = "qamel_report_text";
        const string CaretGlyph = "|";
        const string ShortcutHint = "Shift+Enter to send · Esc to cancel";

        enum FormAction
        {
            None = 0,
            Cancel = 1,
            Send = 2,
        }

        public bool IsOpen { get; private set; }

        /// <summary>Raised with the (possibly empty) tester text when the form is submitted.</summary>
        public event Action<string> Submitted;

        /// <summary>Raised when the form opens, so the game can pause its own way.</summary>
        public event Action Opened;

        /// <summary>Raised when the form closes, whether it was sent or cancelled.</summary>
        public event Action Closed;

        readonly QamelSettings _settings;
        readonly ReportTextEditor _editor = new ReportTextEditor(MaxTextChars);
        CursorLockMode _previousLock;
        bool _previousCursorVisible;
        bool _focusPending;
        bool _paused;
        float _previousTimeScale;
        bool _previousAudioPause;
        static Texture2D _dimTexture;

        public ReportOverlay(QamelSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// True when IMGUI receives input events, and therefore owns the text field.
        /// On the Input System-only backend player builds get no IMGUI events at
        /// all, while the editor's Game view still feeds them.
        /// </summary>
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
        static bool ImguiOwnsInput => true;
#elif QAMEL_INPUT_SYSTEM
        static bool ImguiOwnsInput => Application.isEditor;
#else
        // Input System-only without the package present: nothing can feed the
        // buffer, so leave the interactive form as the least broken option.
        static bool ImguiOwnsInput => true;
#endif

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _editor.Clear();
            _focusPending = true;
            _previousLock = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            ApplyPause();
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            if (!ImguiOwnsInput)
            {
                var keyboard = Keyboard.current;
                if (keyboard != null) keyboard.onTextInput += OnTextInput;
            }
#endif
            Opened?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Cursor.lockState = _previousLock;
            Cursor.visible = _previousCursorVisible;
            ReleasePause();
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = Keyboard.current;
            if (keyboard != null) keyboard.onTextInput -= OnTextInput;
#endif
            Closed?.Invoke();
        }

        /// <summary>
        /// Freezing the game is opt-in: it is what a tester expects in a
        /// singleplayer game, and wrong for anything networked, where only this
        /// client would stop. Capture is unaffected either way -- frame timing
        /// uses unscaled time and the session clock is a stopwatch.
        /// </summary>
        void ApplyPause()
        {
            if (_paused || _settings == null || !_settings.pauseWhileReporting) return;
            _previousTimeScale = Time.timeScale;
            _previousAudioPause = AudioListener.pause;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            _paused = true;
        }

        void ReleasePause()
        {
            if (!_paused) return;
            _paused = false;
            Time.timeScale = _previousTimeScale;
            AudioListener.pause = _previousAudioPause;
        }

        /// <summary>Called from the runner's Update on the main thread.</summary>
        public void Tick()
        {
            if (!IsOpen || ImguiOwnsInput) return;
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
                // Same shortcut as the button form: Shift+Enter sends, Enter is a
                // line break. Buttons cannot be clicked on this backend (IMGUI
                // receives no pointer events), so the shortcut is the only way out.
                bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                if (shift) Submit();
                else _editor.NewLine();
                return;
            }
            if (keyboard.backspaceKey.wasPressedThisFrame) _editor.Backspace();
            if (keyboard.deleteKey.wasPressedThisFrame) _editor.Delete();
            if (keyboard.leftArrowKey.wasPressedThisFrame) _editor.MoveLeft();
            if (keyboard.rightArrowKey.wasPressedThisFrame) _editor.MoveRight();
            if (keyboard.homeKey.wasPressedThisFrame) _editor.MoveHome();
            if (keyboard.endKey.wasPressedThisFrame) _editor.MoveEnd();
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
            GUILayout.Label("Report a bug", TitleStyle);
            GUILayout.Label(
                "What went wrong? A sentence helps a lot — but it is optional: the last " +
                "minutes of gameplay, logs and input are attached either way.",
                WrappedStyle);
            GUILayout.Space(4);

            FormAction action = ImguiOwnsInput ? DrawInteractiveForm() : DrawKeyboardOnlyForm();
            GUILayout.EndArea();

            if (action == FormAction.Cancel) Close();
            else if (action == FormAction.Send) Submit();
        }

        FormAction DrawInteractiveForm()
        {
            var action = FormAction.None;

            // Plain Enter belongs to the text area so testers can structure what
            // they write; Shift+Enter is the send shortcut, matching every Qamel
            // report form. Both are claimed before the text area sees them.
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
                if (e.keyCode == KeyCode.Escape)
                {
                    action = FormAction.Cancel;
                    e.Use();
                }
                else if (enter && e.shift)
                {
                    action = FormAction.Send;
                    e.Use();
                }
            }

            GUI.SetNextControlName(TextControlName);
            string current = _editor.Text;
            string edited = GUILayout.TextArea(current, MaxTextChars,
                GUILayout.MinHeight(90), GUILayout.ExpandHeight(true));
            if (!string.Equals(edited, current, StringComparison.Ordinal)) _editor.Set(edited);
            if (_focusPending)
            {
                GUI.FocusControl(TextControlName);
                _focusPending = false;
            }

            GUILayout.Space(4);
            GUILayout.Label(ShortcutHint, HintStyle);
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Width(100), GUILayout.Height(32)))
                action = FormAction.Cancel;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Send report", PrimaryButtonStyle,
                    GUILayout.Width(170), GUILayout.Height(32)))
                action = FormAction.Send;
            GUILayout.EndHorizontal();
            return action;
        }

        /// <summary>
        /// Read-only on purpose: an editable control keeps its own copy of the
        /// text and would diverge from the buffer the keyboard events feed. The
        /// caret is drawn where the next character actually lands.
        /// </summary>
        FormAction DrawKeyboardOnlyForm()
        {
            GUILayout.Label(_editor.Render(CaretGlyph), ReadOnlyTextStyle,
                GUILayout.MinHeight(90), GUILayout.ExpandHeight(true));
            GUILayout.Space(6);
            GUILayout.Label(ShortcutHint, HintStyle);
            return FormAction.None;
        }

        void Submit()
        {
            string text = (_editor.Text ?? "").Trim();
            Close();
            Submitted?.Invoke(text);
        }

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        void OnTextInput(char c)
        {
            if (!IsOpen) return;
            if (c == '\b' || c == '\n' || c == '\r' || c == 27) return;
            _editor.Insert(c);
        }
#endif

        static GUIStyle _titleStyle;
        static GUIStyle _wrappedStyle;
        static GUIStyle _hintStyle;
        static GUIStyle _primaryButtonStyle;
        static GUIStyle _readOnlyTextStyle;

        static GUIStyle TitleStyle =>
            _titleStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = GUI.skin.label.fontSize + 4,
                fontStyle = FontStyle.Bold,
            };

        static GUIStyle WrappedStyle =>
            _wrappedStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };

        static GUIStyle HintStyle =>
            _hintStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(9, GUI.skin.label.fontSize - 1),
                wordWrap = true,
            };

        /// <summary>Send is the primary action, so it reads heavier than Cancel.</summary>
        static GUIStyle PrimaryButtonStyle =>
            _primaryButtonStyle ??= new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };

        /// <summary>Looks like the text area it replaces, but cannot take focus.</summary>
        static GUIStyle ReadOnlyTextStyle =>
            _readOnlyTextStyle ??= new GUIStyle(GUI.skin.textArea)
            {
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
            };

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
