using System;
using System.Linq;
using QamelCapture.TestAuthoring;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class TestLabWindow : EditorWindow
    {
        Vector2 _scroll;
        string _draftName = "";
        string _expectedOutcome = "";
        string _reviewNote = "";
        string _saveError = "";
        string _savedReplayError = "";

        public static void Open()
        {
            if (!TestLabPreferences.IsEnabled) return;
            var window = GetWindow<TestLabWindow>();
            window.titleContent = new GUIContent("Qamel Test Lab");
            window.minSize = new Vector2(440, 520);
            window.Show();
        }

        void OnInspectorUpdate()
        {
            Repaint();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeader();
            if (!TestLabPreferences.IsEnabled)
            {
                EditorGUILayout.HelpBox(TestLabPreferences.DisabledMessage, MessageType.Info);
                if (GUILayout.Button("Open Qamel settings"))
                    SettingsService.OpenProjectSettings("Project/Qamel");
                EditorGUILayout.EndScrollView();
                return;
            }
            if (!string.IsNullOrWhiteSpace(TestLabLaunch.Status))
                EditorGUILayout.HelpBox(TestLabLaunch.Status, MessageType.Info);

            var controller = TestLabSession.Controller;
            var model = TestLabWindowModel.Create(
                EditorApplication.isPlaying,
                TestLabSession.InputAvailable,
                TestLabSession.InputUnavailableReason,
                TestLabSession.Registry.HasProvider,
                controller,
                _draftName,
                _expectedOutcome);

            DrawCurrentState(model);
            DrawHealth(controller);
            DrawAuthoringConnection();
            DrawSavedTestLibrary(controller);
            if (model.ShowDraftForm)
                DrawDraftForm();
            bool primaryDrawn = false;
            if (model.ShowDraft && controller?.CurrentDraft != null)
            {
                DrawDraft(
                    controller.CurrentDraft,
                    TestLabSession.CurrentDraftIsSavedVersion
                        ? "Saved Test version " + TestLabSession.LoadedSavedVersionNumber
                        : "Local draft " +
                          controller.CurrentDraft.DraftId.Substring(0, 8));
                if (model.ShowReview && controller.CurrentRun != null)
                    DrawReview(controller.CurrentDraft, controller.CurrentRun);
                if (model.PrimaryAction != TestLabPrimaryAction.None)
                {
                    DrawPrimaryAction(model);
                    primaryDrawn = true;
                }
                if (model.ShowRun)
                    DrawValidationHistory(controller.CurrentDraft);
                if (TestLabSession.CurrentDraftIsSavedVersion)
                    DrawLoadedSavedVersion();
                else
                    DrawCloudSave(controller.CurrentDraft);
            }
            if (controller?.CurrentDraft == null && TestLabSession.SaveOperation != null)
                DrawDetachedCloudSave(TestLabSession.SaveOperation);
            else if (controller?.CurrentDraft == null && TestLabSession.LastSavedTest != null)
                DrawLastSavedTest(TestLabSession.LastSavedTest);
            if (controller?.CurrentDraft == null &&
                !string.IsNullOrWhiteSpace(TestLabSession.SaveRequestError))
            {
                EditorGUILayout.HelpBox(
                    TestLabSession.SaveRequestError,
                    MessageType.Error);
            }
            if (!primaryDrawn && model.PrimaryAction != TestLabPrimaryAction.None)
                DrawPrimaryAction(model);
            if (model.CanClearDraft)
            {
                using (new EditorGUI.DisabledScope(
                           TestLabSession.IsSaveBusy || TestLabSession.IsSavedReplayBusy))
                {
                    if (GUILayout.Button("Clear draft"))
                        TestLabSession.ClearDraft();
                }
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.HelpBox(
                "Experimental and Editor-only. Downloaded Test bytes, local starting state, " +
                "input buffer, replay history, and notes always end with Play Mode. Qamel " +
                "keeps only explicitly saved immutable Test Versions and artifacts.",
                MessageType.Info);
            if (!TestLabSession.CanDisableExperimentalFeature)
                EditorGUILayout.HelpBox(TestLabSession.DisableBlockedMessage, MessageType.None);
            using (new EditorGUI.DisabledScope(!TestLabSession.CanDisableExperimentalFeature))
            {
                if (TestLabPreferences.IsEnabled &&
                    GUILayout.Button(
                        "Disable experimental Test Lab",
                        EditorStyles.miniButton))
                {
                    RequestDisableExperimentalFeature();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        internal static void RequestDisableExperimentalFeature()
        {
            TestLabSession.DisableExperimentalFeature(() => EditorUtility.DisplayDialog(
                "Discard local Test Lab work?",
                "Disabling experimental testing discards this unsaved draft and local replay notes. " +
                "Saved Test Versions remain in Qamel.",
                "Discard and disable", "Keep working"));
        }

        static void DrawHeader()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Qamel Test Lab", LargeTitle);
            EditorGUILayout.LabelField(
                "Turn a short gameplay demonstration into a local test draft.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);
        }

        static void DrawCurrentState(TestLabWindowModel model)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(model.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(model.Guidance, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        void DrawAuthoringConnection()
        {
            EditorGUILayout.LabelField("Qamel connection", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (ClientConnectionSession.HasPending)
            {
                EditorGUILayout.HelpBox(ClientConnectionSession.Status ?? "Waiting for the Qamel connection approval.", MessageType.Info);
                if (GUILayout.Button("Cancel pending connection"))
                    ClientConnectionSession.Cancel();
            }
            EditorGUILayout.LabelField(
                "Use a project API key with Test Lab access. The key stays on this machine.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            bool connectionBusy = TestLabSession.IsSaveBusy ||
                                  TestLabSession.IsSavedReplayBusy ||
                                  TestLabSession.IsConnectedRunBusy ||
                                  TestLabSession.IsBrowserTestCreationActive ||
                                  TestLabSession.CatalogOperation?.State ==
                                  SavedTestCatalogState.Loading;
            using (new EditorGUI.DisabledScope(connectionBusy))
            {
                EditorGUI.BeginChangeCheck();
                GUI.SetNextControlName("QamelTestLabEndpoint");
                string endpoint = EditorGUILayout.TextField(
                    "Qamel endpoint",
                    TestLabPreferences.AuthoringEndpoint);
                GUI.SetNextControlName("QamelTestLabApiKey");
                string apiKey = SafePasswordField(
                    "Project API key",
                    TestLabPreferences.AuthoringApiKey);
                if (EditorGUI.EndChangeCheck())
                {
                    TestLabPreferences.AuthoringEndpoint = endpoint;
                    TestLabPreferences.AuthoringApiKey = apiKey;
                    TestAuthoringHealthCheck.OnPreferencesChanged();
                    TestLabSession.ClearSaveRequestFeedback();
                    _saveError = "";
                }
            }
            TestAuthoringHealthCheck.ObservePreferences();

            var connection = TestAuthoringConnectionModel.Create(
                TestLabPreferences.AuthoringEndpoint,
                TestLabPreferences.AuthoringApiKey,
                TestAuthoringHealthCheck.State,
                TestAuthoringHealthCheck.ProjectId,
                TestAuthoringHealthCheck.ProjectName,
                TestAuthoringHealthCheck.LastError);
            EditorGUILayout.HelpBox(
                connection.Status,
                NoticeType(connection.NoticeKind));
            if (connection.IsConnected)
            {
                HealthRow("Project", connection.ProjectName);
                HealthRow("Project ID", connection.ProjectId);
                HealthRow("Test Lab access", "Allowed");
            }
            if (connectionBusy)
                EditorGUILayout.LabelField(
                    "Finish the current connection check or save before changing this connection.",
                    EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           connectionBusy || !connection.CanCheck))
                {
                    string label = TestAuthoringHealthCheck.IsChecking
                        ? "Checking connection..."
                        : "Check connection";
                    if (GUILayout.Button(label))
                    {
                        TestLabSession.ClearSaveRequestFeedback();
                        TestAuthoringHealthCheck.CheckNow();
                    }
                }
                using (new EditorGUI.DisabledScope(
                           connectionBusy || !connection.CanDisconnect))
                {
                    if (GUILayout.Button("Clear key", EditorStyles.miniButton))
                    {
                        TestLabPreferences.AuthoringApiKey = "";
                        TestAuthoringHealthCheck.OnPreferencesChanged();
                        TestLabSession.ClearSaveRequestFeedback();
                        _saveError = "";
                        GUI.FocusControl(null);
                    }
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        static string SafePasswordField(string label, string value)
        {
            try
            {
                return EditorGUILayout.PasswordField(label, value ?? "");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Unity can retain a cursor position from a previously focused IMGUI
                // text field after the window changes shape or scripts reload.
                GUI.FocusControl(null);
                return value ?? "";
            }
        }

        void DrawSavedTestLibrary(TestAuthoringController controller)
        {
            EditorGUILayout.LabelField("Saved Tests", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (!TestAuthoringHealthCheck.IsConnected)
            {
                EditorGUILayout.LabelField(
                    "Check the Editor connection to browse immutable Test Versions.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(8);
                return;
            }

            var catalogOperation = TestLabSession.CatalogOperation;
            bool catalogLoading = catalogOperation?.State == SavedTestCatalogState.Loading;
            bool replayBusy = TestLabSession.IsSavedReplayBusy;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           catalogLoading || replayBusy || TestLabSession.IsSaveBusy))
                {
                    string label = TestLabSession.SavedTestCatalog == null
                        ? "Load saved Tests"
                        : "Refresh saved Tests";
                    if (GUILayout.Button(label))
                    {
                        _savedReplayError = "";
                        if (!TestLabSession.RefreshSavedTests(out _savedReplayError))
                            Repaint();
                    }
                }
                if (catalogLoading && GUILayout.Button("Cancel", EditorStyles.miniButton))
                    catalogOperation.Cancel();
            }

            if (catalogOperation != null)
            {
                MessageType type = catalogOperation.State == SavedTestCatalogState.Failed
                    ? MessageType.Error
                    : catalogOperation.State == SavedTestCatalogState.Cancelled
                        ? MessageType.Warning
                        : MessageType.Info;
                EditorGUILayout.HelpBox(catalogOperation.Status, type);
            }

            var replayOperation = TestLabSession.SavedReplayOperation;
            if (replayOperation != null)
            {
                MessageType type = replayOperation.State == SavedTestReplayDownloadState.Failed
                    ? MessageType.Error
                    : replayOperation.State == SavedTestReplayDownloadState.Cancelled
                        ? MessageType.Warning
                        : MessageType.Info;
                EditorGUILayout.HelpBox(replayOperation.Status, type);
                if (!replayOperation.IsFinished &&
                    GUILayout.Button("Cancel replay download", EditorStyles.miniButton))
                    TestLabSession.CancelSavedReplay();
            }

            string replayError = !string.IsNullOrWhiteSpace(_savedReplayError)
                ? _savedReplayError
                : TestLabSession.SavedReplayRequestError;
            if (!string.IsNullOrWhiteSpace(replayError))
                EditorGUILayout.HelpBox(replayError, MessageType.Error);

            var catalog = TestLabSession.SavedTestCatalog;
            if (catalog != null)
            {
                foreach (var test in catalog.Tests)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(test.Name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        test.Status + ", " + test.Versions.Count + " immutable version" +
                        (test.Versions.Count == 1 ? "" : "s"),
                        EditorStyles.miniLabel);
                    foreach (var version in test.Versions)
                        DrawSavedVersion(version, controller);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        void DrawSavedVersion(
            SavedTestVersion version,
            TestAuthoringController controller)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            HealthRow("Version", version.VersionNumber.ToString());
            HealthRow("Definition", "Contract v" + version.ContractVersion);
            var compatibility = TestLabSession.ReplayCompatibility(version);
            if (compatibility.IsCompatible)
            {
                EditorGUILayout.HelpBox(
                    "Compatible with the active Unity project, scene, provider, and input setup.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(compatibility.Reason, MessageType.Error);
            }
            string localStateBlock = TestLabSession.SavedReplayLocalStateBlockedReason;
            bool canReplay = compatibility.IsCompatible &&
                             controller != null &&
                             EditorApplication.isPlaying &&
                             string.IsNullOrWhiteSpace(localStateBlock) &&
                             !TestLabSession.IsSaveBusy &&
                             !TestLabSession.IsSavedReplayBusy;
            using (new EditorGUI.DisabledScope(!canReplay))
            {
                if (GUILayout.Button(
                        "Replay saved version " + version.VersionNumber,
                        GUILayout.Height(24)))
                {
                    _savedReplayError = "";
                    if (!TestLabSession.StartSavedReplay(version, out _savedReplayError))
                        Repaint();
                }
            }
            if (compatibility.IsCompatible && !string.IsNullOrWhiteSpace(localStateBlock))
            {
                EditorGUILayout.LabelField(
                    localStateBlock,
                    EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndVertical();
        }

        static void DrawHealth(TestAuthoringController controller)
        {
            EditorGUILayout.LabelField("Health", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            HealthRow("Environment", EditorApplication.isPlaying ? "Play Mode" : "Not in Play Mode");
            HealthRow(
                "State provider",
                TestLabSession.Registry.HasProvider
                    ? TestLabSession.Registry.ProviderId + " v" + TestLabSession.Registry.StateFormatVersion
                    : "Not registered");
            HealthRow(
                "Outcome check",
                TestLabSession.Registry.HasOutcomeProvider
                    ? "Game-owned signal available"
                    : "Human review");
            if (TestLabSession.Registry.ProviderId == SceneReloadStateProvider.Id)
                EditorGUILayout.HelpBox(SceneReloadStateProvider.Limitation, MessageType.Info);
            HealthRow(
                "Local runner",
                TestLabSession.IsConnectedRunBusy
                    ? "Executing a Run from Qamel"
                    : TestLabSession.IsConnectedRunnerReady
                        ? (TestLabSession.ConnectedRunnerStatus ?? "Starting")
                        : "Not ready");
            if (TestLabSession.IsBrowserTestCreationActive)
            {
                HealthRow(
                    "Browser Test creation",
                    TestLabSession.BrowserTestCreationStatus ?? "Starting");
                if (TestLabSession.CanStopBrowserTestCreationLocally &&
                    GUILayout.Button(
                        "Emergency stop browser recording",
                        EditorStyles.miniButton))
                {
                    TestLabSession.StopBrowserTestCreationLocally();
                }
            }
            if (!string.IsNullOrWhiteSpace(TestLabSession.BrowserTestCreationError))
            {
                EditorGUILayout.HelpBox(
                    TestLabSession.BrowserTestCreationError,
                    MessageType.Error);
            }
            if (!string.IsNullOrWhiteSpace(TestLabSession.ConnectedRunnerError))
            {
                EditorGUILayout.HelpBox(
                    TestLabSession.ConnectedRunnerError,
                    TestLabSession.ConnectedRunnerBlockedReason != null
                        ? MessageType.Info
                        : MessageType.Error);
            }
            HealthRow(
                "Input adapter",
                TestLabSession.InputAvailable ? "New Input System ready" : "Unavailable");

            if (controller != null)
            {
                var metrics = controller.InputMetrics;
                var layouts = metrics.DeviceLayouts.Count > 0
                    ? string.Join(", ", metrics.DeviceLayouts.ToArray())
                    : "No devices yet";
                HealthRow(
                    "Input buffer",
                    controller.IsBackgroundRecording
                        ? $"Recording, {metrics.RetainedStateEventCount} input events, " +
                          $"{metrics.RetainedEventCount} trace events, " +
                          $"{FormatBytes(metrics.RetainedEventBytes)} of " +
                          $"{FormatBytes(metrics.MaximumBytes)}, {layouts}"
                        : "Not recording");
                if (metrics.OverwroteEvents)
                    EditorGUILayout.HelpBox(
                        "The input ring overwrote the beginning of this flow. " +
                        "Capture a new starting state.",
                        MessageType.Warning);

                if (controller.IsRunActive || controller.State == TestAuthoringControllerState.NeedsReview)
                {
                    var diagnostics = controller.ReplayDiagnostics;
                    HealthRow(
                        "Replay adapter",
                        diagnostics.ReplayMode + ", " +
                        diagnostics.ReplayedEventCount + " input events");
                }
            }

            var runner = QamelRunner.Instance;
            HealthRow(
                "Session evidence",
                runner != null && runner.HasActiveSession
                    ? "Capture clock available"
                    : "Optional capture evidence unavailable");
            if (runner != null && runner.HasActiveSession)
            {
                HealthRow(
                    "Game performance",
                    runner.CurrentFramesPerSecond.ToString("0.0") + " fps");
                if (runner.TryGetCaptureHealth(out var captureHealth))
                {
                    var drops = captureHealth.DropInflight + captureHealth.DropEncodeQueue;
                    var errors = captureHealth.ReadbackErrors + captureHealth.EncodeErrors;
                    HealthRow(
                        "Visual capture",
                        captureHealth.Kept + "/" + captureHealth.Attempted +
                        " frames kept, " + drops + " drops, " + errors + " errors");
                }
            }
            HealthRow(
                "Gameplay focus",
                TestLabInputFocusPolicy.IsGameViewFocused
                    ? "Game view focused, gameplay input is recording"
                    : "Not recording Editor interactions");
            EditorGUILayout.EndVertical();

            if (EditorApplication.isPlaying && controller != null &&
                controller.IsBackgroundRecording && !TestLabInputFocusPolicy.IsGameViewFocused)
            {
                EditorGUILayout.HelpBox(
                    "Click the Game view before demonstrating the flow. Test Lab and other " +
                    "Editor-window input is deliberately excluded from the draft.",
                    MessageType.Warning);
            }

            if (EditorApplication.isPlaying && !TestLabSession.Registry.HasProvider)
            {
                EditorGUILayout.HelpBox(
                    "Register your game-owned provider during Play Mode with " +
                    "TestStateRegistry.Global.Register(provider), and unregister the same " +
                    "instance during teardown.",
                    MessageType.Info);
            }
            if (!string.IsNullOrWhiteSpace(controller?.LastError))
                EditorGUILayout.HelpBox(controller.LastError, MessageType.Error);
            if (controller?.State == TestAuthoringControllerState.ReplayCountdown ||
                controller?.State == TestAuthoringControllerState.ReplayRunning)
            {
                EditorGUILayout.HelpBox(
                    "Keep your hands off the keyboard, mouse, and gamepad during replay. " +
                    "Physical input is monitored and will be reported as possible contamination.",
                    MessageType.Warning);
            }
            EditorGUILayout.Space(8);
        }

        void DrawDraftForm()
        {
            EditorGUILayout.LabelField("Draft details", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Test name");
            _draftName = EditorGUILayout.TextField(_draftName);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Expected outcome");
            _expectedOutcome = EditorGUILayout.TextArea(
                _expectedOutcome,
                GUILayout.MinHeight(64));
            EditorGUILayout.LabelField(
                "Describe what should be true after the recorded actions.",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        static void DrawDraft(RecordedTestDraft draft, string sourceLabel)
        {
            EditorGUILayout.LabelField(draft.Name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                sourceLabel,
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            DrawStage(
                "1", "Setup", "Restore starting state",
                draft.Anchor.StateLabel + "\n" +
                draft.Anchor.ProviderId + " v" + draft.Anchor.StateFormatVersion +
                ", local state fingerprint " + draft.Anchor.PayloadChecksum);
            DrawStage(
                "2", "Action", "Replay recorded actions",
                draft.InputTrace.Metrics.RetainedStateEventCount + " device-state events, " +
                FormatBytes(draft.InputTrace.Metrics.RetainedEventBytes) + ", " +
                draft.InputTrace.Metrics.RetainedDurationSeconds.ToString("0.###") + " seconds\n" +
                "Devices: " + string.Join(", ", draft.InputTrace.Metrics.DeviceLayouts.ToArray()));
            if (draft.ExpectedObservation != null)
            {
                DrawStage(
                    "3", "Check", "Compare game outcome",
                    draft.ExpectedOutcome + "\nExpected signal: " +
                    (draft.ExpectedObservation.Observation.Summary ??
                     draft.ExpectedObservation.Observation.Value));
            }
            else
            {
                DrawStage(
                    "3", "Check", "Review expected outcome",
                    draft.ExpectedOutcome +
                    "\nHuman review: Worked, Did not work, or Could not tell");
            }
            EditorGUILayout.Space(8);
        }

        static void DrawLoadedSavedVersion()
        {
            EditorGUILayout.LabelField("Immutable Qamel version", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "Replay uses the exact verified state and input bytes attached to Test " +
                "version " + TestLabSession.LoadedSavedVersionNumber +
                ". Local replay results and review notes are not uploaded yet.",
                EditorStyles.wordWrappedMiniLabel);
            if (!TestLabSession.CanReplayCurrentDraft)
            {
                EditorGUILayout.HelpBox(
                    TestLabSession.CurrentDraftReplayBlockedReason,
                    MessageType.Error);
            }
            if (!string.IsNullOrWhiteSpace(TestLabSession.LoadedSavedTestUrl) &&
                GUILayout.Button("Open in Qamel", GUILayout.Height(24)))
            {
                Application.OpenURL(TestLabSession.LoadedSavedTestUrl);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        void DrawCloudSave(RecordedTestDraft draft)
        {
            EditorGUILayout.LabelField("Save durable Test", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                "Save registers the definition and declared SHA-256 values, then uploads " +
                "the exact in-memory starting state and input trace. Qamel confirms each " +
                "private object's presence and byte size before marking it uploaded. " +
                "Local replay results and review notes are not uploaded yet.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            var operation = TestLabSession.SaveOperation;
            var controller = TestLabSession.Controller;
            var cloud = TestLabCloudSaveModel.Create(
                TestAuthoringHealthCheck.IsConnected || TestAuthoringHealthCheck.CanCheck,
                draft.DraftId,
                operation,
                TestLabSession.IsSavePending,
                controller != null &&
                (controller.IsRunActive ||
                 controller.State == TestAuthoringControllerState.NeedsReview));
            bool sameDraft = operation != null && operation.DraftId == draft.DraftId;
            if (operation != null && cloud.ShowOperationStatus)
            {
                MessageType type = SaveMessageType(operation.State);
                string prefix = sameDraft ? "" : "Another draft save: ";
                EditorGUILayout.HelpBox(prefix + operation.Status, type);
            }
            if (sameDraft)
            {
                HealthRow(
                    "Starting state",
                    FormatBytes(operation.StateByteCount) + ", SHA-256 " +
                    operation.StateSha256);
                HealthRow(
                    "Input trace",
                    FormatBytes(operation.InputTraceByteCount) + ", SHA-256 " +
                    operation.InputTraceSha256);
            }
            if (!string.IsNullOrWhiteSpace(TestLabSession.SaveRequestStatus))
                EditorGUILayout.HelpBox(TestLabSession.SaveRequestStatus, MessageType.Info);
            string saveError = !string.IsNullOrWhiteSpace(_saveError)
                ? _saveError
                : TestLabSession.SaveRequestError;
            if (!string.IsNullOrWhiteSpace(saveError))
                EditorGUILayout.HelpBox(saveError, MessageType.Error);

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!cloud.CanSave))
            {
                if (GUILayout.Button(cloud.ButtonLabel, GUILayout.Height(28)))
                {
                    _saveError = "";
                    if (!TestLabSession.SaveCurrentDraft(out _saveError))
                        Repaint();
                }
            }
            if (!cloud.CanSave && !string.IsNullOrWhiteSpace(cloud.DisabledReason))
            {
                EditorGUILayout.LabelField(
                    cloud.DisabledReason,
                    EditorStyles.wordWrappedMiniLabel);
            }
            if (cloud.ShowCancel && GUILayout.Button("Cancel save", EditorStyles.miniButton))
                TestLabSession.CancelSave();
            if (cloud.ShowOpen && GUILayout.Button("Open in Qamel", GUILayout.Height(24)))
                Application.OpenURL(cloud.OpenUrl);

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        static void DrawDetachedCloudSave(TestDefinitionSaveOperation operation)
        {
            EditorGUILayout.LabelField("Last Test save", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                operation.Status,
                SaveMessageType(operation.State));
            HealthRow(
                "Starting state",
                FormatBytes(operation.StateByteCount) + ", SHA-256 " +
                operation.StateSha256);
            HealthRow(
                "Input trace",
                FormatBytes(operation.InputTraceByteCount) + ", SHA-256 " +
                operation.InputTraceSha256);

            if (!operation.IsFinished &&
                GUILayout.Button("Cancel save", EditorStyles.miniButton))
                TestLabSession.CancelSave();
            if (operation.State == TestDefinitionSaveState.Succeeded &&
                !string.IsNullOrWhiteSpace(operation.TestUrl) &&
                GUILayout.Button("Open in Qamel", GUILayout.Height(24)))
                Application.OpenURL(operation.TestUrl);
            if ((operation.State == TestDefinitionSaveState.Failed ||
                 operation.State == TestDefinitionSaveState.Cancelled) &&
                !EditorApplication.isPlaying)
            {
                EditorGUILayout.LabelField(
                    "Enter Play Mode and recreate the draft before retrying this save.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        static void DrawLastSavedTest(TestLabSavedTestReceipt receipt)
        {
            EditorGUILayout.LabelField("Last saved Test", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                "This Test is durable in Qamel. The prior local draft and replay history " +
                "ended with its Play Mode session.",
                MessageType.Info);
            HealthRow("Test", receipt.TestName);
            HealthRow("Version", receipt.VersionNumber.ToString());
            HealthRow("Project", receipt.ProjectName);
            if (GUILayout.Button("Open in Qamel", GUILayout.Height(24)))
                Application.OpenURL(receipt.TestUrl);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        static MessageType SaveMessageType(TestDefinitionSaveState state)
        {
            if (state == TestDefinitionSaveState.Failed)
                return MessageType.Error;
            if (state == TestDefinitionSaveState.Succeeded)
                return MessageType.Info;
            if (state == TestDefinitionSaveState.Cancelled)
                return MessageType.Warning;
            return MessageType.None;
        }

        static void DrawValidationHistory(RecordedTestDraft draft)
        {
            if (draft.ValidationHistory.Count == 0)
                return;

            EditorGUILayout.LabelField("Validation history", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "These replay results and notes are local to the current Play Mode session.",
                EditorStyles.wordWrappedMiniLabel);
            for (var index = draft.ValidationHistory.Count - 1; index >= 0; index--)
            {
                var run = draft.ValidationHistory[index];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(
                    "Run " + run.AttemptNumber + ": " + FormatRunStatus(run.Status),
                    EditorStyles.boldLabel);
                HealthRow(
                    "State",
                    FormatStageStatus(run.StateStageStatus) + ", " +
                    run.StateDurationSeconds.ToString("0.###") + " seconds");
                HealthRow(
                    "Actions",
                    FormatStageStatus(run.ActionStageStatus) + ", " +
                    run.ActionDurationSeconds.ToString("0.###") + " seconds");
                HealthRow("Check", FormatStageStatus(run.CheckStageStatus));
                HealthRow(
                    "Replay mode",
                    run.AdapterDiagnostics.ReplayMode + ", " +
                    run.AdapterDiagnostics.ReplayedEventCount + " input events");
                var mappings = run.AdapterDiagnostics.DeviceMappings.Count > 0
                    ? string.Join(", ", run.AdapterDiagnostics.DeviceMappings.ToArray())
                    : "No device mappings recorded";
                HealthRow("Devices", mappings);
                if (run.ReplayEvidence != null)
                {
                    HealthRow(
                        "Evidence",
                        run.ReplayEvidence.HasSessionTimes
                            ? run.ReplayEvidence.DurationSeconds.ToString("0.###") +
                              " seconds on capture clock"
                            : "Optional capture clock unavailable");
                }
                if (run.AdapterDiagnostics.PhysicalInputDetected)
                {
                    EditorGUILayout.HelpBox(
                        run.AdapterDiagnostics.PhysicalInputEventCount +
                        " possible physical input event(s) occurred during replay. " +
                        "Treat this run as potentially contaminated.",
                        MessageType.Warning);
                }
                if (!string.IsNullOrWhiteSpace(run.Error))
                    EditorGUILayout.HelpBox(run.Error, MessageType.Error);
                if (run.OutcomeComparison != null)
                {
                    HealthRow(
                        "Expected outcome signal",
                        run.OutcomeComparison.Expected.Summary ??
                        run.OutcomeComparison.Expected.Value);
                    HealthRow(
                        "Observed outcome signal",
                        run.OutcomeComparison.Actual.Summary ??
                        run.OutcomeComparison.Actual.Value);
                }
                if (!string.IsNullOrWhiteSpace(run.ReviewNote))
                    EditorGUILayout.LabelField("Note: " + run.ReviewNote, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
            EditorGUILayout.Space(4);
        }

        void DrawReview(RecordedTestDraft draft, LocalTestRun run)
        {
            EditorGUILayout.LabelField("Review expected outcome", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(draft.ExpectedOutcome, EditorStyles.wordWrappedLabel);
            if (run.AdapterDiagnostics.PhysicalInputDetected)
            {
                EditorGUILayout.HelpBox(
                    "Possible physical input was detected. Choose Could not tell if the " +
                    "live result is not trustworthy.",
                    MessageType.Warning);
            }
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Optional note");
            _reviewNote = EditorGUILayout.TextArea(_reviewNote, GUILayout.MinHeight(44));
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Worked"))
                    SubmitVerdict(TestHumanVerdict.Worked);
                if (GUILayout.Button("Did not work"))
                    SubmitVerdict(TestHumanVerdict.DidNotWork);
                if (GUILayout.Button("Could not tell"))
                    SubmitVerdict(TestHumanVerdict.CouldNotTell);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(8);
        }

        void SubmitVerdict(TestHumanVerdict verdict)
        {
            if (TestLabSession.RecordVerdict(verdict, _reviewNote))
                _reviewNote = "";
        }

        static void DrawStage(string number, string role, string title, string details)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(number + ". " + title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(role.ToUpperInvariant(), EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(details, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        void DrawPrimaryAction(TestLabWindowModel model)
        {
            bool saveBlocksReplay =
                model.PrimaryAction == TestLabPrimaryAction.Replay &&
                TestLabSession.IsSaveBusy;
            bool downloadBlocksAction = TestLabSession.IsSavedReplayBusy;
            bool connectedRunBlocksAction = TestLabSession.IsConnectedRunBusy &&
                                            model.PrimaryAction !=
                                            TestLabPrimaryAction.CancelRun;
            bool browserCreationBlocksAction =
                TestLabSession.IsBrowserTestCreationActive;
            bool savedBindingBlocksReplay =
                model.PrimaryAction == TestLabPrimaryAction.Replay &&
                !TestLabSession.CanReplayCurrentDraft;
            using (new EditorGUI.DisabledScope(
                       !model.PrimaryEnabled || saveBlocksReplay || downloadBlocksAction ||
                       savedBindingBlocksReplay || connectedRunBlocksAction ||
                       browserCreationBlocksAction))
            {
                if (GUILayout.Button(model.PrimaryLabel, GUILayout.Height(32)))
                    Execute(model.PrimaryAction);
            }
            if (saveBlocksReplay)
            {
                EditorGUILayout.HelpBox(
                    "Finish or cancel the current Qamel save before replaying this draft.",
                    MessageType.Info);
            }
            else if (downloadBlocksAction)
            {
                EditorGUILayout.HelpBox(
                    "Finish or cancel the saved Test download before changing Test Lab state.",
                    MessageType.Info);
            }
            else if (connectedRunBlocksAction)
            {
                EditorGUILayout.HelpBox(
                    "Qamel is executing a web-requested Run. Wait for it to finish before " +
                    "starting another Test Lab action.",
                    MessageType.Info);
            }
            else if (browserCreationBlocksAction)
            {
                EditorGUILayout.HelpBox(
                    "This Test is being created from Qamel in the browser. Use the browser " +
                    "controls to stop, review, save, or cancel it.",
                    MessageType.Info);
            }
            else if (!model.PrimaryEnabled &&
                     !string.IsNullOrWhiteSpace(model.DisabledReason))
            {
                EditorGUILayout.HelpBox(model.DisabledReason, MessageType.Info);
            }
        }

        void Execute(TestLabPrimaryAction action)
        {
            switch (action)
            {
                case TestLabPrimaryAction.EnterPlayMode:
                    EditorApplication.isPlaying = true;
                    break;
                case TestLabPrimaryAction.CaptureState:
                    TestLabSession.CaptureStartingState();
                    break;
                case TestLabPrimaryAction.CreateDraft:
                    TestLabSession.CreateDraft(_draftName, _expectedOutcome, out _);
                    break;
                case TestLabPrimaryAction.CancelCapture:
                    TestLabSession.CancelStateCapture();
                    break;
                case TestLabPrimaryAction.Replay:
                    TestLabSession.StartReplay();
                    break;
                case TestLabPrimaryAction.CancelRun:
                    TestLabSession.CancelRun();
                    break;
            }
        }

        static string FormatRunStatus(LocalTestRunStatus status)
        {
            switch (status)
            {
                case LocalTestRunStatus.AwaitingReview:
                    return "Awaiting review";
                case LocalTestRunStatus.NeedsReview:
                    return "Could not tell";
                case LocalTestRunStatus.Pass:
                    return "Worked";
                case LocalTestRunStatus.Fail:
                    return "Did not work";
                default:
                    return status.ToString();
            }
        }

        static string FormatStageStatus(LocalTestStageStatus status)
        {
            return status == LocalTestStageStatus.NeedsReview
                ? "Could not tell"
                : status.ToString();
        }

        static MessageType NoticeType(TestLabNoticeKind kind)
        {
            switch (kind)
            {
                case TestLabNoticeKind.Warning:
                    return MessageType.Warning;
                case TestLabNoticeKind.Error:
                    return MessageType.Error;
                case TestLabNoticeKind.Info:
                    return MessageType.Info;
                default:
                    return MessageType.None;
            }
        }

        static void HealthRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(110));
                EditorGUILayout.LabelField(value, EditorStyles.wordWrappedMiniLabel);
            }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return (bytes / (1024d * 1024d)).ToString("0.##") + " MB";
            if (bytes >= 1024)
                return (bytes / 1024d).ToString("0.##") + " KB";
            return bytes + " B";
        }

        static GUIStyle _largeTitle;
        static GUIStyle LargeTitle => _largeTitle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 20,
            wordWrap = true
        };
    }
}
