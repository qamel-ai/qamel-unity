using QamelCapture.TestAuthoring;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum TestLabPrimaryAction
    {
        None,
        Enable,
        EnterPlayMode,
        CaptureState,
        CreateDraft,
        CancelCapture,
        Replay,
        CancelRun
    }

    internal sealed class TestLabWindowModel
    {
        public string Title;
        public string Guidance;
        public string PrimaryLabel;
        public TestLabPrimaryAction PrimaryAction;
        public bool PrimaryEnabled;
        public string DisabledReason;
        public bool ShowDraftForm;
        public bool ShowDraft;
        public bool ShowRun;
        public bool ShowReview;
        public bool CanClearDraft;

        public static TestLabWindowModel Create(
            bool featureEnabled,
            bool isPlaying,
            bool inputAvailable,
            string inputUnavailableReason,
            bool hasProvider,
            TestAuthoringController controller,
            string draftName,
            string expectedOutcome)
        {
            if (!featureEnabled)
            {
                return EnabledAction(
                    "Experimental Test Lab",
                    "Enable the local, session-only recorded-test authoring trial for this project.",
                    "Enable experimental Test Lab",
                    TestLabPrimaryAction.Enable);
            }
            if (!isPlaying)
            {
                return EnabledAction(
                    "Not in Play Mode",
                    "Test Lab runs beside the live game and keeps all draft data in memory.",
                    "Enter Play Mode",
                    TestLabPrimaryAction.EnterPlayMode);
            }
            if (controller?.CurrentDraft != null &&
                controller.State == TestAuthoringControllerState.AnchorExpired)
            {
                return RecoverableDraftError(
                    "Starting state expired",
                    controller.LastError ?? "The complete input range is no longer retained.",
                    "Capture new starting state",
                    controller);
            }
            if (controller?.CurrentDraft != null &&
                controller.State == TestAuthoringControllerState.Error)
            {
                return RecoverableDraftError(
                    "Test Lab needs attention",
                    controller.LastError ?? "The last authoring operation could not complete.",
                    "Retry starting-state capture",
                    controller);
            }
            if (!inputAvailable)
            {
                return Disabled(
                    "Input adapter unavailable",
                    inputUnavailableReason ?? "Unity's newer Input System is required.",
                    "Capture starting state",
                    TestLabPrimaryAction.CaptureState);
            }
            if (!hasProvider)
            {
                return Disabled(
                    "No state provider",
                    "Register one game-owned IQamelTestStateProvider during Play Mode.",
                    "Capture starting state",
                    TestLabPrimaryAction.CaptureState);
            }
            if (controller == null)
            {
                return Disabled(
                    "Starting Test Lab",
                    "The local authoring controller is initializing.",
                    "Capture starting state",
                    TestLabPrimaryAction.CaptureState);
            }

            switch (controller.State)
            {
                case TestAuthoringControllerState.Idle:
                    return EnabledAction(
                        "Ready for a starting state",
                        "Put the game in the state where this test should begin.",
                        "Capture starting state",
                        TestLabPrimaryAction.CaptureState);
                case TestAuthoringControllerState.CapturingStartingState:
                    return EnabledAction(
                        "Capturing starting state",
                        "The game-owned provider is preparing an opaque state anchor.",
                        "Cancel",
                        TestLabPrimaryAction.CancelCapture);
                case TestAuthoringControllerState.ReadyForDemonstration:
                    return Disabled(
                        "Ready to demonstrate",
                        "The game is paused. Use Start demonstration in the Game view when ready.",
                        "Start in Game view",
                        TestLabPrimaryAction.None);
                case TestAuthoringControllerState.PreparingDemonstration:
                    return Disabled(
                        "Preparing starting state",
                        "Qamel is restoring the captured state. Recording begins when it is ready.",
                        "Starting demonstration",
                        TestLabPrimaryAction.None);
                case TestAuthoringControllerState.Recording:
                    return DraftForm(controller, draftName, expectedOutcome);
                case TestAuthoringControllerState.DraftReady:
                    return new TestLabWindowModel
                    {
                        Title = "Draft ready",
                        Guidance = controller.CurrentDraft.ValidationHistory.Count == 0
                            ? "Inspect the three stages, then replay. Qamel will focus the Game view."
                            : "Replay the same draft again or inspect its validation history below.",
                        PrimaryLabel = controller.CurrentDraft.ValidationHistory.Count == 0
                            ? "Replay draft locally"
                            : "Replay draft locally again",
                        PrimaryAction = TestLabPrimaryAction.Replay,
                        PrimaryEnabled = true,
                        ShowDraft = true,
                        ShowRun = controller.CurrentRun != null,
                        CanClearDraft = true
                    };
                case TestAuthoringControllerState.Restoring:
                    return ActiveRun(
                        "Restoring starting state",
                        "The game-owned provider is restoring the captured anchor.",
                        controller);
                case TestAuthoringControllerState.Settling:
                    return ActiveRun(
                        "State restored",
                        "Waiting one game frame before the replay countdown.",
                        controller);
                case TestAuthoringControllerState.ReplayCountdown:
                    return ActiveRun(
                        "Replay starts in " +
                        controller.ReplayCountdownRemainingSeconds.ToString("0.0") + " seconds",
                        "The restored game is paused while Qamel focuses the Game view. " +
                        "Keep your hands off input devices.",
                        controller);
                case TestAuthoringControllerState.ReplayRunning:
                    return ActiveRun(
                        "Replaying recorded actions",
                        "Watch the Game view and keep your hands off input devices.",
                        controller);
                case TestAuthoringControllerState.NeedsReview:
                    return new TestLabWindowModel
                    {
                        Title = "Review the outcome",
                        Guidance = "Compare the live result with the expected outcome below.",
                        ShowDraft = true,
                        ShowRun = true,
                        ShowReview = true
                    };
                case TestAuthoringControllerState.ReplayError:
                    return CompletedRun(
                        "Replay could not complete",
                        controller.LastError ?? "A state or input error stopped the replay.",
                        controller);
                case TestAuthoringControllerState.ReplayCancelled:
                    return CompletedRun(
                        "Replay cancelled",
                        "The run was cleaned up and the game remains interactive.",
                        controller);
                case TestAuthoringControllerState.AnchorExpired:
                    return RecoverableDraftError(
                        "Starting state expired",
                        controller.LastError ?? "The complete input range is no longer retained.",
                        "Capture new starting state",
                        controller);
                case TestAuthoringControllerState.Error:
                    return RecoverableDraftError(
                        "Test Lab needs attention",
                        controller.LastError ?? "The last authoring operation could not complete.",
                        "Retry starting-state capture",
                        controller);
                default:
                    return Disabled(
                        "Test Lab is busy",
                        "Finish or cancel the current operation before authoring another draft.",
                        "Please wait",
                        TestLabPrimaryAction.None);
            }
        }

        static TestLabWindowModel ActiveRun(
            string title,
            string guidance,
            TestAuthoringController controller)
        {
            return new TestLabWindowModel
            {
                Title = title,
                Guidance = guidance,
                PrimaryLabel = "Cancel",
                PrimaryAction = TestLabPrimaryAction.CancelRun,
                PrimaryEnabled = true,
                ShowDraft = controller.CurrentDraft != null,
                ShowRun = controller.CurrentRun != null
            };
        }

        static TestLabWindowModel RecoverableDraftError(
            string title,
            string guidance,
            string primaryLabel,
            TestAuthoringController controller)
        {
            var model = EnabledAction(
                title,
                guidance,
                primaryLabel,
                TestLabPrimaryAction.CaptureState);
            model.ShowDraft = controller.CurrentDraft != null;
            model.ShowRun = controller.CurrentRun != null;
            model.CanClearDraft = controller.CurrentDraft != null;
            return model;
        }

        static TestLabWindowModel CompletedRun(
            string title,
            string guidance,
            TestAuthoringController controller)
        {
            return new TestLabWindowModel
            {
                Title = title,
                Guidance = guidance,
                PrimaryLabel = "Replay draft locally again",
                PrimaryAction = TestLabPrimaryAction.Replay,
                PrimaryEnabled = true,
                ShowDraft = controller.CurrentDraft != null,
                ShowRun = controller.CurrentRun != null,
                CanClearDraft = true
            };
        }

        static TestLabWindowModel DraftForm(
            TestAuthoringController controller,
            string draftName,
            string expectedOutcome)
        {
            var model = new TestLabWindowModel
            {
                Title = "Recording from starting state",
                Guidance = "Qamel pauses outside the Game view. Demonstrate the flow there, " +
                           "then return here to describe the draft.",
                PrimaryLabel = "Create draft from recent play",
                PrimaryAction = TestLabPrimaryAction.CreateDraft,
                PrimaryEnabled = true,
                ShowDraftForm = true
            };

            if (!controller.CanCreateDraft)
            {
                model.PrimaryEnabled = false;
                model.DisabledReason = controller.InputMetrics.OverwroteEvents
                    ? "The ring overwrote the beginning of this flow. Capture a new starting state."
                    : "No complete input range is available yet.";
            }
            else if (string.IsNullOrWhiteSpace(draftName))
            {
                model.PrimaryEnabled = false;
                model.DisabledReason = "Enter a test name.";
            }
            else if (string.IsNullOrWhiteSpace(expectedOutcome))
            {
                model.PrimaryEnabled = false;
                model.DisabledReason = "Describe what should be true after the actions.";
            }
            return model;
        }

        static TestLabWindowModel EnabledAction(
            string title,
            string guidance,
            string label,
            TestLabPrimaryAction action)
        {
            return new TestLabWindowModel
            {
                Title = title,
                Guidance = guidance,
                PrimaryLabel = label,
                PrimaryAction = action,
                PrimaryEnabled = true
            };
        }

        static TestLabWindowModel Disabled(
            string title,
            string guidance,
            string label,
            TestLabPrimaryAction action)
        {
            return new TestLabWindowModel
            {
                Title = title,
                Guidance = guidance,
                PrimaryLabel = label,
                PrimaryAction = action,
                PrimaryEnabled = false,
                DisabledReason = guidance
            };
        }
    }

    internal enum TestLabNoticeKind
    {
        None,
        Info,
        Warning,
        Error,
    }

    internal sealed class TestAuthoringConnectionModel
    {
        public bool CanCheck;
        public bool CanDisconnect;
        public bool IsConnected;
        public string Status;
        public string ProjectId;
        public string ProjectName;
        public TestLabNoticeKind NoticeKind;

        public static TestAuthoringConnectionModel Create(
            string endpoint,
            string apiKey,
            TestAuthoringHealthState state,
            string projectId,
            string projectName,
            string lastError)
        {
            bool hasKey = !string.IsNullOrWhiteSpace(apiKey);
            bool validEndpoint = TestDefinitionRoutes.IsValidBase(endpoint);
            bool validKey = TestDefinitionRoutes.IsValidProjectApiKey(apiKey);
            var model = new TestAuthoringConnectionModel
            {
                CanCheck = validEndpoint && validKey &&
                           state != TestAuthoringHealthState.Checking,
                CanDisconnect = hasKey,
                IsConnected = state == TestAuthoringHealthState.Connected,
                ProjectId = projectId,
                ProjectName = projectName,
                NoticeKind = TestLabNoticeKind.Info,
            };

            if (!hasKey)
            {
                model.Status = "Paste a project API key to connect this project.";
                return model;
            }
            if (!validEndpoint)
            {
                model.Status = "Use an HTTPS Qamel endpoint or HTTP localhost for development.";
                model.NoticeKind = TestLabNoticeKind.Error;
                return model;
            }
            if (!validKey)
            {
                model.Status =
                    "Paste the complete project API key beginning with qa_key_.";
                model.NoticeKind = TestLabNoticeKind.Error;
                return model;
            }

            switch (state)
            {
                case TestAuthoringHealthState.Checking:
                    model.Status = "Checking the authoring connection...";
                    break;
                case TestAuthoringHealthState.Connected:
                    if (string.IsNullOrWhiteSpace(projectId) ||
                        string.IsNullOrWhiteSpace(projectName))
                    {
                        model.IsConnected = false;
                        model.Status = "The connection check did not identify a Qamel project.";
                        model.NoticeKind = TestLabNoticeKind.Error;
                    }
                    else
                    {
                        model.Status = "Connected to " + projectName + ".";
                    }
                    break;
                case TestAuthoringHealthState.Rejected:
                case TestAuthoringHealthState.InvalidConfiguration:
                case TestAuthoringHealthState.InvalidResponse:
                    model.Status = string.IsNullOrWhiteSpace(lastError)
                        ? "Qamel could not verify this authoring connection."
                        : lastError;
                    model.NoticeKind = TestLabNoticeKind.Error;
                    break;
                case TestAuthoringHealthState.Unreachable:
                    model.Status = string.IsNullOrWhiteSpace(lastError)
                        ? "Qamel is currently unreachable."
                        : lastError;
                    model.NoticeKind = TestLabNoticeKind.Warning;
                    break;
                default:
                    model.Status = "This authoring connection has not been checked yet.";
                    break;
            }
            return model;
        }
    }

    internal sealed class TestLabCloudSaveModel
    {
        public bool CanSave;
        public bool ShowCancel;
        public bool ShowOpen;
        public bool ShowOperationStatus;
        public string ButtonLabel;
        public string DisabledReason;
        public string OpenUrl;

        public static TestLabCloudSaveModel Create(
            bool canAttemptSave,
            string currentDraftId,
            TestDefinitionSaveOperation operation,
            bool connectionCheckPending,
            bool localValidationBusy = false)
        {
            return CreateForState(
                canAttemptSave,
                currentDraftId,
                operation != null,
                operation?.DraftId,
                operation?.State ?? TestDefinitionSaveState.Cancelled,
                operation?.TestUrl,
                connectionCheckPending,
                localValidationBusy);
        }

        internal static TestLabCloudSaveModel CreateForState(
            bool canAttemptSave,
            string currentDraftId,
            bool hasOperation,
            string operationDraftId,
            TestDefinitionSaveState operationState,
            string testUrl,
            bool connectionCheckPending = false,
            bool localValidationBusy = false)
        {
            bool sameDraft = hasOperation && string.Equals(
                currentDraftId,
                operationDraftId,
                System.StringComparison.Ordinal);
            bool saving = hasOperation &&
                          (operationState == TestDefinitionSaveState.Registering ||
                           operationState == TestDefinitionSaveState.Uploading ||
                           operationState == TestDefinitionSaveState.Confirming);
            bool canOpen = sameDraft &&
                           operationState == TestDefinitionSaveState.Succeeded &&
                           !string.IsNullOrWhiteSpace(testUrl);
            bool alreadySaved = sameDraft &&
                                operationState == TestDefinitionSaveState.Succeeded;

            return new TestLabCloudSaveModel
            {
                CanSave = canAttemptSave && !saving && !connectionCheckPending &&
                          !localValidationBusy && !alreadySaved,
                ShowCancel = saving,
                ShowOpen = canOpen,
                ShowOperationStatus = sameDraft || saving,
                ButtonLabel = alreadySaved ? "Saved to Qamel" : "Save to Qamel",
                DisabledReason = connectionCheckPending
                    ? "Checking the Qamel connection before saving."
                    : !canAttemptSave
                        ? "Enter a valid Qamel endpoint and project API key, then check " +
                          "the connection before saving."
                        : saving
                        ? "A Test save is already in progress."
                        : localValidationBusy
                            ? "Finish or review the local replay before saving this draft."
                            : alreadySaved
                                ? "This draft's definition is already saved. Local replay " +
                                  "results and notes are not uploaded yet."
                                : null,
                OpenUrl = canOpen ? testUrl : null,
            };
        }
    }
}
