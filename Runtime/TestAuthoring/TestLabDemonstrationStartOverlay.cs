#if UNITY_EDITOR
using UnityEngine;

namespace QamelCapture.TestAuthoring
{
    [DefaultExecutionOrder(32000)]
    internal sealed class TestLabDemonstrationStartOverlay : MonoBehaviour
    {
        CursorLockMode _previousCursorLockMode;
        bool _previousCursorVisible;
        bool _hasCursorState;
        bool _isPresented;

        public bool StartRequested { get; private set; }

        public void Present()
        {
            if (_isPresented)
                return;

            _previousCursorLockMode = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            _hasCursorState = true;
            _isPresented = true;
            KeepCursorAvailableForStart();
        }

        public void Dismiss()
        {
            _isPresented = false;
            RestoreCursorState();
        }

        void LateUpdate()
        {
            if (_isPresented && !StartRequested)
                KeepCursorAvailableForStart();
        }

        void OnGUI()
        {
            if (!_isPresented)
                return;

            if (!StartRequested)
                KeepCursorAvailableForStart();

            var previousColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(
                new Rect(0, 0, Screen.width, Screen.height),
                Texture2D.whiteTexture);
            GUI.color = previousColor;

            const float width = 480f;
            const float height = 210f;
            var panel = new Rect(
                Mathf.Max(16f, (Screen.width - width) * 0.5f),
                Mathf.Max(16f, (Screen.height - height) * 0.5f),
                Mathf.Min(width, Screen.width - 32f),
                Mathf.Min(height, Screen.height - 32f));
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };
            var bodyStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                wordWrap = true
            };

            GUILayout.BeginArea(panel, GUI.skin.window);
            GUILayout.Space(12f);
            GUILayout.Label(
                StartRequested ? "Preparing starting state" : "Ready to record",
                titleStyle,
                GUILayout.Height(30f));
            GUILayout.Space(6f);
            GUILayout.Label(
                StartRequested
                    ? "Qamel is discarding the activation input and restoring the captured " +
                      "starting state. Recording begins automatically when it is ready."
                    : "The game is paused. Put your hand on the controls, then start when " +
                      "ready. The activation click will not become part of the Test.",
                bodyStyle,
                GUILayout.Height(64f));
            GUILayout.FlexibleSpace();
            if (!StartRequested)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        "Start demonstration",
                        GUILayout.Width(240f),
                        GUILayout.Height(44f)))
                {
                    StartRequested = true;
                    RestoreCursorState();
                    Event.current.Use();
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("Starting...", bodyStyle, GUILayout.Height(44f));
            }
            GUILayout.Space(12f);
            GUILayout.EndArea();
        }

        void OnDestroy()
        {
            RestoreCursorState();
        }

        static void KeepCursorAvailableForStart()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void RestoreCursorState()
        {
            if (!_hasCursorState)
                return;

            _hasCursorState = false;
            Cursor.lockState = _previousCursorLockMode;
            Cursor.visible = _previousCursorVisible;
        }
    }
}
#endif
