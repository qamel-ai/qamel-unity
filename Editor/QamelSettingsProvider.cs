using System.IO;
using QamelCapture.Editor.TestAuthoring;
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

        const string ShowOptionalKey = "Qamel.Settings.ShowOptional";
        const string FoldExperimentalTestingKey = "Qamel.Settings.Fold.ExperimentalTesting";
        static bool _listeningForHotkey;
        static EditorWindow _hotkeyWindow;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            var provider = new SettingsProvider("Project/Qamel", SettingsScope.Project)
            {
                label = "Qamel",
                keywords = new[] { "qamel", "capture", "bug", "report", "playtest", "qa", "api key" },
                guiHandler = _ => Draw(),
                activateHandler = (_, __) => QamelUpdateCheck.CheckWhenDue(QamelSettings.LoadFromResources()),
                deactivateHandler = () => { _listeningForHotkey = false; _hotkeyWindow = null; },
            };
            // Repaint the settings window itself, including after a request finishes
            // while another editor window has focus.
            provider.inspectorUpdateHandler = provider.Repaint;
            return provider;
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
                EditorGUILayout.HelpBox("Endpoint is empty: reports cannot be delivered.",
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
                EditorGUILayout.LabelField("Qamel for Unity " + QamelUpdateCheck.InstalledVersion,
                    EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                string status = QamelUpdateCheck.DescribeStatus(QamelUpdateCheck.IsChecking,
                    QamelPackageUpdater.IsUpdating, QamelUpdateCheck.ReleaseVerified,
                    QamelUpdateCheck.LastError, QamelUpdateCheck.InstalledVersion, QamelUpdateCheck.LatestVersion,
                    QamelUpdateCheck.LastSuccess, System.DateTime.UtcNow);
                GUILayout.Label(new GUIContent(status, QamelUpdateCheck.LastError ?? QamelUpdateCheck.LastResult),
                    EditorStyles.miniBoldLabel, GUILayout.Width(115));
                using (new EditorGUI.DisabledScope(QamelUpdateCheck.IsChecking || QamelPackageUpdater.IsUpdating))
                {
                    if (GUILayout.Button("Check for updates",
                            EditorStyles.miniButton, GUILayout.Width(140)))
                        QamelUpdateCheck.CheckNow(settings, userInitiated: true);
                }
            }

            var lastCheck = QamelUpdateCheck.LastSuccessfulCheckUtc;
            if (lastCheck.HasValue)
            {
                var localTime = lastCheck.Value.ToLocalTime();
                EditorGUILayout.LabelField(new GUIContent(
                    "Last successful check: " + localTime.ToString("g"),
                    localTime.ToString("yyyy-MM-dd HH:mm:ss zzz") +
                    "\nChecks run once per Editor session, then hourly. Check for updates refreshes immediately."),
                    EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(QamelUpdateCheck.LastError))
                EditorGUILayout.HelpBox(QamelUpdateCheck.LastError, MessageType.Warning);
            if (!string.IsNullOrEmpty(QamelPackageUpdater.LastError))
                EditorGUILayout.HelpBox(QamelPackageUpdater.LastError, MessageType.Warning);

            if (QamelUpdateCheck.InstalledVersionUnsupported)
                EditorGUILayout.HelpBox("This version is older than the oldest supported release (" +
                    QamelUpdateCheck.MinSupportedVersion + "). Update as soon as you can.", MessageType.Warning);
            if (!QamelUpdateCheck.ShowUpdate) return;

            string latest = QamelUpdateCheck.LatestVersion;
            string message = "Qamel " + latest + " is available. You have " + QamelUpdateCheck.InstalledVersion + ".";
            if (QamelPackageUpdater.IsUpdating) message = "Updating Qamel to " + latest + "...";
            EditorGUILayout.HelpBox(message, MessageType.Info);

            string unsupportedReason = QamelPackageUpdater.UnsupportedReason(QamelPackageUpdater.Installed);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(unsupportedReason != null || QamelPackageUpdater.IsUpdating))
                {
                    if (GUILayout.Button(QamelPackageUpdater.IsUpdating ? "Updating..." : "Update to " + latest,
                            GUILayout.Width(180))) QamelPackageUpdater.UpdateToLatest(latest);
                }
                if (GUILayout.Button("View changes", GUILayout.Width(120))) Application.OpenURL(QamelUpdateCheck.NotesUrl);
                using (new EditorGUI.DisabledScope(QamelPackageUpdater.IsUpdating))
                {
                    if (GUILayout.Button("Skip this version", GUILayout.Width(140))) QamelUpdateCheck.SkipCurrentLatest();
                }
            }
            if (unsupportedReason != null) EditorGUILayout.LabelField(unsupportedReason, WrappedMiniLabel);
        }

        static GUIStyle _wrappedMiniLabel;

        static GUIStyle WrappedMiniLabel =>
            _wrappedMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        static void DrawProperty(SerializedObject serialized, string name, string label = null)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null)
                EditorGUILayout.PropertyField(property, new GUIContent(label ?? property.displayName, property.tooltip), true);
        }

        static bool Foldout(string sessionKey, string label, bool defaultOpen = false)
        {
            bool open = SessionState.GetBool(sessionKey, defaultOpen);
            bool next = EditorGUILayout.Foldout(open, label, true);
            if (next != open) SessionState.SetBool(sessionKey, next);
            return next;
        }

        static void DrawHealthStatus(QamelSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.apiKey) &&
                QamelHealthCheck.Status == QamelHealthCheck.Result.Idle)
            {
                return;
            }

            if (QamelHealthCheck.IsChecking && EditorWindow.focusedWindow != null)
                EditorWindow.focusedWindow.Repaint();

            switch (QamelHealthCheck.Status)
            {
                case QamelHealthCheck.Result.Checking:
                    EditorGUILayout.HelpBox("Checking the API key…", MessageType.Info);
                    break;
                case QamelHealthCheck.Result.Connected:
                    EditorGUILayout.HelpBox("Connected to Qamel. This project can receive reports.",
                        MessageType.Info);
                    break;
                case QamelHealthCheck.Result.InvalidKey:
                    EditorGUILayout.HelpBox(
                        "This API key was rejected. Create a new key and paste it here.",
                        MessageType.Error);
                    DrawRetryHealth(settings);
                    break;
                case QamelHealthCheck.Result.UnverifiedKey:
                    EditorGUILayout.HelpBox(
                        "This server has not confirmed capture-only access. Player builds will be blocked. " +
                        "Use an API key from your project's API keys page and an updated ingest server.",
                        MessageType.Error);
                    DrawRetryHealth(settings);
                    break;
                case QamelHealthCheck.Result.Unreachable:
                    EditorGUILayout.HelpBox(
                        "Could not reach the ingest host to check this key. " +
                        "Confirm the endpoint, then try again.",
                        MessageType.Warning);
                    DrawRetryHealth(settings);
                    break;
            }
        }

        static void DrawRetryHealth(QamelSettings settings)
        {
            if (GUILayout.Button("Check connection", GUILayout.Width(160)))
                QamelHealthCheck.CheckNow(settings);
        }

        static void DrawCapture(SerializedObject serialized, QamelSettings settings)
        {
            DrawProperty(serialized, nameof(QamelSettings.captureEnabled), "Playtest capture enabled");
            DrawProperty(serialized, nameof(QamelSettings.includeInPlayerBuild));
            SerializedProperty apiKey = serialized.FindProperty(nameof(QamelSettings.apiKey));
            if (apiKey != null)
            {
                EditorGUILayout.PropertyField(
                    apiKey,
                    new GUIContent("API key", apiKey.tooltip),
                    true);
            }
            if (GUILayout.Button(string.IsNullOrWhiteSpace(settings.apiKey) ? "Create API key" : "Manage API keys",
                    GUILayout.Width(180)))
                Application.OpenURL(QamelSettingsLink.ApiKeysUrl(settings.endpoint));
            DrawReportHotkey(serialized.FindProperty(nameof(QamelSettings.reportHotkey)));
        }

        static void DrawReportHotkey(SerializedProperty property)
        {
            if (property == null) return;
            Rect row = EditorGUILayout.GetControlRect();
            Rect button = EditorGUI.PrefixLabel(row, new GUIContent("Report hotkey",
                "Click, then press a single keyboard key. Escape cancels."));
            int control = GUIUtility.GetControlID("Qamel.ReportHotkey".GetHashCode(), FocusType.Keyboard, button);
            Event input = Event.current;
            if (_listeningForHotkey && (EditorWindow.focusedWindow != _hotkeyWindow ||
                GUIUtility.keyboardControl != control ||
                (input.type == EventType.MouseDown && !button.Contains(input.mousePosition))))
            {
                _listeningForHotkey = false;
                if (GUIUtility.keyboardControl == control) GUIUtility.keyboardControl = 0;
            }
            if (_listeningForHotkey && input.type == EventType.KeyDown)
            {
                if (input.keyCode != KeyCode.None)
                {
                    if (input.keyCode != KeyCode.Escape) property.intValue = (int)input.keyCode;
                    _listeningForHotkey = false;
                    GUIUtility.keyboardControl = 0;
                }
                input.Use();
            }
            var assigned = (KeyCode)property.intValue;
            string name = assigned >= KeyCode.Alpha0 && assigned <= KeyCode.Alpha9
                ? ((int)assigned - (int)KeyCode.Alpha0).ToString()
                : ObjectNames.NicifyVariableName(assigned.ToString());
            string label = _listeningForHotkey ? "Press a key..." : name;
            if (GUI.Button(button, new GUIContent(label, "Click to change the report hotkey."), EditorStyles.miniButton))
            {
                _listeningForHotkey = !_listeningForHotkey;
                _hotkeyWindow = _listeningForHotkey ? EditorWindow.focusedWindow : null;
                EditorGUIUtility.editingTextField = false;
                GUIUtility.keyboardControl = _listeningForHotkey ? control : 0;
            }
        }

        static void DrawOptional(SerializedObject serialized, QamelSettings settings)
        {
            if (!Foldout(ShowOptionalKey, "Optional settings")) return;
            EditorGUI.indentLevel++;
            DrawProperty(serialized, nameof(QamelSettings.endpoint));
            DrawEndpointWarning(settings);
            DrawProperty(serialized, nameof(QamelSettings.uploadReports));
            DrawProperty(serialized, nameof(QamelSettings.buildId));
            DrawProperty(serialized, nameof(QamelSettings.defaultParticipantKind));
            EditorGUILayout.Space(8);
            DrawProperty(serialized, nameof(QamelSettings.bufferSeconds));
            DrawProperty(serialized, nameof(QamelSettings.captureFps));
            DrawProperty(serialized, nameof(QamelSettings.frameWidth));
            DrawProperty(serialized, nameof(QamelSettings.jpegQuality));
            DrawProperty(serialized, nameof(QamelSettings.frameFlip));
            DrawProperty(serialized, nameof(QamelSettings.captureInput));
            DrawProperty(serialized, nameof(QamelSettings.captureMousePosition));
            DrawProperty(serialized, nameof(QamelSettings.captureAudio));
            EditorGUILayout.Space(8);
            DrawProperty(serialized, nameof(QamelSettings.useBuiltInOverlay));
            DrawProperty(serialized, nameof(QamelSettings.pauseWhileReporting));
            DrawProperty(serialized, nameof(QamelSettings.autoReportOnException));
            DrawProperty(serialized, nameof(QamelSettings.continuousStreaming));
            if (settings.continuousStreaming)
                DrawProperty(serialized, nameof(QamelSettings.streamChunkSeconds));
            EditorGUILayout.Space(8);
            DrawProperty(serialized, nameof(QamelSettings.checkForUpdates));
            using (new EditorGUI.DisabledScope(!settings.checkForUpdates || QamelPackageUpdater.IsUpdating))
            {
                bool automatic = EditorGUILayout.Toggle(new GUIContent("Update automatically",
                    "Install stable releases when Unity is idle and scenes are saved. Applies to this project on this machine."),
                    QamelPackageUpdater.AutomaticUpdatesEnabled);
                if (automatic != QamelPackageUpdater.AutomaticUpdatesEnabled)
                    QamelPackageUpdater.AutomaticUpdatesEnabled = automatic;
            }
            DrawProperty(serialized, nameof(QamelSettings.verboseLogging));
            DrawProperty(serialized, nameof(QamelSettings.sendPluginDiagnostics));
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!HasNonDefaultValues(settings)))
            {
                if (GUILayout.Button("Reset to defaults", GUILayout.Width(180)) &&
                    EditorUtility.DisplayDialog("Reset Qamel settings?",
                        "Every setting goes back to its default. Your API key is kept.", "Reset", "Cancel"))
                    ResetToDefaults(settings);
            }
            DrawExperimentalTesting();
            EditorGUI.indentLevel--;
        }

        static void Draw()
        {
            EditorGUILayout.Space(8);

            var settings = QamelSettings.LoadFromResources();
            DrawVersionAndUpdates(settings);
            EditorGUILayout.Space(8);
            if (settings == null)
            {
                if (GUILayout.Button("Create Qamel settings", GUILayout.Width(180)))
                {
                    Directory.CreateDirectory(AssetDir);
                    var asset = ScriptableObject.CreateInstance<QamelSettings>();
                    AssetDatabase.CreateAsset(asset, AssetPath);
                    AssetDatabase.SaveAssets();
                    Selection.activeObject = asset;
                }
                return;
            }

            var serialized = new SerializedObject(settings);
            serialized.Update();

            DrawCapture(serialized, settings);
            EditorGUILayout.Space(8);
            DrawOptional(serialized, settings);

            // Surface host problems even when Optional settings is collapsed.
            if (!SessionState.GetBool(ShowOptionalKey, false))
                DrawEndpointWarning(settings);

            if (serialized.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(settings);
                QamelHealthCheck.OnSettingsChanged(settings);
            }

            QamelHealthCheck.ObserveSettings(settings);
            DrawHealthStatus(settings);

        }

        static void DrawExperimentalTesting()
        {
            EditorGUILayout.Space(12);
            if (!Foldout(FoldExperimentalTestingKey, "Experimental testing", defaultOpen: false))
                return;

            bool enabled = TestLabPreferences.IsEnabled;
            using (new EditorGUI.DisabledScope(enabled && !TestLabSession.CanDisableExperimentalFeature))
            {
                bool next = EditorGUILayout.Toggle("Enable experimental testing", enabled);
                if (next != enabled)
                {
                    if (next) TestLabSession.EnableExperimentalFeature();
                    else TestLabWindow.RequestDisableExperimentalFeature();
                }
            }
            if (!TestLabPreferences.IsEnabled) return;
            if (!TestLabSession.CanDisableExperimentalFeature)
                EditorGUILayout.HelpBox(TestLabSession.DisableBlockedMessage, MessageType.None);
            if (GUILayout.Button("Open Test Lab", GUILayout.Width(220)))
                TestLabWindow.Open();
            if (ClientConnectionSession.HasPending &&
                GUILayout.Button("Cancel pending connection", GUILayout.Width(220)))
                ClientConnectionSession.Cancel();
        }
    }
}
