using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class TestAuthoringHealthCheckTests
    {
        [Test]
        public void AutomaticRetryOnlyHandlesIdleAndTransientConnectionFailures()
        {
            Assert.IsTrue(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.Idle));
            Assert.IsTrue(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.Unreachable));
            Assert.IsFalse(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.Checking));
            Assert.IsFalse(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.Connected));
            Assert.IsFalse(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.InvalidConfiguration));
            Assert.IsFalse(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.Rejected));
            Assert.IsFalse(TestAuthoringHealthCheck.ShouldRetryAutomatically(
                TestAuthoringHealthState.InvalidResponse));
        }

        const string ProjectId = "018f0000-0000-7000-8000-000000000001";
        const string ManagementKey =
            "qa_key_abcdefghijklmnopqrstuvwxyzABCDEF";

        [Test]
        public void ParsesStableHealthEnvelopeWithLatestDefinitionVersion()
        {
            const string json = "{" +
                "\"contractVersion\":1," +
                "\"latestDefinitionContractVersion\":3," +
                "\"ok\":true," +
                "\"projectId\":\"" + ProjectId + "\"," +
                "\"projectName\":\"  FPS Sample  \"}";

            Assert.IsTrue(TestAuthoringHealthCheck.TryReadResponse(
                json,
                out var projectId,
                out var projectName,
                out var error), error);
            Assert.AreEqual(ProjectId, projectId);
            Assert.AreEqual("FPS Sample", projectName);
        }

        [TestCase("{}")]
        [TestCase("{\"contractVersion\":1,\"ok\":true," +
                  "\"projectId\":\"018f0000-0000-7000-8000-000000000001\"," +
                  "\"projectName\":\"FPS\"}")]
        [TestCase("{\"contractVersion\":2," +
                  "\"latestDefinitionContractVersion\":3,\"ok\":true," +
                  "\"projectId\":\"018f0000-0000-7000-8000-000000000001\"," +
                  "\"projectName\":\"FPS\"}")]
        [TestCase("{\"contractVersion\":1," +
                  "\"latestDefinitionContractVersion\":2,\"ok\":true," +
                  "\"projectId\":\"018f0000-0000-7000-8000-000000000001\"," +
                  "\"projectName\":\"FPS\"}")]
        [TestCase("{\"contractVersion\":1," +
                  "\"latestDefinitionContractVersion\":3,\"ok\":false," +
                  "\"projectId\":\"018f0000-0000-7000-8000-000000000001\"," +
                  "\"projectName\":\"FPS\"}")]
        [TestCase("{\"contractVersion\":1," +
                  "\"latestDefinitionContractVersion\":3,\"ok\":true," +
                  "\"projectId\":\"not-a-uuid\",\"projectName\":\"FPS\"}")]
        [TestCase("{\"contractVersion\":1," +
                  "\"latestDefinitionContractVersion\":3,\"ok\":true," +
                  "\"projectId\":\"018f0000-0000-7000-8000-000000000001\"," +
                  "\"projectName\":\"  \"}")]
        [TestCase("not json")]
        public void RejectsIncompleteOrMalformedResponse(string json)
        {
            Assert.IsFalse(TestAuthoringHealthCheck.TryReadResponse(
                json,
                out _,
                out _,
                out var error));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }

        [Test]
        public void AcceptsAHealthResponseFromANewerDefinitionServer()
        {
            const string json = "{" +
                "\"contractVersion\":1," +
                "\"latestDefinitionContractVersion\":4," +
                "\"ok\":true," +
                "\"projectId\":\"" + ProjectId + "\"," +
                "\"projectName\":\"FPS\"}";

            Assert.IsTrue(TestAuthoringHealthCheck.TryReadResponse(
                json,
                out var projectId,
                out _,
                out var error), error);
            Assert.AreEqual(ProjectId, projectId);
        }

        [Test]
        public void ConnectionPresentationRequiresValidCheckedIdentity()
        {
            var missing = TestAuthoringConnectionModel.Create(
                "https://qamel.ai",
                "",
                TestAuthoringHealthState.Idle,
                null,
                null,
                null);
            Assert.IsFalse(missing.CanCheck);
            Assert.IsFalse(missing.IsConnected);

            var uncheckedConnection = TestAuthoringConnectionModel.Create(
                "https://qamel.ai",
                ManagementKey,
                TestAuthoringHealthState.Idle,
                null,
                null,
                null);
            Assert.IsTrue(uncheckedConnection.CanCheck);
            Assert.IsFalse(uncheckedConnection.IsConnected);
            StringAssert.Contains("not been checked", uncheckedConnection.Status);

            var checking = TestAuthoringConnectionModel.Create(
                "https://qamel.ai",
                ManagementKey,
                TestAuthoringHealthState.Checking,
                null,
                null,
                null);
            Assert.IsFalse(checking.CanCheck);

            var connected = TestAuthoringConnectionModel.Create(
                "https://qamel.ai",
                ManagementKey,
                TestAuthoringHealthState.Connected,
                ProjectId,
                "FPS Sample",
                null);
            Assert.IsTrue(connected.IsConnected);
            Assert.AreEqual("FPS Sample", connected.ProjectName);
            StringAssert.Contains("FPS Sample", connected.Status);

            var rejected = TestAuthoringConnectionModel.Create(
                "https://qamel.ai",
                ManagementKey,
                TestAuthoringHealthState.Rejected,
                null,
                null,
                "Qamel rejected this authoring connection.");
            Assert.IsFalse(rejected.IsConnected);
            Assert.AreEqual(TestLabNoticeKind.Error, rejected.NoticeKind);
        }

        [Test]
        public void EvaluatesConnectedRejectedInvalidAndUnavailableResponses()
        {
            const string valid = "{" +
                "\"contractVersion\":1," +
                "\"latestDefinitionContractVersion\":3,\"ok\":true," +
                "\"projectId\":\"" + ProjectId + "\"," +
                "\"projectName\":\"FPS\"}";
            var connected = TestAuthoringHealthCheck.EvaluateResponse(
                true, 200, valid, null);
            Assert.AreEqual(TestAuthoringHealthState.Connected, connected.State);
            Assert.AreEqual("FPS", connected.ProjectName);

            var rejected = TestAuthoringHealthCheck.EvaluateResponse(
                false, 403, "{}", "Forbidden");
            Assert.AreEqual(TestAuthoringHealthState.Rejected, rejected.State);
            StringAssert.Contains("revoked", rejected.Error);

            var invalid = TestAuthoringHealthCheck.EvaluateResponse(
                true, 200, "{}", null);
            Assert.AreEqual(TestAuthoringHealthState.InvalidResponse, invalid.State);

            var unavailable = TestAuthoringHealthCheck.EvaluateResponse(
                false, 503, "{\"error\":\"try later\"}", "Service Unavailable");
            Assert.AreEqual(TestAuthoringHealthState.Unreachable, unavailable.State);
            StringAssert.Contains("try later", unavailable.Error);
        }
    }
}
