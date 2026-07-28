using System.IO;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Project Settings > Qamel. Creates and edits the QamelSettings asset so setup
    /// is: install package, paste API key, press play.
    /// </summary>
    internal static class QamelSettingsProvider
    {
        const string AssetDir = "Assets/Qamel/Resources";
        const string AssetPath = AssetDir + "/" + QamelSettings.ResourceName + ".asset";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Qamel", SettingsScope.Project)
            {
                label = "Qamel",
                keywords = new[] { "qamel", "capture", "bug", "report", "playtest", "qa" },
                guiHandler = _ => Draw(),
            };
        }

        /// <summary>Fresh instance; its fields carry the declared defaults.</summary>
        static QamelSettings Defaults()
        {
            return ScriptableObject.CreateInstance<QamelSettings>();
        }

        /// <summary>
        /// The endpoint stays editable because a build's ingest host cannot be changed
        /// later: self-hosting, a regional host and local testing all need it. A typo
        /// there silently costs every report, so flag anything unusual.
        /// </summary>
        static void DrawEndpointWarning(QamelSettings settings)
        {
            var defaults = Defaults();
            string defaultEndpoint = defaults.endpoint;
            Object.DestroyImmediate(defaults);

            string endpoint = (settings.endpoint ?? "").Trim();
            if (endpoint == defaultEndpoint) return;

            EditorGUILayout.Space(4);
            bool loopback = endpoint.StartsWith("http://localhost") ||
                            endpoint.StartsWith("http://127.0.0.1");
            if (endpoint.Length == 0)
            {
                EditorGUILayout.HelpBox("Endpoint is empty — reports cannot be delivered.",
                    MessageType.Error);
            }
            else if (!endpoint.StartsWith("https://") && !loopback)
            {
                EditorGUILayout.HelpBox(
                    "Endpoint must be an https:// URL (plain http is only allowed on localhost). " +
                    "Uploads will fail as configured.",
                    MessageType.Error);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Custom ingest host. The default is {defaultEndpoint}.",
                    MessageType.Info);
            }
        }

        static bool HasNonDefaultValues(QamelSettings settings)
        {
            var defaults = Defaults();
            defaults.apiKey = settings.apiKey; // the key is never "a default"
            bool identical = JsonUtility.ToJson(defaults) == JsonUtility.ToJson(settings);
            Object.DestroyImmediate(defaults);
            return !identical;
        }

        static void ResetToDefaults(QamelSettings settings)
        {
            var defaults = Defaults();
            defaults.apiKey = settings.apiKey;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(defaults), settings);
            Object.DestroyImmediate(defaults);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Version line plus everything the update check has to say. A git
        /// dependency gets no update affordance from Package Manager, so this is
        /// the only place a developer can find out they are behind.
        /// </summary>
        static void DrawVersionAndUpdates(QamelSettings settings)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    "Qamel for Unity " + QamelUpdateCheck.InstalledVersion,
                    EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(QamelUpdateCheck.IsChecking))
                {
                    if (GUILayout.Button(
                            QamelUpdateCheck.IsChecking ? "Checking…" : "Check for updates",
                            EditorStyles.miniButton, GUILayout.Width(140)))
                    {
                        QamelUpdateCheck.CheckNow(settings, userInitiated: true);
                    }
                }
            }

            // Settings GUI only repaints on interaction, which would leave
            // "Checking…" frozen on screen after the request finished.
            if (QamelUpdateCheck.IsChecking || QamelPackageUpdater.IsUpdating)
            {
                if (EditorWindow.focusedWindow != null) EditorWindow.focusedWindow.Repaint();
            }

            if (QamelUpdateCheck.InstalledVersionUnsupported)
            {
                EditorGUILayout.HelpBox(
                    "This version is older than the oldest supported release (" +
                    QamelUpdateCheck.MinSupportedVersion + "). Reports still upload, but " +
                    "update as soon as you can.",
                    MessageType.Warning);
            }

            if (!QamelUpdateCheck.UpdateAvailable) return;

            string latest = QamelUpdateCheck.LatestVersion;
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Qamel " + latest + " is available. You have " +
                QamelUpdateCheck.InstalledVersion + ".",
                MessageType.Info);

            string unsupportedReason =
                QamelPackageUpdater.UnsupportedReason(QamelPackageUpdater.Installed);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                    unsupportedReason != null || QamelPackageUpdater.IsUpdating))
                {
                    if (GUILayout.Button(
                            QamelPackageUpdater.IsUpdating ? "Updating…" : "Update to " + latest,
                            GUILayout.Width(180)))
                    {
                        QamelPackageUpdater.UpdateToLatest(latest);
                    }
                }
                if (GUILayout.Button("Release notes", GUILayout.Width(120)))
                {
                    Application.OpenURL(QamelUpdateCheck.NotesUrl);
                }
                if (GUILayout.Button("Skip this version", GUILayout.Width(140)))
                {
                    QamelUpdateCheck.SkipCurrentLatest();
                }
            }
            if (unsupportedReason != null)
            {
                EditorGUILayout.LabelField(unsupportedReason, WrappedMiniLabel);
            }
        }

        static GUIStyle _wrappedMiniLabel;

        static GUIStyle WrappedMiniLabel =>
            _wrappedMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        static void Draw()
        {
            EditorGUILayout.Space(8);

            var settings = QamelSettings.LoadFromResources();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Qamel Capture is installed but not configured yet.\n" +
                    "Create the settings asset, then paste your project API key.",
                    MessageType.Info);
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Create Qamel settings", GUILayout.Width(220)))
                {
                    Directory.CreateDirectory(AssetDir);
                    var asset = ScriptableObject.CreateInstance<QamelSettings>();
                    AssetDatabase.CreateAsset(asset, AssetPath);
                    AssetDatabase.SaveAssets();
                    Selection.activeObject = asset;
                }
                return;
            }

            DrawVersionAndUpdates(settings);
            EditorGUILayout.Space(8);

            var serialized = new SerializedObject(settings);
            var property = serialized.GetIterator();
            property.NextVisible(true); // skip m_Script
            while (property.NextVisible(false))
            {
                EditorGUILayout.PropertyField(property, true);
            }
            if (serialized.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
            }

            DrawEndpointWarning(settings);

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Qamel keeps captured data only in memory on the device and delivers it to the " +
                "Qamel servers. Nothing is written to the player's disk, so an API key and " +
                "endpoint are required for reports to go anywhere.",
                MessageType.None);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!HasNonDefaultValues(settings)))
            {
                if (GUILayout.Button("Reset to defaults", GUILayout.Width(220)) &&
                    EditorUtility.DisplayDialog(
                        "Reset Qamel settings?",
                        "Every setting goes back to its default. Your API key is kept.",
                        "Reset",
                        "Cancel"))
                {
                    ResetToDefaults(settings);
                }
            }
        }
    }
}
