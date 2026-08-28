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

        const string ShowOptionalKey = "Qamel.Settings.ShowOptional";
        const string FoldUploadKey = "Qamel.Settings.Fold.Upload";
        const string FoldBuildKey = "Qamel.Settings.Fold.Build";
        const string FoldCaptureKey = "Qamel.Settings.Fold.Capture";
        const string FoldReportingKey = "Qamel.Settings.Fold.Reporting";
        const string FoldExperimentalKey = "Qamel.Settings.Fold.Experimental";
        const string FoldDiagnosticsKey = "Qamel.Settings.Fold.Diagnostics";
        const string DashboardUrl = "https://qamel.ai";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Qamel", SettingsScope.Project)
            {
                label = "Qamel",
                keywords = new[] { "qamel", "capture", "bug", "report", "playtest", "qa", "api key" },
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

        static void DrawProperty(SerializedObject serialized, string name)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property != null)
                EditorGUILayout.PropertyField(property, true);
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
                    EditorGUILayout.HelpBox("Checking the ingest API key…", MessageType.Info);
                    break;
                case QamelHealthCheck.Result.Connected:
                    EditorGUILayout.HelpBox("Connected to Qamel. This project can receive reports.",
                        MessageType.Info);
                    break;
                case QamelHealthCheck.Result.InvalidKey:
                    EditorGUILayout.HelpBox(
                        "This API key was rejected. Create a new ingest key at qamel.ai " +
                        "(project > API keys) and paste it here.",
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

        static void DrawEssential(SerializedObject serialized, QamelSettings settings)
        {
            EditorGUILayout.LabelField("Essential", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Create an ingest API key for your project at qamel.ai, then paste it here. " +
                "Defaults work for most projects; open Optional settings below only if you need to change them.",
                MessageType.None);
            EditorGUILayout.Space(2);
            DrawProperty(serialized, nameof(QamelSettings.captureEnabled));
            DrawProperty(serialized, nameof(QamelSettings.apiKey));
            if (string.IsNullOrWhiteSpace(settings.apiKey))
            {
                EditorGUILayout.HelpBox(
                    "API key is required. Create one at qamel.ai (project > API keys). " +
                    "Reports stay in memory until one is set.",
                    MessageType.Warning);
                if (GUILayout.Button("Create API key at qamel.ai", GUILayout.Width(220)))
                    Application.OpenURL(DashboardUrl);
            }
            DrawProperty(serialized, nameof(QamelSettings.reportHotkey));
        }

        static void DrawOptional(SerializedObject serialized, QamelSettings settings)
        {
            bool showOptional = SessionState.GetBool(ShowOptionalKey, false);
            bool next = EditorGUILayout.Foldout(showOptional, "Optional settings", true);
            if (next != showOptional) SessionState.SetBool(ShowOptionalKey, next);
            if (!next) return;

            EditorGUI.indentLevel++;

            // Groups default open so expanding Optional is one click to a full layout;
            // SessionState still remembers if the user collapses a section.
            if (Foldout(FoldUploadKey, "Upload", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                DrawProperty(serialized, nameof(QamelSettings.endpoint));
                DrawEndpointWarning(settings);
                DrawProperty(serialized, nameof(QamelSettings.uploadReports));
                EditorGUI.indentLevel--;
            }

            if (Foldout(FoldBuildKey, "Build context", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                DrawProperty(serialized, nameof(QamelSettings.buildId));
                DrawProperty(serialized, nameof(QamelSettings.defaultParticipantKind));
                EditorGUI.indentLevel--;
            }

            if (Foldout(FoldCaptureKey, "Capture", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "If capture cannot keep up, Qamel skips frames instead of slowing the game. " +
                    "Drops are counted on each report. Lower Capture Fps if footage looks choppy.",
                    MessageType.None);
                DrawProperty(serialized, nameof(QamelSettings.bufferSeconds));
                DrawProperty(serialized, nameof(QamelSettings.captureFps));
                DrawProperty(serialized, nameof(QamelSettings.frameWidth));
                DrawProperty(serialized, nameof(QamelSettings.jpegQuality));
                DrawProperty(serialized, nameof(QamelSettings.frameFlip));
                DrawProperty(serialized, nameof(QamelSettings.captureInput));
                DrawProperty(serialized, nameof(QamelSettings.captureMousePosition));
                EditorGUI.indentLevel--;
            }

            if (Foldout(FoldReportingKey, "Reporting", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                DrawProperty(serialized, nameof(QamelSettings.useBuiltInOverlay));
                DrawProperty(serialized, nameof(QamelSettings.pauseWhileReporting));
                DrawProperty(serialized, nameof(QamelSettings.autoReportOnException));
                EditorGUI.indentLevel--;
            }

            if (Foldout(FoldExperimentalKey, "Experimental", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                DrawProperty(serialized, nameof(QamelSettings.continuousStreaming));
                DrawProperty(serialized, nameof(QamelSettings.streamChunkSeconds));
                EditorGUI.indentLevel--;
            }

            if (Foldout(FoldDiagnosticsKey, "Diagnostics", defaultOpen: true))
            {
                EditorGUI.indentLevel++;
                DrawProperty(serialized, nameof(QamelSettings.checkForUpdates));
                DrawProperty(serialized, nameof(QamelSettings.verboseLogging));
                DrawProperty(serialized, nameof(QamelSettings.sendPluginDiagnostics));
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
        }

        static void Draw()
        {
            EditorGUILayout.Space(8);

            var settings = QamelSettings.LoadFromResources();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Qamel Capture is installed but not configured yet.\n" +
                    "Create the settings asset, then paste an ingest API key from qamel.ai.",
                    MessageType.Info);
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Create Qamel settings", GUILayout.Width(220)))
                    {
                        Directory.CreateDirectory(AssetDir);
                        var asset = ScriptableObject.CreateInstance<QamelSettings>();
                        AssetDatabase.CreateAsset(asset, AssetPath);
                        AssetDatabase.SaveAssets();
                        Selection.activeObject = asset;
                    }
                    if (GUILayout.Button("Create API key at qamel.ai", GUILayout.Width(220)))
                        Application.OpenURL(DashboardUrl);
                }
                return;
            }

            DrawVersionAndUpdates(settings);
            EditorGUILayout.Space(8);

            var serialized = new SerializedObject(settings);
            serialized.Update();

            DrawEssential(serialized, settings);
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
