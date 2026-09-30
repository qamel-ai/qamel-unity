using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class TestLabSavedTestReceipt
    {
        public TestLabSavedTestReceipt(
            string projectId,
            string projectName,
            string testId,
            string testVersionId,
            string testName,
            int versionNumber,
            string testUrl)
        {
            ProjectId = projectId;
            ProjectName = projectName;
            TestId = testId;
            TestVersionId = testVersionId;
            TestName = testName;
            VersionNumber = versionNumber;
            TestUrl = testUrl;
        }

        public string ProjectId { get; }
        public string ProjectName { get; }
        public string TestId { get; }
        public string TestVersionId { get; }
        public string TestName { get; }
        public int VersionNumber { get; }
        public string TestUrl { get; }
    }

    internal static class TestLabPreferences
    {
        public const string DefaultAuthoringEndpoint = "https://qamel.ai";
        internal const string DisabledMessage = "Experimental testing is off. Enable it in " +
            "Project Settings > Qamel > Experimental testing before connecting or opening Test Lab. " +
            "Human playtest capture is configured separately in Qamel settings.";

        static string ProjectKey(string field) =>
            "Qamel.TestLab." + field + "." + Hash128.Compute(Application.dataPath);

        internal static string AuthoringFingerprint => ComputeAuthoringFingerprint(
            AuthoringEndpoint,
            AuthoringApiKey);

        internal static string ComputeAuthoringFingerprint(
            string endpoint,
            string apiKey)
        {
            string value = TestDefinitionRoutes.Normalize(endpoint) + "\n" +
                           (apiKey ?? "").Trim();
            using (var sha256 = SHA256.Create())
            {
                byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool IsEnabled
        {
            get => EditorPrefs.GetBool(ProjectKey("Experimental"), false);
            set => EditorPrefs.SetBool(ProjectKey("Experimental"), value);
        }

        public static string AuthoringEndpoint
        {
            get => EditorPrefs.GetString(
                ProjectKey("AuthoringEndpoint"),
                DefaultAuthoringEndpoint);
            set => EditorPrefs.SetString(
                ProjectKey("AuthoringEndpoint"),
                (value ?? "").Trim());
        }

        public static string AuthoringApiKey
        {
            get => EditorPrefs.GetString(ProjectKey("AuthoringApiKey"), "");
            set => EditorPrefs.SetString(
                ProjectKey("AuthoringApiKey"),
                (value ?? "").Trim());
        }

        // Same machine-local, project-scoped Editor store as the existing key.
        // Never serialize pending credentials into a scene, asset or player build.
        internal static string PendingConnection
        {
            get => EditorPrefs.GetString(ProjectKey("PendingConnection"), "");
            set
            {
                if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(ProjectKey("PendingConnection"));
                else EditorPrefs.SetString(ProjectKey("PendingConnection"), value);
            }
        }

        internal static string RunnerInstallationId
        {
            get
            {
                string key = ProjectKey("RunnerInstallationId");
                string value = EditorPrefs.GetString(key, "");
                if (Guid.TryParse(value, out var parsed))
                    return parsed.ToString("D");

                value = Guid.NewGuid().ToString("D");
                EditorPrefs.SetString(key, value);
                return value;
            }
        }

        internal static void RememberCheckedConnection(
            string fingerprint,
            string projectId,
            string projectName)
        {
            SessionState.SetString(ProjectKey("CheckedFingerprint"), fingerprint ?? "");
            SessionState.SetString(ProjectKey("CheckedProjectId"), projectId ?? "");
            SessionState.SetString(ProjectKey("CheckedProjectName"), projectName ?? "");
        }

        internal static bool TryGetCheckedConnection(
            string fingerprint,
            out string projectId,
            out string projectName)
        {
            projectId = null;
            projectName = null;
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                !string.Equals(
                    SessionState.GetString(ProjectKey("CheckedFingerprint"), ""),
                    fingerprint,
                    StringComparison.Ordinal))
                return false;

            string savedProjectId = SessionState.GetString(
                ProjectKey("CheckedProjectId"), "");
            string savedProjectName = SessionState.GetString(
                ProjectKey("CheckedProjectName"), "");
            if (!Guid.TryParse(savedProjectId, out _) ||
                string.IsNullOrWhiteSpace(savedProjectName))
                return false;

            projectId = savedProjectId;
            projectName = savedProjectName;
            return true;
        }

        internal static void ClearCheckedConnection()
        {
            SessionState.SetString(ProjectKey("CheckedFingerprint"), "");
            SessionState.SetString(ProjectKey("CheckedProjectId"), "");
            SessionState.SetString(ProjectKey("CheckedProjectName"), "");
        }

        internal static void RememberLastSavedTest(
            string projectId,
            string projectName,
            TestDefinitionSaveOperation operation)
        {
            if (operation == null ||
                operation.State != TestDefinitionSaveState.Succeeded ||
                !Guid.TryParse(projectId, out _) ||
                string.IsNullOrWhiteSpace(projectName) ||
                !Guid.TryParse(operation.TestId, out _) ||
                !Guid.TryParse(operation.TestVersionId, out _) ||
                string.IsNullOrWhiteSpace(operation.TestName) ||
                operation.VersionNumber <= 0 ||
                !TestDefinitionRoutes.TryGetTestUrl(
                    AuthoringEndpoint,
                    operation.TestId,
                    out var testUrl))
                return;

            RememberLastSavedTest(new TestLabSavedTestReceipt(
                projectId,
                projectName,
                operation.TestId,
                operation.TestVersionId,
                operation.TestName,
                operation.VersionNumber,
                testUrl));
        }

        internal static void RememberLastSavedTest(TestLabSavedTestReceipt receipt)
        {
            if (receipt == null ||
                !Guid.TryParse(receipt.ProjectId, out _) ||
                string.IsNullOrWhiteSpace(receipt.ProjectName) ||
                !Guid.TryParse(receipt.TestId, out _) ||
                !Guid.TryParse(receipt.TestVersionId, out _) ||
                string.IsNullOrWhiteSpace(receipt.TestName) ||
                receipt.VersionNumber <= 0 ||
                !TestDefinitionRoutes.IsValidBase(AuthoringEndpoint))
                return;

            EditorPrefs.SetString(
                ProjectKey("LastSavedEndpoint"),
                TestDefinitionRoutes.Normalize(AuthoringEndpoint));
            EditorPrefs.SetString(
                ProjectKey("LastSavedProjectId"),
                receipt.ProjectId);
            EditorPrefs.SetString(
                ProjectKey("LastSavedProjectName"),
                receipt.ProjectName.Trim());
            EditorPrefs.SetString(ProjectKey("LastSavedTestId"), receipt.TestId);
            EditorPrefs.SetString(
                ProjectKey("LastSavedTestVersionId"),
                receipt.TestVersionId);
            EditorPrefs.SetString(
                ProjectKey("LastSavedTestName"),
                receipt.TestName.Trim());
            EditorPrefs.SetInt(
                ProjectKey("LastSavedVersionNumber"),
                receipt.VersionNumber);
        }

        internal static TestLabSavedTestReceipt LastSavedTestForProject(
            string connectedProjectId)
        {
            string projectId = EditorPrefs.GetString(
                ProjectKey("LastSavedProjectId"), "");
            string savedEndpoint = EditorPrefs.GetString(
                ProjectKey("LastSavedEndpoint"), "");
            if (!Guid.TryParse(connectedProjectId, out var connectedProjectGuid) ||
                !Guid.TryParse(projectId, out var savedProjectGuid) ||
                savedProjectGuid != connectedProjectGuid ||
                !string.Equals(
                    savedEndpoint,
                    TestDefinitionRoutes.Normalize(AuthoringEndpoint),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            string projectName = EditorPrefs.GetString(
                ProjectKey("LastSavedProjectName"), "");
            string testId = EditorPrefs.GetString(
                ProjectKey("LastSavedTestId"), "");
            string testVersionId = EditorPrefs.GetString(
                ProjectKey("LastSavedTestVersionId"), "");
            string testName = EditorPrefs.GetString(
                ProjectKey("LastSavedTestName"), "");
            int versionNumber = EditorPrefs.GetInt(
                ProjectKey("LastSavedVersionNumber"), 0);
            if (!Guid.TryParse(projectId, out _) ||
                string.IsNullOrWhiteSpace(projectName) ||
                !Guid.TryParse(testId, out _) ||
                !Guid.TryParse(testVersionId, out _) ||
                string.IsNullOrWhiteSpace(testName) ||
                versionNumber <= 0 ||
                !TestDefinitionRoutes.TryGetTestUrl(
                    AuthoringEndpoint,
                    testId,
                    out var testUrl))
                return null;

            return new TestLabSavedTestReceipt(
                projectId,
                projectName,
                testId,
                testVersionId,
                testName,
                versionNumber,
                testUrl);
        }

        internal static void ClearLastSavedTest()
        {
            EditorPrefs.DeleteKey(ProjectKey("LastSavedEndpoint"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedProjectId"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedProjectName"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedTestId"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedTestVersionId"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedTestName"));
            EditorPrefs.DeleteKey(ProjectKey("LastSavedVersionNumber"));
        }

        internal static void RememberSaveInterruption(string message)
        {
            SessionState.SetString(
                ProjectKey("InterruptedSaveMessage"),
                message ?? "");
        }

        internal static string ConsumeSaveInterruption()
        {
            string message = SessionState.GetString(
                ProjectKey("InterruptedSaveMessage"), "");
            SessionState.SetString(ProjectKey("InterruptedSaveMessage"), "");
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
    }
}
