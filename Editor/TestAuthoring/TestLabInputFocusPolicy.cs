using System;
using System.Runtime.InteropServices;
using QamelCapture.TestAuthoring;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class TestLabInputFocusPolicy
    {
        public static bool IsGameViewFocused
        {
            get
            {
                var focused = EditorWindow.focusedWindow;
                return focused != null && focused.GetType().Name == "GameView";
            }
        }

        public static bool FocusGameView()
        {
            if (Application.isBatchMode)
                return true;

#if UNITY_2022_3_OR_NEWER
            if (!EditorApplication.isFocused)
                BringEditorApplicationToFront();
#else
            // Older supported Editors do not expose EditorApplication.isFocused.
            // Activating an already frontmost Editor is harmless.
            BringEditorApplicationToFront();
#endif
            if (IsGameViewFocused)
                return true;

            return EditorApplication.ExecuteMenuItem("Window/General/Game");
        }

        static void BringEditorApplicationToFront()
        {
#if UNITY_EDITOR_OSX
            try
            {
                var applicationClass = objc_getClass("NSApplication");
                var application = SendIntPtr(
                    applicationClass,
                    sel_registerName("sharedApplication"));
                if (application != IntPtr.Zero)
                {
                    SendBool(
                        application,
                        sel_registerName("activateIgnoringOtherApps:"),
                        true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Qamel Test Lab] Unity could not bring its Editor window forward: " +
                    exception.Message);
            }
#endif
        }

#if UNITY_EDITOR_OSX
        const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjectiveCLibrary)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(ObjectiveCLibrary)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        static extern void SendBool(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.I1)] bool value);
#endif

        internal static bool ShouldFocusGameView(
            TestAuthoringControllerState previousState,
            TestAuthoringControllerState currentState,
            bool isPlaying)
        {
            return isPlaying &&
                   previousState != currentState &&
                   (currentState == TestAuthoringControllerState.ReadyForDemonstration ||
                    currentState == TestAuthoringControllerState.Restoring ||
                    currentState == TestAuthoringControllerState.ReplayCountdown ||
                    currentState == TestAuthoringControllerState.ReplayRunning);
        }

        internal static bool ShouldPauseSimulation(
            TestAuthoringControllerState state,
            bool isPlaying,
            bool isBatchMode,
            bool isCapturingGameplayInput)
        {
            if (!isPlaying)
                return false;

            return TestAuthoringController.RequiresPausedSimulation(state) ||
                   (state == TestAuthoringControllerState.Recording &&
                    !isBatchMode && !isCapturingGameplayInput);
        }

        public static bool ShouldCaptureGameplayInput()
        {
            if (Application.isBatchMode)
                return true;

            var focused = EditorWindow.focusedWindow;
            var hovered = EditorWindow.mouseOverWindow;
            return Evaluate(
                batchMode: false,
                testLabFocused: focused is TestLabWindow,
                testLabHovered: hovered is TestLabWindow,
                focusedWindowType: focused != null ? focused.GetType().Name : null);
        }

        internal static bool Evaluate(
            bool batchMode,
            bool testLabFocused,
            bool testLabHovered,
            string focusedWindowType)
        {
            if (batchMode)
                return true;
            if (testLabFocused || testLabHovered)
                return false;
            return focusedWindowType == "GameView";
        }
    }

}
