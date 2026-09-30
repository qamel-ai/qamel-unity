using System;

namespace QamelCapture.Editor.TestAuthoring
{
    internal static class TestDefinitionRoutes
    {
        public const string RegistrationPath = "/api/v1/test-versions";
        public const string TestsPath = "/api/v1/tests";
        public const string RunnerLeasePath = "/api/v1/runners/lease";
        public const string TestCreationPollPath = "/api/v1/test-creation/poll";
        public const string TestCreationAdvancePath = "/api/v1/test-creation/advance";
        const string ProjectKeyPrefix = "qa_key_";
        const int ProjectKeySecretLength = 32;

        public static string Normalize(string baseUrl)
        {
            return string.IsNullOrWhiteSpace(baseUrl)
                ? ""
                : baseUrl.Trim().TrimEnd('/');
        }

        public static bool IsValidBase(string baseUrl)
        {
            string normalized = Normalize(baseUrl);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
                return false;
            if (!string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
                return false;

            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return true;

            return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                   uri.IsLoopback;
        }

        public static string RegistrationUrl(string baseUrl)
        {
            return Normalize(baseUrl) + RegistrationPath;
        }

        public static string TestsUrl(string baseUrl)
        {
            return Normalize(baseUrl) + TestsPath;
        }

        public static string RunnerLeaseUrl(string baseUrl)
        {
            return Normalize(baseUrl) + RunnerLeasePath;
        }

        public static string TestCreationPollUrl(string baseUrl)
        {
            return Normalize(baseUrl) + TestCreationPollPath;
        }

        public static string TestCreationAdvanceUrl(string baseUrl)
        {
            return Normalize(baseUrl) + TestCreationAdvancePath;
        }

        public static bool TryGetTestRunResultUrl(
            string baseUrl,
            string testRunId,
            out string resultUrl)
        {
            resultUrl = null;
            if (!IsValidBase(baseUrl) || !Guid.TryParse(testRunId, out var parsedRunId))
                return false;

            resultUrl = Normalize(baseUrl) + "/api/v1/test-runs/" +
                        parsedRunId.ToString("D") + "/result";
            return true;
        }

        public static bool TryGetTestRunEvidenceUrl(
            string baseUrl,
            string testRunId,
            out string evidenceUrl)
        {
            evidenceUrl = null;
            if (!IsValidBase(baseUrl) || !Guid.TryParse(testRunId, out var parsedRunId))
                return false;

            evidenceUrl = Normalize(baseUrl) + "/api/v1/test-runs/" +
                          parsedRunId.ToString("D") + "/evidence";
            return true;
        }

        public static bool TryGetTestVersionEvidenceUrl(
            string baseUrl,
            string testVersionId,
            out string evidenceUrl)
        {
            evidenceUrl = null;
            if (!IsValidBase(baseUrl) ||
                !Guid.TryParse(testVersionId, out var parsedVersionId))
                return false;

            evidenceUrl = Normalize(baseUrl) + "/api/v1/test-versions/" +
                          parsedVersionId.ToString("D") + "/evidence";
            return true;
        }

        public static bool TryGetTestRunHeartbeatUrl(
            string baseUrl,
            string testRunId,
            out string heartbeatUrl)
        {
            heartbeatUrl = null;
            if (!IsValidBase(baseUrl) || !Guid.TryParse(testRunId, out var parsedRunId))
                return false;

            heartbeatUrl = Normalize(baseUrl) + "/api/v1/test-runs/" +
                           parsedRunId.ToString("D") + "/heartbeat";
            return true;
        }

        public static bool TryGetVersionDownloadUrl(
            string baseUrl,
            string testVersionId,
            out string downloadUrl)
        {
            downloadUrl = null;
            if (!IsValidBase(baseUrl) ||
                !Guid.TryParse(testVersionId, out var parsedVersionId))
                return false;

            downloadUrl = Normalize(baseUrl) + "/api/v1/test-versions/" +
                          parsedVersionId.ToString("D") + "/download";
            return true;
        }

        public static bool IsValidProjectApiKey(string apiKey)
        {
            string value = (apiKey ?? "").Trim();
            if (!value.StartsWith(ProjectKeyPrefix, StringComparison.Ordinal) ||
                value.Length != ProjectKeyPrefix.Length + ProjectKeySecretLength)
                return false;

            for (var index = ProjectKeyPrefix.Length; index < value.Length; index++)
            {
                char character = value[index];
                bool alphaNumeric = character >= 'a' && character <= 'z' ||
                                    character >= 'A' && character <= 'Z' ||
                                    character >= '0' && character <= '9';
                if (!alphaNumeric) return false;
            }
            return true;
        }

        public static bool TryGetTestUrl(
            string baseUrl,
            string testId,
            out string testUrl)
        {
            testUrl = null;
            if (!IsValidBase(baseUrl) || !Guid.TryParse(testId, out var parsedTestId))
                return false;

            testUrl = Normalize(baseUrl) + "/tests/" + parsedTestId.ToString("D");
            return true;
        }

        public static bool IsValidArtifactUploadUrl(string uploadUrl)
        {
            return IsValidSignedArtifactUrl(uploadUrl);
        }

        public static bool IsValidArtifactDownloadUrl(string downloadUrl)
        {
            return IsValidSignedArtifactUrl(downloadUrl);
        }

        static bool IsValidSignedArtifactUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment))
                return false;

            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return true;

            return uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                   uri.IsLoopback;
        }
    }
}
