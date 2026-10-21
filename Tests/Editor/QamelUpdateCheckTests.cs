using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public class QamelUpdateCheckTests
    {
        [TestCase(false, false, false, null, "0.1.11", "", "Not checked")]
        [TestCase(false, false, true, null, "0.1.11", "", "Not checked")]
        [TestCase(true, false, false, null, "0.1.11", "0.1.11", "Checking...")]
        [TestCase(false, true, true, null, "0.1.11", "0.1.12", "Updating...")]
        [TestCase(false, false, false, "Offline", "0.1.11", "0.1.11", "Check failed")]
        [TestCase(false, false, true, null, "0.1.11", "0.1.11", "Up to date")]
        [TestCase(false, false, true, null, "0.1.11", "0.1.12", "Update available")]
        [TestCase(false, false, true, null, "0.1.12", "0.1.11", "Development version")]
        public void SettingsStatusNeverClaimsAnUncheckedOrFailedVersionIsCurrent(bool checking,
            bool updating, bool verified, string error, string installed, string latest, string expected)
        {
            var now = DateTime.UtcNow;
            Assert.AreEqual(expected, QamelUpdateCheck.DescribeStatus(checking, updating, verified,
                error, installed, latest, now.AddMinutes(-1).Ticks.ToString(), now));
        }

        [Test]
        public void MatchingCachedVersionStopsClaimingItIsCurrentWhenTheCheckIsDue()
        {
            var now = new DateTime(2026, 10, 21, 4, 0, 0, DateTimeKind.Utc);
            foreach (string stored in new[] { "", "bad", "0", long.MaxValue.ToString(),
                         now.AddMinutes(1).Ticks.ToString(), now.AddHours(-1).Ticks.ToString() })
                Assert.AreEqual("Check overdue", QamelUpdateCheck.DescribeStatus(false, false, true,
                    null, "0.1.13", "0.1.13", stored, now), stored);
            Assert.AreEqual("Up to date", QamelUpdateCheck.DescribeStatus(false, false, true,
                null, "0.1.13", "0.1.13", now.AddHours(-1).AddTicks(1).Ticks.ToString(), now));
            Assert.AreEqual("Update available", QamelUpdateCheck.DescribeStatus(false, false, true,
                null, "0.1.13", "0.1.14", now.AddDays(-2).Ticks.ToString(), now));
            Assert.AreEqual("Check failed", QamelUpdateCheck.DescribeStatus(false, false, false,
                "Offline", "0.1.13", "0.1.13", now.AddMinutes(-1).Ticks.ToString(), now));
        }

        [Test]
        public void PersistedCheckTimeRetainsUtcAndRejectsInvalidValues()
        {
            var checkedAt = new DateTime(2026, 10, 21, 4, 0, 0, DateTimeKind.Utc);
            var restored = QamelUpdateCheck.ReadCheckTime(checkedAt.Ticks.ToString());
            Assert.AreEqual(checkedAt, restored);
            Assert.AreEqual(DateTimeKind.Utc, restored.Value.Kind);
            foreach (string stored in new[] { null, "", "bad", "-1", "0", long.MaxValue.ToString() })
                Assert.IsNull(QamelUpdateCheck.ReadCheckTime(stored), stored);
        }

        [Test]
        public void ChangesOpenThePublishedTagHistoryRatherThanAMissingGitHubRelease()
        {
            Assert.AreEqual("https://github.com/qamel-ai/qamel-unity/commits/v0.1.11",
                QamelUpdateCheck.ChangeHistoryUrl("0.1.11"));
            Assert.AreEqual("https://github.com/qamel-ai/qamel-unity/commits/main",
                QamelUpdateCheck.ChangeHistoryUrl("../other"));
        }

        [Test]
        public void ReadsTheQamelPayload()
        {
            string body =
                "{\"package\":\"com.qamel.unity\",\"latest\":\"0.2.0\"," +
                "\"minSupported\":\"0.1.0\"," +
                "\"gitUrl\":\"https://github.com/qamel-ai/qamel-unity.git\"," +
                "\"notesUrl\":\"https://github.com/qamel-ai/qamel-unity/releases/tag/v0.2.0\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.2.0", latest);
            Assert.AreEqual("0.1.0", minSupported);
            Assert.AreEqual("https://github.com/qamel-ai/qamel-unity/releases/tag/v0.2.0", notesUrl);
        }

        /// <summary>The fallback reads the package manifest, which says "version".</summary>
        [Test]
        public void ReadsThePackageManifestFallback()
        {
            string body = "{\"name\":\"com.qamel.unity\",\"version\":\"0.3.1\",\"unity\":\"2021.3\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.3.1", latest);
            Assert.AreEqual("", minSupported);
            Assert.AreEqual("", notesUrl);
        }

        /// <summary>
        /// A captive portal, a proxy error page or a truncated response must leave
        /// the stored version untouched rather than nag about nothing.
        /// </summary>
        [Test]
        public void RejectsAnythingWithoutAVersion()
        {
            foreach (string body in new[]
                     {
                         null,
                         "",
                         "   ",
                         "<html><body>Sign in to the network</body></html>",
                         "{}",
                         "{\"latest\":\"\"}",
                         "{\"latest\":\"unknown\"}",
                         "{\"error\":\"unknown package\"}",
                         "{\"latest\":\"0.2",
                     })
            {
                Assert.IsFalse(
                    QamelUpdateCheck.TryParsePayload(body, out _, out _, out _),
                    body ?? "null");
            }
        }

        [Test]
        public void IgnoresAnUnsupportedMinVersionAndNonHttpsNotes()
        {
            string body = "{\"latest\":\"0.2.0\",\"minSupported\":\"garbage\"," +
                          "\"notesUrl\":\"javascript:alert(1)\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.2.0", latest);
            Assert.AreEqual("", minSupported, "an unparseable minSupported must be dropped");
            Assert.AreEqual("", notesUrl, "only absolute https URLs may be opened");
        }

        [TestCase("0.1.9", "0.1.11")]
        [TestCase("9.0.0", "0.1.11")]
        public void PublishedManifestIsAuthoritativeEvenWhenTheServerDisagrees(string endpoint, string published)
        {
            Assert.IsTrue(QamelUpdateCheck.ChooseRelease(
                "{\"latest\":\"" + endpoint + "\",\"minSupported\":\"0.1.0\",\"notesUrl\":\"https://example.com/old\"}",
                "{\"name\":\"com.qamel.unity\",\"version\":\"" + published + "\"}",
                out string latest, out string minimum, out string notes, out bool verified));
            Assert.AreEqual(published, latest);
            Assert.AreEqual("0.1.0", minimum);
            Assert.AreEqual("https://github.com/qamel-ai/qamel-unity/commits/v" + published, notes);
            Assert.IsTrue(verified);
        }

        [Test]
        public void EndpointFailureDoesNotHideAPublishedRelease()
        {
            Assert.IsTrue(QamelUpdateCheck.ChooseRelease(null,
                "{\"name\":\"com.qamel.unity\",\"version\":\"0.1.11\"}",
                out string latest, out _, out _, out bool verified));
            Assert.AreEqual("0.1.11", latest);
            Assert.IsTrue(verified);
        }

        [Test]
        public void EndpointOnlyResultsAreNotEligibleForAutomaticInstallation()
        {
            Assert.IsTrue(QamelUpdateCheck.ChooseRelease("{\"latest\":\"0.2.0\"}", null,
                out string latest, out _, out _, out bool verified));
            Assert.AreEqual("0.2.0", latest);
            Assert.IsFalse(verified);
        }

        [TestCase("{\"name\":\"wrong.package\",\"version\":\"1.0.0\"}")]
        [TestCase("{\"latest\":\"1.0.0\"}")]
        [TestCase("{\"name\":\"com.qamel.unity\",\"version\":\"1.0.0-rc.1\"}")]
        public void InvalidPublishedManifestsAreNotVerified(string body)
        {
            Assert.IsFalse(QamelUpdateCheck.TryParsePublishedManifest(body, out _));
        }

        [Test]
        public void CorruptOrFutureCacheTimesCannotSuppressChecks()
        {
            var now = DateTime.UtcNow;
            foreach (string value in new[] { "", "bad", "-1", long.MaxValue.ToString(), now.AddDays(1).Ticks.ToString() })
                Assert.IsTrue(QamelUpdateCheck.IntervalElapsed(value, now), value);
            Assert.IsFalse(QamelUpdateCheck.IntervalElapsed(now.AddHours(-1).Ticks.ToString(), now));
            Assert.IsTrue(QamelUpdateCheck.IntervalElapsed(now.AddHours(-25).Ticks.ToString(), now));
        }

        [TestCase("", true)]
        [TestCase("v0.1.10", true)]
        [TestCase("main", false)]
        [TestCase("feature", false)]
        [TestCase("3f7a91c", false)]
        [TestCase("v0.2.0-rc.1", false)]
        public void AutomaticUpdatesRespectDeliberateDevelopmentRevisions(string revision, bool allowed)
        {
            Assert.AreEqual(allowed, QamelUpdateTargets.SupportsAutomaticGitUpdate(
                "https://github.com/qamel-ai/qamel-unity.git", revision));
        }

        [Test]
        public void AutomaticUpdatesNeverChangeAForkOrSubfolderInstall()
        {
            Assert.IsFalse(QamelUpdateTargets.SupportsAutomaticGitUpdate("https://example.com/qamel.git", ""));
            Assert.IsFalse(QamelUpdateTargets.SupportsAutomaticGitUpdate(
                "https://github.com/qamel-ai/qamel-unity.git?path=/fork", ""));
            Assert.IsFalse(QamelUpdateTargets.SupportsAutomaticGitUpdate(
                "https://github.com/qamel-ai/qamel-unity.git#main", null));
        }

        [Test]
        public void FailedChecksRetryEvenWhenAnEarlierSuccessIsStillFresh()
        {
            var now = DateTime.UtcNow;
            string freshSuccess = now.AddMinutes(-1).Ticks.ToString();
            Assert.IsFalse(QamelUpdateCheck.CheckIsDue(false, freshSuccess, now, 599, 600, true));
            Assert.IsTrue(QamelUpdateCheck.CheckIsDue(false, freshSuccess, now, 600, 600, true));
            Assert.IsFalse(QamelUpdateCheck.CheckIsDue(true, freshSuccess, now, 600, 600, true));
        }

        [Test]
        public void EachEditorSessionChecksAgainAndLongSessionsRefreshHourly()
        {
            var now = DateTime.UtcNow;
            string recentSuccess = now.AddMinutes(-1).Ticks.ToString();
            Assert.IsTrue(QamelUpdateCheck.CheckIsDue(true, recentSuccess, now, 10, 0, false));
            Assert.IsFalse(QamelUpdateCheck.CheckIsDue(true, recentSuccess, now, 10, 0, true));
            Assert.IsFalse(QamelUpdateCheck.CheckIsDue(false, recentSuccess, now, 599, 600, false));
            Assert.IsTrue(QamelUpdateCheck.CheckIsDue(true, now.AddHours(-1).Ticks.ToString(),
                now, 3600, 0, true));
        }

        [Test]
        public void FailedAutomaticInstallsRetryAfterTenMinutesWithoutBlockingANewerRelease()
        {
            var now = DateTime.UtcNow;
            Assert.IsFalse(QamelPackageUpdater.AutomaticRetryIsDue("0.1.14", "0.1.14",
                now.AddMinutes(-10).AddTicks(1).Ticks.ToString(), now));
            Assert.IsTrue(QamelPackageUpdater.AutomaticRetryIsDue("0.1.14", "0.1.14",
                now.AddMinutes(-10).Ticks.ToString(), now));
            Assert.IsTrue(QamelPackageUpdater.AutomaticRetryIsDue("0.1.15", "0.1.14",
                now.Ticks.ToString(), now));
            Assert.IsTrue(QamelPackageUpdater.AutomaticRetryIsDue("0.1.14", "0.1.14", "bad", now));
        }

        [UnityTest]
        public IEnumerator RealRequestsFindAnUpdateDespiteAStaleEndpointAndASkippedVersion()
        {
            string[] fields = { "Latest", "MinSupported", "NotesUrl", "Skipped", "LastSuccess" };
            string[] previous = new string[fields.Length];
            bool[] existed = new bool[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                string key = QamelUpdateCheck.ProjectKey(fields[i]);
                existed[i] = EditorPrefs.HasKey(key);
                previous[i] = EditorPrefs.GetString(key, "");
            }
            string verifiedKey = QamelUpdateCheck.ProjectKey("Verified");
            bool verifiedExisted = EditorPrefs.HasKey(verifiedKey);
            bool previousVerified = EditorPrefs.GetBool(verifiedKey, false);
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            string root = "http://127.0.0.1:" + port + "/";
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(root);
                listener.Start();
                Task server = Task.Run(async () =>
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var context = await listener.GetContextAsync();
                        string body = context.Request.RawUrl == "/endpoint"
                            ? "{\"latest\":\"0.1.9\",\"minSupported\":\"0.1.0\"}"
                            : "{\"name\":\"com.qamel.unity\",\"version\":\"9.0.1\"}";
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        context.Response.Close();
                    }
                });
                try
                {
                    EditorPrefs.SetString(QamelUpdateCheck.ProjectKey("Skipped"), "9.0.1");
                    QamelUpdateCheck.CheckSources(root + "endpoint", root + "manifest", userInitiated: true);
                    double deadline = EditorApplication.timeSinceStartup + 15;
                    while (QamelUpdateCheck.IsChecking && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.IsFalse(QamelUpdateCheck.IsChecking, "request timed out");
                    Assert.AreEqual("9.0.1", QamelUpdateCheck.LatestVersion);
                    Assert.IsTrue(QamelUpdateCheck.ReleaseVerified);
                    Assert.IsTrue(QamelUpdateCheck.UpdateAvailable);
                    Assert.IsTrue(QamelUpdateCheck.IsSkipped, "manual checks must preserve the automatic-install dismissal");
                    Assert.IsTrue(QamelUpdateCheck.ShowUpdate, "a manual check still reveals the skipped update");
                    Assert.IsNull(QamelUpdateCheck.LastError);
                    StringAssert.Contains("is available", QamelUpdateCheck.LastResult);
                    Assert.IsTrue(server.IsCompleted);
                    if (server.IsFaulted) throw server.Exception;
                }
                finally
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        string key = QamelUpdateCheck.ProjectKey(fields[i]);
                        if (existed[i]) EditorPrefs.SetString(key, previous[i]);
                        else EditorPrefs.DeleteKey(key);
                    }
                    if (verifiedExisted) EditorPrefs.SetBool(verifiedKey, previousVerified);
                    else EditorPrefs.DeleteKey(verifiedKey);
                }
            }
        }

        [UnityTest]
        public IEnumerator UnreachableSourcesShowAFailureWithoutRecordingASuccess()
        {
            string successKey = QamelUpdateCheck.ProjectKey("LastSuccess");
            string verifiedKey = QamelUpdateCheck.ProjectKey("Verified");
            bool hadSuccess = EditorPrefs.HasKey(successKey);
            string previousSuccess = EditorPrefs.GetString(successKey, "");
            bool hadVerified = EditorPrefs.HasKey(verifiedKey);
            bool previousVerified = EditorPrefs.GetBool(verifiedKey, false);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string root = "http://127.0.0.1:" + port + "/";
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(root);
                listener.Start();
                Task server = Task.Run(async () => {
                    for (int i = 0; i < 2; i++) {
                        var context = await listener.GetContextAsync();
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                    }
                });
                try
                {
                    EditorPrefs.SetString(successKey, "prior-success");
                    EditorPrefs.SetBool(verifiedKey, true);
                    QamelUpdateCheck.CheckSources(root + "endpoint", root + "manifest", userInitiated: false);
                    double deadline = EditorApplication.timeSinceStartup + 15;
                    while (QamelUpdateCheck.IsChecking && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.IsFalse(QamelUpdateCheck.IsChecking);
                    Assert.IsNotNull(QamelUpdateCheck.LastError);
                    Assert.IsNull(QamelUpdateCheck.LastResult, "failure must not say up to date");
                    Assert.IsFalse(QamelUpdateCheck.ReleaseVerified);
                    Assert.AreEqual("prior-success", EditorPrefs.GetString(successKey));
                    if (server.IsFaulted) throw server.Exception;
                }
                finally
                {
                    if (hadSuccess) EditorPrefs.SetString(successKey, previousSuccess);
                    else EditorPrefs.DeleteKey(successKey);
                    if (hadVerified) EditorPrefs.SetBool(verifiedKey, previousVerified);
                    else EditorPrefs.DeleteKey(verifiedKey);
                }
            }
        }
    }
}
