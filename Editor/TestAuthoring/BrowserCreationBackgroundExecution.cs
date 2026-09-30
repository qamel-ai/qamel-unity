using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class BrowserCreationBackgroundExecution
    {
        const string ActiveKey = "Qamel.TestLab.BrowserCreationBackgroundActive";
        const string PreviousBackgroundKey =
            "Qamel.TestLab.BrowserCreationPreviousBackground";

        public static void Hold()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                SessionState.SetBool(PreviousBackgroundKey, Application.runInBackground);
                SessionState.SetBool(ActiveKey, true);
            }
            Application.runInBackground = true;
        }

        public static void Release()
        {
            if (!SessionState.GetBool(ActiveKey, false))
                return;
            Application.runInBackground = SessionState.GetBool(
                PreviousBackgroundKey,
                false);
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseBool(PreviousBackgroundKey);
        }
    }
}
