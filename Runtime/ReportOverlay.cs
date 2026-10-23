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
        const string TextControlName = "qamel_report_text";
        const string CaretGlyph = "|";

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
        static readonly Color Ink = new Color32(22, 32, 30, 255);
        static readonly Color MutedInk = new Color32(76, 87, 84, 255);
        static readonly Color Sand = new Color32(245, 243, 236, 255);
        static readonly Color Water = new Color32(14, 155, 181, 255);
        static readonly Color Border = new Color32(134, 140, 137, 255);
        Vector2 _textScroll;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        FormAction _pressedButton;
#endif

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
            _textScroll = Vector2.zero;
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
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            _pressedButton = FormAction.None;
#endif
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
            // Input System-only players do not feed IMGUI pointer events.
            // Use the same layout for drawing and hit testing, and activate only
            // when the press and release both land on the same button.
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var layout = CalculateLayout(Screen.width, Screen.height);
                Vector2 position = mouse.position.ReadValue();
                position = new Vector2(position.x, Screen.height - position.y) / layout.Scale;
                if (mouse.leftButton.wasPressedThisFrame)
                    _pressedButton = ButtonAt(layout, position);
                if (mouse.leftButton.wasReleasedThisFrame)
                {
                    var released = ButtonAt(layout, position);
                    var pressed = _pressedButton;
                    _pressedButton = FormAction.None;
                    if (released == pressed && released != FormAction.None)
                    {
                        if (released == FormAction.Cancel) Close();
                        else Submit();
                        return;
                    }
                }
            }

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
                // line break.
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

        // Logical pixels stay at full size down to a 640 x 360 game window.
        // Larger displays scale up; exceptionally small windows scale to fit.
        internal struct Layout
        {
            public float Scale;
            public Rect Panel, Title, Description, FieldLabel, Counter, Text, Cancel, Submit;
        }

        internal static Layout CalculateLayout(float screenWidth, float screenHeight)
        {
            float scale = Mathf.Min(
                Mathf.Clamp(Mathf.Min(screenWidth / 1280f, screenHeight / 720f), 1f, 1.5f),
                Mathf.Min(screenWidth / 640f, screenHeight / 360f));
            scale = Mathf.Max(0.01f, scale);
            float width = screenWidth / scale;
            float height = screenHeight / scale;
            var panel = new Rect((width - Mathf.Min(760f, width - 32f)) / 2f,
                (height - Mathf.Min(520f, height - 32f)) / 2f,
                Mathf.Min(760f, width - 32f), Mathf.Min(520f, height - 32f));
            float x = panel.x + 24f;
            float innerWidth = panel.width - 48f;
            float buttonY = panel.yMax - 24f - 60f;
            float buttonWidth = (innerWidth - 16f) / 2f;
            return new Layout
            {
                Scale = scale,
                Panel = panel,
                Title = new Rect(x, panel.y + 20f, innerWidth, 36f),
                Description = new Rect(x, panel.y + 62f, innerWidth, 44f),
                FieldLabel = new Rect(x, panel.y + 112f, innerWidth - 100f, 26f),
                Counter = new Rect(x + innerWidth - 100f, panel.y + 112f, 100f, 26f),
                Text = new Rect(x, panel.y + 144f, innerWidth, buttonY - 16f - (panel.y + 144f)),
                Cancel = new Rect(x, buttonY, buttonWidth, 60f),
                Submit = new Rect(x + buttonWidth + 16f, buttonY, buttonWidth, 60f),
            };
        }

        static FormAction ButtonAt(Layout layout, Vector2 position)
        {
            if (layout.Cancel.Contains(position)) return FormAction.Cancel;
            if (layout.Submit.Contains(position)) return FormAction.Send;
            return FormAction.None;
        }

        public void OnGUI()
        {
            if (!IsOpen) return;

            var previousMatrix = GUI.matrix;
            var previousColor = GUI.color;
            var previousBackground = GUI.backgroundColor;
            var previousContent = GUI.contentColor;
            int previousDepth = GUI.depth;
            var layout = CalculateLayout(Screen.width, Screen.height);
            var action = FormAction.None;
            try
            {
                GUI.depth = -1000;
                GUI.matrix = Matrix4x4.Scale(new Vector3(layout.Scale, layout.Scale, 1f));
                GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
                Fill(new Rect(0, 0, Screen.width / layout.Scale, Screen.height / layout.Scale),
                    new Color(0f, 0f, 0f, 0.65f));
                Fill(layout.Panel, Border);
                Fill(new Rect(layout.Panel.x + 1f, layout.Panel.y + 1f,
                    layout.Panel.width - 2f, layout.Panel.height - 2f), Color.white);
                Fill(new Rect(layout.Panel.x + 1f, layout.Panel.y + 1f, layout.Panel.width - 2f, 4f), Water);

                EnsureStyles();
                GUI.Label(layout.Title, "Report a bug", _titleStyle);
                GUI.Label(layout.Description,
                    "Your recent gameplay, logs and input are attached automatically.", _bodyStyle);
                GUI.Label(layout.FieldLabel, "What happened? (optional)", _fieldLabelStyle);
                GUI.Label(layout.Counter, $"{_editor.Length} / {MaxTextChars}", _counterStyle);

                if (ImguiOwnsInput) action = DrawInteractiveText(layout.Text);
                else DrawKeyboardOnlyText(layout.Text);

                if (DrawButton(layout.Cancel, "Cancel", "Click or press Esc", false))
                    action = FormAction.Cancel;
                if (DrawButton(layout.Submit, "Submit report", "Click or press Shift+Enter", true))
                    action = FormAction.Send;
            }
            finally
            {
                GUI.matrix = previousMatrix;
                GUI.color = previousColor;
                GUI.backgroundColor = previousBackground;
                GUI.contentColor = previousContent;
                GUI.depth = previousDepth;
            }

            if (action == FormAction.Cancel) Close();
            else if (action == FormAction.Send) Submit();
        }

        FormAction DrawInteractiveText(Rect rect)
        {
            var action = FormAction.None;
            // Claim shortcuts before the field can turn Enter into a newline.
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

            DrawTextFrame(rect);
            GUI.SetNextControlName(TextControlName);
            string current = _editor.Text;
            string edited = GUI.TextArea(rect, current, MaxTextChars, _textStyle);
            if (!string.Equals(edited, current, StringComparison.Ordinal)) _editor.Set(edited);
            if (_focusPending)
            {
                GUI.FocusControl(TextControlName);
                _focusPending = false;
            }
            return action;
        }

        // Read-only: the Input System buffer owns the text and caret.
        void DrawKeyboardOnlyText(Rect rect)
        {
            DrawTextFrame(rect);
            string text = _editor.Render(CaretGlyph);
            // Clip inside the padding so long reports never paint over the border.
            var viewport = new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f);
            float height = Mathf.Max(viewport.height,
                _readOnlyTextStyle.CalcHeight(new GUIContent(text), viewport.width));
            float caretBottom = _readOnlyTextStyle.CalcHeight(
                new GUIContent(_editor.Text.Substring(0, _editor.Caret) + CaretGlyph), viewport.width);
            _textScroll.y = Mathf.Clamp(_textScroll.y,
                Mathf.Max(0f, caretBottom - viewport.height), Mathf.Max(0f, caretBottom - 22f));
            GUI.BeginGroup(viewport);
            GUI.Label(new Rect(0, -_textScroll.y, viewport.width, height), text, _readOnlyTextStyle);
            GUI.EndGroup();
        }

        static void DrawTextFrame(Rect rect)
        {
            Fill(rect, Border);
            Fill(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f), Sand);
        }

        bool DrawButton(Rect rect, string title, string shortcut, bool primary)
        {
            var e = Event.current;
            Vector2 pointer = e != null ? e.mousePosition : new Vector2(-1f, -1f);
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            if (!ImguiOwnsInput && Mouse.current != null)
            {
                pointer = Mouse.current.position.ReadValue();
                pointer = new Vector2(pointer.x, Screen.height - pointer.y) /
                    CalculateLayout(Screen.width, Screen.height).Scale;
            }
#endif
            bool hover = rect.Contains(pointer);
            Fill(rect, primary ? Ink : Border);
            Fill(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f),
                primary ? (hover ? new Color32(38, 57, 53, 255) : Ink) : (hover ? Sand : Color.white));
            bool clicked = GUI.Button(rect, GUIContent.none, _buttonStyle);
            GUI.Label(new Rect(rect.x, rect.y + 7f, rect.width, 24f), title,
                primary ? _primaryTitleStyle : _buttonTitleStyle);
            GUI.Label(new Rect(rect.x, rect.y + 32f, rect.width, 20f), shortcut,
                primary ? _primaryHintStyle : _buttonHintStyle);
            return clicked;
        }

        static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
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

        static GUIStyle _titleStyle, _bodyStyle, _fieldLabelStyle, _counterStyle, _textStyle, _readOnlyTextStyle;
        static GUIStyle _buttonStyle, _buttonTitleStyle, _buttonHintStyle, _primaryTitleStyle, _primaryHintStyle;

        static GUIStyle LabelStyle(int size, Color color, bool bold = false,
            TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                wordWrap = true,
                alignment = alignment,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                richText = false,
            };
            style.normal.textColor = color;
            return style;
        }

        static void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = LabelStyle(28, Ink, true);
            _bodyStyle = LabelStyle(18, MutedInk);
            _fieldLabelStyle = LabelStyle(18, Ink, true);
            _counterStyle = LabelStyle(16, MutedInk, alignment: TextAnchor.MiddleRight);
            _textStyle = new GUIStyle(GUI.skin.textArea)
            {
                fontSize = 18,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 10, 10),
                margin = new RectOffset(0, 0, 0, 0),
                richText = false,
            };
            // Explicit colors and empty backgrounds keep the form independent of
            // the host game's GUISkin and its light/dark defaults.
            foreach (var state in new[] { _textStyle.normal, _textStyle.hover, _textStyle.active,
                _textStyle.focused, _textStyle.onNormal, _textStyle.onHover,
                _textStyle.onActive, _textStyle.onFocused })
            {
                state.background = null;
                state.textColor = Ink;
            }
            _readOnlyTextStyle = new GUIStyle(_textStyle) { padding = new RectOffset(0, 0, 0, 0) };
            _buttonStyle = new GUIStyle(GUIStyle.none);
            _buttonTitleStyle = LabelStyle(18, Ink, true, TextAnchor.MiddleCenter);
            _buttonHintStyle = LabelStyle(16, MutedInk, alignment: TextAnchor.MiddleCenter);
            _primaryTitleStyle = LabelStyle(18, Color.white, true, TextAnchor.MiddleCenter);
            _primaryHintStyle = LabelStyle(16, Color.white, alignment: TextAnchor.MiddleCenter);
        }
    }
}
