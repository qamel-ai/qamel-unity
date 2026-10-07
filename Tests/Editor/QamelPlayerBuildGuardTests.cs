using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using QamelCapture.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace QamelCapture.Tests
{
    public class QamelPlayerBuildGuardTests
    {
        [TestCase(200, "{\"ok\":true,\"captureOnly\":true}", true)]
        [TestCase(200, "{\"ok\":true}", false)]
        [TestCase(200, "{\"ok\":true,\"captureOnly\":false}", false)]
        [TestCase(200, "{\"ok\":false,\"captureOnly\":true}", false)]
        [TestCase(200, "not json", false)]
        [TestCase(200, "", false)]
        [TestCase(401, "{\"ok\":true,\"captureOnly\":true}", false)]
        [TestCase(403, "{\"ok\":true,\"captureOnly\":true}", false)]
        [TestCase(500, "{\"ok\":true,\"captureOnly\":true}", false)]
        [TestCase(302, "{\"ok\":true,\"captureOnly\":true}", false)]
        public void RequiresExplicitServerApproval(long status, string json, bool approved)
        {
            Assert.AreEqual(approved, QamelPlayerBuildGuard.IsCaptureOnlyResponse(status, json));
        }

        [Test]
        public void DisabledSettingsAndDuplicateAssetsStillRequireVerification()
        {
            var first = ScriptableObject.CreateInstance<QamelSettings>();
            var second = ScriptableObject.CreateInstance<QamelSettings>();
            try
            {
                first.apiKey = "capture-test-key";
                second.apiKey = "privileged-test-key";
                second.captureEnabled = false;
                second.uploadReports = false;
                int checks = 0;
                Assert.Throws<BuildFailedException>(() => QamelPlayerBuildGuard.ValidateSettings(
                    new[] { first, second }, (endpoint, key) => { checks++; return key == first.apiKey; }));
                Assert.AreEqual(2, checks);
            }
            finally { UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second); }
        }

        [Test]
        public void BuildDiscoveryFindsDisabledSettingsAssets()
        {
            string path = "Assets/QamelBuildGuardTest-" + Guid.NewGuid().ToString("N") + ".asset";
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.apiKey = "test-only-privileged-key";
            settings.endpoint = "http://insecure.example";
            settings.captureEnabled = false;
            settings.uploadReports = false;
            try
            {
                AssetDatabase.CreateAsset(settings, path);
                var discovered = QamelPlayerBuildGuard.FindSettings();
                CollectionAssert.Contains(discovered, settings);
                Assert.Throws<BuildFailedException>(() =>
                    QamelPlayerBuildGuard.ValidateSettings(discovered, (_, __) => false));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void MissingKeysDoNotContactServer()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            try
            {
                settings.apiKey = " ";
                QamelPlayerBuildGuard.ValidateSettings(new[] { null, settings },
                    (_, __) => throw new Exception("must not contact server"));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void EveryBuildChecksAgainAndTransportErrorsAreRedacted()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            try
            {
                settings.apiKey = "test-secret-never-log";
                int checks = 0;
                QamelPlayerBuildGuard.ValidateSettings(new[] { settings }, (_, __) => { checks++; return true; });
                var error = Assert.Throws<BuildFailedException>(() =>
                    QamelPlayerBuildGuard.ValidateSettings(new[] { settings }, (_, key) => {
                        checks++; throw new Exception(key);
                    }));
                Assert.AreEqual(2, checks);
                StringAssert.DoesNotContain(settings.apiKey, error.ToString());
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase("https://ingest.qamel.ai", true)]
        [TestCase("http://localhost:3000", true)]
        [TestCase("http://127.0.0.1:3000", true)]
        [TestCase("http://[::1]:3000", true)]
        [TestCase("http://ingest.qamel.ai", false)]
        [TestCase("https://user:password@example.com", false)]
        [TestCase("https://example.com?redirect=elsewhere", false)]
        [TestCase("invalid", false)]
        public void RequiresSecureTransportExceptLoopback(string endpoint, bool allowed)
        {
            Assert.AreEqual(allowed, QamelPlayerBuildGuard.IsSecureEndpoint(endpoint));
        }

        [TestCase(200, "{\"ok\":true,\"captureOnly\":true}", true)]
        [TestCase(200, "{\"ok\":true}", false)]
        [TestCase(403, "{}", false)]
        [TestCase(302, "{}", false)]
        public void ActualHttpCheckUsesConfiguredEndpointAndExplicitApproval(int status, string json, bool allowed)
        {
            // Bind before choosing a port so parallel test runs cannot claim it.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(() => {
                    using (var connection = listener.AcceptTcpClient())
                    using (var stream = connection.GetStream())
                    {
                        var data = new byte[8192];
                        int count = stream.Read(data, 0, data.Length);
                        string headers = Encoding.ASCII.GetString(data, 0, count);
                        StringAssert.Contains("POST /v1/plugin/health HTTP/1.1", headers);
                        StringAssert.Contains("Authorization: Bearer test-key", headers);
                        string response = "HTTP/1.1 " + status + " Test\r\nContent-Type: application/json\r\n" +
                            "Content-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\n" +
                            "Location: http://127.0.0.1:1/must-not-follow\r\nConnection: close\r\n\r\n" + json;
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                });
                Assert.AreEqual(allowed, QamelPlayerBuildGuard.VerifyKey("http://127.0.0.1:" + port, "test-key"));
                Assert.IsTrue(server.Wait(TimeSpan.FromSeconds(5)));
            }
            finally { listener.Stop(); }
        }
    }
}
