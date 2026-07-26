using UnityEngine;

namespace QamelCapture
{
    /// <summary>
    /// Starts Qamel automatically when the game runs. No scene setup is required:
    /// install the package, configure Project Settings > Qamel, press play.
    /// </summary>
    internal static class QamelBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            var settings = QamelSettings.LoadFromResources();
            if (settings == null)
            {
#if UNITY_EDITOR
                Debug.Log(QLog.Prefix + "Not configured yet. Open Project Settings > Qamel to set up capture.");
#endif
                return;
            }

            if (!settings.captureEnabled) return;
            if (QamelRunner.Instance != null) return;

            var host = new GameObject("[QamelCapture]")
            {
                hideFlags = HideFlags.NotEditable,
            };
            Object.DontDestroyOnLoad(host);
            host.AddComponent<QamelRunner>();
        }
    }
}
