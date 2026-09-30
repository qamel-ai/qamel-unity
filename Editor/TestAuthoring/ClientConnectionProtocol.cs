using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    [Serializable]
    internal sealed class PendingClientConnection
    {
        public string projectId, endpoint, verifier, state, apiKey, fingerprint, code, label;
        public long expiresAt;
        public bool browserOpened;
    }

    internal static class ClientConnectionProtocol
    {
        internal const string LocalOrigin = "http://localhost:3000";
        static bool Token(string value) => value != null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}$");

        internal static bool TryReadLink(Uri uri, out string projectId, out string endpoint,
            out string state, out string code)
        {
            projectId = endpoint = state = code = null;
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != "com.unity.editor" ||
                uri.Host != "editor" || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
                return false;
            var parts = uri.AbsolutePath.Split('/');
            if (parts.Length < 5 || parts[1] != "qamelcapture" ||
                !Guid.TryParseExact(parts[3], "D", out var project) || project == Guid.Empty) return false;
            if (parts.Length == 5 && parts[2] == "connect")
            {
                endpoint = parts[4] == "production" ? TestLabPreferences.DefaultAuthoringEndpoint
                    : parts[4] == "local" ? LocalOrigin : null;
                if (endpoint == null) return false;
            }
            else if (parts.Length == 6 && parts[2] == "connected" && Token(parts[4]) && Token(parts[5]))
            { state = parts[4]; code = parts[5]; }
            else return false;
            projectId = project.ToString("D");
            return true;
        }

        internal static string Challenge(string verifier)
        {
            using (var hash = SHA256.Create()) return Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        internal static string Hash(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static string RandomToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Base64Url(bytes);
        }

        internal static string CreateKey()
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var key = new StringBuilder("qa_key_");
            var bytes = new byte[64];
            using (var random = RandomNumberGenerator.Create())
                while (key.Length < 39)
                {
                    random.GetBytes(bytes);
                    foreach (byte value in bytes)
                    {
                        if (value < 248) key.Append(alphabet[value % alphabet.Length]);
                        if (key.Length == 39) break;
                    }
                }
            return key.ToString();
        }

        internal static bool IsValid(PendingClientConnection pending, long now, string fingerprint)
        {
            return pending != null && pending.expiresAt > now && pending.expiresAt <= now + 600 &&
                pending.fingerprint == fingerprint && Guid.TryParseExact(pending.projectId, "D", out var id) && id != Guid.Empty &&
                (pending.endpoint == LocalOrigin || pending.endpoint == TestLabPreferences.DefaultAuthoringEndpoint) &&
                Token(pending.verifier) && Token(pending.state) &&
                TestDefinitionRoutes.IsValidProjectApiKey(pending.apiKey) &&
                (string.IsNullOrEmpty(pending.code) || Token(pending.code));
        }

        internal static bool MatchesCallback(PendingClientConnection pending, string projectId, string state, string code,
            long now, string fingerprint) => IsValid(pending, now, fingerprint) && pending.projectId == projectId &&
            pending.state == state && Token(code) && (string.IsNullOrEmpty(pending.code) || pending.code == code);

        internal static string ApprovalUrl(PendingClientConnection pending) => pending.endpoint + "/connect/unity/" +
            pending.projectId + "?challenge=" + Challenge(pending.verifier) + "&state=" + pending.state +
            "&label=" + Uri.EscapeDataString(pending.label ?? "Unity Editor");

        [Serializable] sealed class ExchangeBody { public string code, verifier, keyHash, keyPrefix; }
        internal static byte[] ExchangeBytes(PendingClientConnection pending) => Encoding.UTF8.GetBytes(JsonUtility.ToJson(
            new ExchangeBody { code = pending.code, verifier = pending.verifier, keyHash = Hash(pending.apiKey), keyPrefix = pending.apiKey.Substring(0, 13) }));

        [Serializable] sealed class ExchangeResult { public string projectId, apiKeyId; }
        internal static bool AcceptsResponse(string json, string expectedProject)
        {
            try
            {
                var result = JsonUtility.FromJson<ExchangeResult>(json);
                return result != null && result.projectId == expectedProject && Guid.TryParseExact(result.apiKeyId, "D", out var id) && id != Guid.Empty;
            }
            catch (Exception) { return false; }
        }
    }
}
