using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using QamelCapture.TestAuthoring;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QamelCapture.Editor.TestAuthoring
{
    internal interface ISavedTestReplayEnvironment
    {
        string UnityRelease { get; }
        string ActiveScenePath { get; }
        bool InputAvailable { get; }
        string InputUnavailableReason { get; }
        string InputSystemVersion { get; }
        bool TryInspectAndMapInputTrace(
            byte[] bytes,
            out IReadOnlyList<string> actualDeviceLayouts,
            out string error);
    }

    internal sealed class SavedTestCompatibilityResult
    {
        readonly string[] _blockingReasons;

        public SavedTestCompatibilityResult(IEnumerable<string> blockingReasons)
        {
            _blockingReasons = blockingReasons == null
                ? Array.Empty<string>()
                : blockingReasons.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        }

        public bool IsCompatible => _blockingReasons.Length == 0;
        public IReadOnlyList<string> BlockingReasons => Array.AsReadOnly(_blockingReasons);
        public string Reason => IsCompatible
            ? null
            : string.Join(" ", _blockingReasons);
    }

    internal static class SavedTestReplayCompatibility
    {
        public const int PreviousReplayContractVersion = 2;
        public const int ReplayContractVersion = 3;
        public const string RestoreStateAdapter = "qamel.unity.restore_starting_state";
        public const string InputTraceAdapter = "qamel.unity.replay_input_trace";
        public const string HumanReviewAdapter = "qamel.human.review_expected_outcome";
        public const string OutcomeObservationAdapter =
            "qamel.unity.compare_outcome_observation";

        static readonly string[] SupportedAdapters =
        {
            RestoreStateAdapter,
            InputTraceAdapter,
            HumanReviewAdapter,
            OutcomeObservationAdapter,
        };

        public static SavedTestCompatibilityResult Evaluate(
            SavedTestVersion version,
            TestStateRegistry registry,
            ISavedTestReplayEnvironment environment)
        {
            var reasons = new List<string>();
            if (version == null)
            {
                reasons.Add("Saved Test Version metadata is missing.");
                return new SavedTestCompatibilityResult(reasons);
            }
            if (registry == null || environment == null)
            {
                reasons.Add("The local replay environment is unavailable.");
                return new SavedTestCompatibilityResult(reasons);
            }

            if (version.ContractVersion != PreviousReplayContractVersion &&
                version.ContractVersion != ReplayContractVersion)
            {
                reasons.Add("Test definition contract v" + version.ContractVersion +
                            " is not supported by this plugin.");
                return new SavedTestCompatibilityResult(reasons);
            }

            var requirements = version.Requirements;
            if (requirements == null)
            {
                reasons.Add("This Test Version has no replay compatibility requirements.");
                return new SavedTestCompatibilityResult(reasons);
            }

            if (!SameUnityReleaseFamily(
                    requirements.UnityRelease,
                    environment.UnityRelease))
            {
                reasons.Add(string.IsNullOrWhiteSpace(requirements.UnityRelease)
                    ? "This Test Version does not declare its Unity release."
                    : "This Test Version requires Unity " + requirements.UnityRelease +
                      ", but the open project uses Unity " +
                      (environment.UnityRelease ?? "unknown") + ".");
            }

            ValidateAdapters(requirements.Adapters, reasons);

            if (string.IsNullOrWhiteSpace(requirements.ActiveScenePath))
            {
                reasons.Add("This Test Version does not declare its Unity scene path.");
            }
            else if (!string.Equals(
                         requirements.ActiveScenePath,
                         environment.ActiveScenePath,
                         StringComparison.Ordinal))
            {
                reasons.Add("Open the saved scene '" + requirements.ActiveScenePath +
                            "' in Edit Mode, then enter Play Mode again before replay. " +
                            (string.IsNullOrWhiteSpace(environment.ActiveScenePath)
                                ? "The current scene has no saved asset path."
                                : "The current scene is '" +
                                  environment.ActiveScenePath + "'."));
            }

            if (!registry.HasProvider)
            {
                reasons.Add("Enter Play Mode and register state provider '" +
                            (requirements.StateProviderId ?? "unknown") + "'.");
            }
            else if (!string.Equals(
                         requirements.StateProviderId,
                         registry.ProviderId,
                         StringComparison.Ordinal))
            {
                reasons.Add("The active state provider is '" + registry.ProviderId +
                            "', but this Test Version requires '" +
                            (requirements.StateProviderId ?? "unknown") + "'.");
            }
            else if (requirements.StateFormatVersion != registry.StateFormatVersion)
            {
                reasons.Add("State provider '" + registry.ProviderId + "' uses format v" +
                            registry.StateFormatVersion +
                            ", but this Test Version requires v" +
                            requirements.StateFormatVersion + ".");
            }

            if (!environment.InputAvailable)
            {
                reasons.Add(environment.InputUnavailableReason ??
                            "Unity's newer Input System is unavailable.");
            }
            if (string.IsNullOrWhiteSpace(requirements.InputSystemVersion))
            {
                reasons.Add("This Test Version does not declare its Input System version.");
            }
            else if (!string.Equals(
                         requirements.InputSystemVersion,
                         environment.InputSystemVersion,
                         StringComparison.Ordinal))
            {
                reasons.Add("This Test Version requires Input System " +
                            requirements.InputSystemVersion + ", but the project uses " +
                            (environment.InputSystemVersion ?? "no supported Input System") + ".");
            }

            if (!version.ArtifactsReady)
            {
                reasons.Add("One or more immutable replay artifacts are not uploaded yet.");
            }

            ValidateDetailedDefinition(version, reasons);
            ValidateOutcomeProvider(version, registry, reasons);
            return new SavedTestCompatibilityResult(reasons);
        }

        public static SavedTestCompatibilityResult ValidateDownloadedTrace(
            SavedTestVersion version,
            byte[] traceBytes,
            ISavedTestReplayEnvironment environment)
        {
            var reasons = new List<string>();
            if (version == null || environment == null || traceBytes == null)
            {
                reasons.Add("Downloaded input trace validation could not start.");
                return new SavedTestCompatibilityResult(reasons);
            }

            var action = version.Stages.FirstOrDefault(item => string.Equals(
                item.Adapter,
                InputTraceAdapter,
                StringComparison.Ordinal));
            if (action == null)
            {
                reasons.Add("The downloaded definition has no supported input trace stage.");
                return new SavedTestCompatibilityResult(reasons);
            }
            if (!environment.TryInspectAndMapInputTrace(
                    traceBytes,
                    out var actualLayouts,
                    out var inspectError))
            {
                reasons.Add(inspectError ?? "The downloaded input trace is not replayable.");
                return new SavedTestCompatibilityResult(reasons);
            }

            var declared = action.Configuration.DeviceLayouts
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var actual = (actualLayouts ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (!declared.SequenceEqual(actual, StringComparer.Ordinal))
            {
                reasons.Add(
                    "The downloaded trace contains device layouts [" +
                    string.Join(", ", actual) +
                    "], but the immutable definition declares [" +
                    string.Join(", ", declared) + "].");
            }
            return new SavedTestCompatibilityResult(reasons);
        }

        internal static bool SameUnityReleaseFamily(string left, string right)
        {
            return TryUnityReleaseFamily(left, out var leftFamily) &&
                   TryUnityReleaseFamily(right, out var rightFamily) &&
                   string.Equals(leftFamily, rightFamily, StringComparison.Ordinal);
        }

        static void ValidateAdapters(
            IReadOnlyList<string> adapters,
            ICollection<string> reasons)
        {
            if (adapters == null || adapters.Count != 3)
            {
                reasons.Add(
                    "This plugin can replay exactly one state restore, one recorded input " +
                    "action, and one supported check stage.");
                return;
            }
            for (var index = 0; index < 2; index++)
            {
                if (string.Equals(adapters[index], SupportedAdapters[index], StringComparison.Ordinal))
                    continue;
                reasons.Add("Stage " + (index + 1) + " uses adapter '" +
                            (adapters[index] ?? "missing") +
                            "'; this replay controller requires '" +
                            SupportedAdapters[index] + "'.");
            }
            if (!string.Equals(adapters[2], HumanReviewAdapter, StringComparison.Ordinal) &&
                !string.Equals(
                    adapters[2],
                    OutcomeObservationAdapter,
                    StringComparison.Ordinal))
            {
                reasons.Add("Stage 3 uses unsupported check adapter '" +
                            (adapters[2] ?? "missing") + "'.");
            }
        }

        static void ValidateDetailedDefinition(
            SavedTestVersion version,
            ICollection<string> reasons)
        {
            if (version.Stages.Count == 0 && version.Artifacts.Count == 0 &&
                version.StartingState == null)
                return;
            if (version.Stages.Count != 3)
            {
                reasons.Add("The downloaded definition must contain exactly three stages.");
                return;
            }

            ValidateStage(
                version.Stages[0], 1, "setup", "scripted", RestoreStateAdapter, reasons);
            ValidateStage(
                version.Stages[1], 2, "action", "scripted", InputTraceAdapter, reasons);
            bool automaticCheck = string.Equals(
                version.Stages[2].Adapter,
                OutcomeObservationAdapter,
                StringComparison.Ordinal);
            ValidateStage(
                version.Stages[2],
                3,
                "check",
                automaticCheck ? "scripted" : "human",
                automaticCheck ? OutcomeObservationAdapter : HumanReviewAdapter,
                reasons);

            var state = version.StartingState;
            if (state == null)
            {
                reasons.Add("This Test Version has no starting state.");
                return;
            }
            if (!string.Equals(
                    state.ProviderId,
                    version.Requirements.StateProviderId,
                    StringComparison.Ordinal) ||
                state.FormatVersion != version.Requirements.StateFormatVersion)
            {
                reasons.Add("Starting-state metadata does not match replay requirements.");
            }
            if (!string.Equals(
                    state.ActiveScenePath,
                    version.Requirements.ActiveScenePath,
                    StringComparison.Ordinal))
            {
                reasons.Add("Starting-state scene does not match replay requirements.");
            }

            var stateArtifact = version.FindArtifact(state.PayloadArtifactId);
            if (stateArtifact == null ||
                !string.Equals(stateArtifact.Kind, "state_payload", StringComparison.Ordinal))
            {
                reasons.Add("The starting state does not reference a state payload artifact.");
            }
            var actionArtifact = version.FindArtifact(version.Stages[1].ArtifactId);
            if (actionArtifact == null ||
                !string.Equals(actionArtifact.Kind, "input_trace", StringComparison.Ordinal))
            {
                reasons.Add("The action stage does not reference an input trace artifact.");
            }
            if (string.IsNullOrWhiteSpace(version.Stages[2].Configuration.ExpectedOutcome))
            {
                reasons.Add("The check stage has no expected outcome.");
            }
            if (automaticCheck &&
                (string.IsNullOrWhiteSpace(version.Stages[2].Configuration.ProviderId) ||
                 version.Stages[2].Configuration.OutcomeFormatVersion <= 0 ||
                 string.IsNullOrWhiteSpace(version.Stages[2].Configuration.ExpectedValue)))
            {
                reasons.Add("The automatic outcome check is incomplete.");
            }
        }

        static void ValidateOutcomeProvider(
            SavedTestVersion version,
            TestStateRegistry registry,
            ICollection<string> reasons)
        {
            if (version?.Stages.Count != 3 ||
                !string.Equals(
                    version.Stages[2].Adapter,
                    OutcomeObservationAdapter,
                    StringComparison.Ordinal))
                return;

            if (!registry.TryGetProvider(out var registered) ||
                !(registered.Provider is IQamelTestOutcomeProvider provider))
            {
                reasons.Add(
                    "This Test requires a game-owned outcome provider on the active " +
                    "state provider.");
                return;
            }

            var configuration = version.Stages[2].Configuration;
            if (!string.Equals(
                    registered.ProviderId,
                    configuration.ProviderId,
                    StringComparison.Ordinal))
            {
                reasons.Add("The active outcome provider belongs to '" +
                            registered.ProviderId + "', but this Test requires '" +
                            configuration.ProviderId + "'.");
                return;
            }

            int formatVersion;
            try
            {
                formatVersion = provider.OutcomeFormatVersion;
            }
            catch (Exception exception)
            {
                reasons.Add("Outcome provider metadata failed: " + exception.Message);
                return;
            }
            if (formatVersion != configuration.OutcomeFormatVersion)
            {
                reasons.Add("The outcome provider uses format v" + formatVersion +
                            ", but this Test requires v" +
                            configuration.OutcomeFormatVersion + ".");
            }
        }

        static void ValidateStage(
            SavedTestStage stage,
            int position,
            string role,
            string executor,
            string adapter,
            ICollection<string> reasons)
        {
            if (stage.Position == position &&
                string.Equals(stage.Role, role, StringComparison.Ordinal) &&
                string.Equals(stage.Executor, executor, StringComparison.Ordinal) &&
                string.Equals(stage.Adapter, adapter, StringComparison.Ordinal))
                return;
            reasons.Add("Stage " + position + " is not the supported " + role +
                        " stage executed by '" + executor + "' through '" + adapter + "'.");
        }

        internal static bool TryUnityReleaseFamily(string version, out string family)
        {
            family = null;
            if (string.IsNullOrWhiteSpace(version)) return false;
            string[] pieces = version.Trim().Split('.');
            if (pieces.Length < 2 || !AllDigits(pieces[0])) return false;
            var minor = new string(pieces[1].TakeWhile(char.IsDigit).ToArray());
            if (minor.Length == 0) return false;
            family = pieces[0] + "." + minor;
            return true;
        }

        static bool AllDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (var index = 0; index < value.Length; index++)
            {
                if (!char.IsDigit(value[index])) return false;
            }
            return true;
        }
    }

    internal sealed class SavedTestUnityEnvironment : ISavedTestReplayEnvironment
    {
        const string InputSystemTypeName =
            "UnityEngine.InputSystem.InputSystem, Unity.InputSystem";
        const string InputTraceTypeName =
            "UnityEngine.InputSystem.LowLevel.InputEventTrace, Unity.InputSystem";

        public string UnityRelease
        {
            get
            {
                return SavedTestReplayCompatibility.TryUnityReleaseFamily(
                    Application.unityVersion,
                    out var family)
                    ? family
                    : null;
            }
        }
        public string ActiveScenePath => SceneManager.GetActiveScene().path;
        public bool InputAvailable =>
            NewInputTraceAdapter.IsSupportedByCurrentConfiguration &&
            InputSystemType != null &&
            InputTraceType != null;
        public string InputUnavailableReason
        {
            get
            {
                if (!NewInputTraceAdapter.IsSupportedByCurrentConfiguration)
                    return NewInputTraceAdapter.CurrentConfigurationUnavailableReason;
                return InputSystemType != null && InputTraceType != null
                    ? null
                    : "Unity's Input System runtime types are unavailable.";
            }
        }
        public string InputSystemVersion => InputSystemType == null
            ? null
            : PackageVersionFor(InputSystemType.Assembly);

        static Type InputSystemType => Type.GetType(InputSystemTypeName, throwOnError: false);
        static Type InputTraceType => Type.GetType(InputTraceTypeName, throwOnError: false);

        public bool TryInspectAndMapInputTrace(
            byte[] bytes,
            out IReadOnlyList<string> actualDeviceLayouts,
            out string error)
        {
            actualDeviceLayouts = Array.Empty<string>();
            error = null;
            if (bytes == null || bytes.Length == 0)
            {
                error = "The downloaded input trace is empty.";
                return false;
            }
            if (!InputAvailable)
            {
                error = InputUnavailableReason;
                return false;
            }

            object trace = null;
            try
            {
                var load = InputTraceType.GetMethod(
                    "LoadFrom",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(Stream) },
                    modifiers: null);
                if (load == null)
                {
                    error = "This Input System cannot inspect saved traces before replay.";
                    return false;
                }
                RecordedInputFrames.Read(bytes, out var nativeTrace);
                using (var stream = new MemoryStream(nativeTrace, writable: false))
                    trace = load.Invoke(null, new object[] { stream });
                if (trace == null)
                {
                    error = "The downloaded input trace could not be loaded.";
                    return false;
                }

                var layouts = ReadTraceLayouts(trace);
                if (layouts.Count == 0)
                {
                    error = "The downloaded input trace contains no device metadata.";
                    return false;
                }
                if (!TryMapLayouts(layouts, out error)) return false;
                actualDeviceLayouts = layouts.AsReadOnly();
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = "The downloaded input trace could not be parsed: " +
                        (exception.InnerException?.Message ?? exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                error = "The downloaded input trace could not be parsed: " + exception.Message;
                return false;
            }
            finally
            {
                (trace as IDisposable)?.Dispose();
            }
        }

        static List<string> ReadTraceLayouts(object trace)
        {
            object infos = ReadMember(trace, "deviceInfos");
            var layouts = new List<string>();
            if (!(infos is IEnumerable enumerable)) return layouts;
            foreach (object info in enumerable)
            {
                string layout = ReadMember(info, "layout") as string;
                if (!string.IsNullOrWhiteSpace(layout)) layouts.Add(layout);
            }
            return layouts;
        }

        static bool TryMapLayouts(IReadOnlyList<string> layouts, out string error)
        {
            error = null;
            object devices = InputSystemType.GetProperty(
                "devices",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (!(devices is IEnumerable enumerable))
            {
                error = "The Input System did not expose active devices.";
                return false;
            }

            var available = new List<object>();
            foreach (object device in enumerable)
            {
                object added = ReadMember(device, "added");
                if (added is bool isAdded && isAdded) available.Add(device);
            }
            var used = new HashSet<object>();
            foreach (string recordedLayout in layouts)
            {
                object match = available.FirstOrDefault(device =>
                    !used.Contains(device) && LayoutMatches(
                        ReadMember(device, "layout") as string,
                        recordedLayout));
                if (match == null)
                {
                    error = "No active input device maps recorded layout '" +
                            recordedLayout + "'.";
                    return false;
                }
                used.Add(match);
            }
            return true;
        }

        static bool LayoutMatches(string playbackLayout, string recordedLayout)
        {
            if (string.Equals(playbackLayout, recordedLayout, StringComparison.Ordinal))
                return true;
            var basedOn = InputSystemType.GetMethod(
                "IsFirstLayoutBasedOnSecond",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(string) },
                modifiers: null);
            return basedOn != null &&
                   basedOn.Invoke(null, new object[] { playbackLayout, recordedLayout }) is bool result &&
                   result;
        }

        static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            Type type = target.GetType();
            var property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null) return property.GetValue(target);
            return type.GetField(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(target);
        }

        static string PackageVersionFor(Assembly assembly)
        {
            try
            {
                return PackageInfo.FindForAssembly(assembly)?.version;
            }
            catch
            {
                return assembly?.GetName().Version?.ToString();
            }
        }
    }
}
