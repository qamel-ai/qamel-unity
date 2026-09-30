using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class TestDefinitionSaveOperationTests
    {
        [TestCase("https://qamel.ai", true)]
        [TestCase("https://qamel.ai/", true)]
        [TestCase("http://localhost:3000", true)]
        [TestCase("http://127.0.0.1:3000", true)]
        [TestCase("http://[::1]:3000", true)]
        [TestCase("http://qamel.ai", false)]
        [TestCase("https://user:qamel@qamel.ai", false)]
        [TestCase("https://qamel.ai/path", false)]
        public void ValidatesAuthoringEndpoint(string endpoint, bool expected)
        {
            Assert.AreEqual(expected, TestDefinitionRoutes.IsValidBase(endpoint));
        }

        [TestCase("qa_key_abcdefghijklmnopqrstuvwxyzABCDEF", true)]
        [TestCase(" qa_key_abcdefghijklmnopqrstuvwxyzABCDEF ", true)]
        [TestCase("qa_mgt_abcdefghijklmnopqrstuvwxyzABCDEF", false)]
        [TestCase("qa_key_too-short", false)]
        [TestCase("qa_key_abcdefghijklmnopqrstuvwxyzABCDE-", false)]
        public void ValidatesManagementKeyShape(string apiKey, bool expected)
        {
            Assert.AreEqual(
                expected,
                TestDefinitionRoutes.IsValidProjectApiKey(apiKey));
        }

        [Test]
        public void BuildsTestUrlOnlyFromValidatedEndpointAndUuid()
        {
            const string testId = "018f0000-0000-7000-8000-000000000001";

            Assert.IsTrue(TestDefinitionRoutes.TryGetTestUrl(
                "https://qamel.ai/",
                testId,
                out var productionUrl));
            Assert.AreEqual(
                "https://qamel.ai/tests/018f0000-0000-7000-8000-000000000001",
                productionUrl);

            Assert.IsTrue(TestDefinitionRoutes.TryGetTestUrl(
                "http://localhost:3000",
                testId,
                out var localUrl));
            Assert.AreEqual(
                "http://localhost:3000/tests/018f0000-0000-7000-8000-000000000001",
                localUrl);

            Assert.IsFalse(TestDefinitionRoutes.TryGetTestUrl(
                "http://qamel.ai",
                testId,
                out _));
            Assert.IsFalse(TestDefinitionRoutes.TryGetTestUrl(
                "https://qamel.ai",
                "../reports",
                out _));
        }

        [TestCase("https://storage.example/upload?token=secret", true)]
        [TestCase("http://localhost:54321/upload?token=secret", true)]
        [TestCase("http://127.0.0.1:54321/upload", true)]
        [TestCase("http://storage.example/upload", false)]
        [TestCase("https://user:secret@storage.example/upload", false)]
        [TestCase("file:///tmp/state.bin", false)]
        public void ValidatesArtifactUploadUrl(string uploadUrl, bool expected)
        {
            Assert.AreEqual(
                expected,
                TestDefinitionRoutes.IsValidArtifactUploadUrl(uploadUrl));
        }

        [Test]
        public void ParsesRetryableRegistrationResponse()
        {
            const string json = "{" +
                "\"contractVersion\":3," +
                "\"created\":true," +
                "\"testId\":\"018f0000-0000-7000-8000-000000000001\"," +
                "\"testVersionId\":\"018f0000-0000-7000-8000-000000000002\"," +
                "\"versionNumber\":1," +
                "\"artifacts\":[" +
                "{\"clientRef\":\"state-payload\"," +
                "\"artifactId\":\"018f0000-0000-7000-8000-000000000003\"," +
                "\"objectPath\":\"state.bin\",\"uploadStatus\":\"uploaded\"}," +
                "{\"clientRef\":\"input-trace\"," +
                "\"artifactId\":\"018f0000-0000-7000-8000-000000000004\"," +
                "\"objectPath\":\"trace.bin\",\"uploadStatus\":\"pending\"," +
                "\"uploadUrl\":\"https://storage.example/upload\"}]}";

            bool ok = TestDefinitionSaveOperation.TryReadRegistration(
                json,
                out var response,
                out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(2, response.artifacts.Length);
            Assert.IsTrue(response.created);
            Assert.AreEqual(1, response.versionNumber);
            Assert.AreEqual("uploaded", response.artifacts[0].uploadStatus);
            Assert.AreEqual(
                "https://storage.example/upload",
                response.artifacts[1].uploadUrl);
        }

        [Test]
        public void RejectsPendingArtifactWithoutUploadUrl()
        {
            const string json = "{" +
                "\"contractVersion\":3," +
                "\"testId\":\"018f0000-0000-7000-8000-000000000001\"," +
                "\"testVersionId\":\"018f0000-0000-7000-8000-000000000002\"," +
                "\"versionNumber\":1," +
                "\"artifacts\":[" +
                "{\"clientRef\":\"state-payload\",\"artifactId\":\"state\"," +
                "\"objectPath\":\"state.bin\",\"uploadStatus\":\"pending\"}," +
                "{\"clientRef\":\"input-trace\",\"artifactId\":\"trace\"," +
                "\"objectPath\":\"trace.bin\",\"uploadStatus\":\"uploaded\"}]}";

            Assert.IsFalse(TestDefinitionSaveOperation.TryReadRegistration(
                json,
                out _,
                out var error));
            StringAssert.Contains("upload URL", error);
        }

        [Test]
        public void AcceptsSizeMismatchWhenRetryUrlIsPresent()
        {
            const string json = "{" +
                "\"contractVersion\":3," +
                "\"testId\":\"018f0000-0000-7000-8000-000000000001\"," +
                "\"testVersionId\":\"018f0000-0000-7000-8000-000000000002\"," +
                "\"versionNumber\":1," +
                "\"artifacts\":[" +
                "{\"clientRef\":\"state-payload\",\"artifactId\":\"state\"," +
                "\"objectPath\":\"state.bin\",\"uploadStatus\":\"size_mismatch\"," +
                "\"uploadUrl\":\"https://storage.example/state\"}," +
                "{\"clientRef\":\"input-trace\",\"artifactId\":\"trace\"," +
                "\"objectPath\":\"trace.bin\",\"uploadStatus\":\"uploaded\"}]}";

            Assert.IsTrue(TestDefinitionSaveOperation.TryReadRegistration(
                json,
                out var response,
                out var error), error);
            Assert.AreEqual("size_mismatch", response.artifacts[0].uploadStatus);
        }

        [Test]
        public void RejectsRegistrationResponseWithInvalidIds()
        {
            const string json = "{" +
                "\"contractVersion\":3," +
                "\"testId\":\"../reports\",\"testVersionId\":\"version\"," +
                "\"versionNumber\":1,\"artifacts\":[]}";

            Assert.IsFalse(TestDefinitionSaveOperation.TryReadRegistration(
                json,
                out _,
                out var error));
            StringAssert.Contains("incomplete", error);
        }

        [Test]
        public void UploadAndCompletionCopyNamesArtifactVersionAndTrustBoundary()
        {
            string state = TestDefinitionSaveOperation.BuildUploadStatus(
                RecordedTestDefinitionSerializer.StateArtifactRef,
                3,
                1,
                2);
            StringAssert.Contains("starting state", state);
            StringAssert.Contains("version 3", state);
            StringAssert.Contains("artifact 1 of 2", state);
            StringAssert.Contains("exact in-memory bytes", state);
            StringAssert.Contains("presence and byte size", state);

            string trace = TestDefinitionSaveOperation.BuildUploadStatus(
                RecordedTestDefinitionSerializer.TraceArtifactRef,
                3,
                2,
                2);
            StringAssert.Contains("input trace", trace);

            string created = TestDefinitionSaveOperation.BuildCompletionStatus(1, true, true);
            StringAssert.Contains("version 1", created);
            StringAssert.Contains("new Test", created);
            StringAssert.Contains("confirmed both artifacts uploaded", created);
            StringAssert.Contains("SHA-256", created);

            string existing = TestDefinitionSaveOperation.BuildCompletionStatus(1, false, false);
            StringAssert.Contains("existing Test", existing);
            StringAssert.Contains("already reports both artifacts uploaded", existing);
        }

        [Test]
        public void FinalReconciliationRequiresBothArtifactsToBeUploaded()
        {
            const string json = "{" +
                "\"contractVersion\":3,\"created\":false," +
                "\"testId\":\"018f0000-0000-7000-8000-000000000001\"," +
                "\"testVersionId\":\"018f0000-0000-7000-8000-000000000002\"," +
                "\"versionNumber\":1,\"artifacts\":[" +
                "{\"clientRef\":\"state-payload\",\"artifactId\":\"state\"," +
                "\"objectPath\":\"state.bin\",\"uploadStatus\":\"uploaded\"}," +
                "{\"clientRef\":\"input-trace\",\"artifactId\":\"trace\"," +
                "\"objectPath\":\"trace.bin\",\"uploadStatus\":\"pending\"," +
                "\"uploadUrl\":\"https://storage.example/trace\"}]}";

            Assert.IsTrue(TestDefinitionSaveOperation.TryReadRegistration(
                json,
                out var response,
                out var parseError), parseError);
            Assert.IsFalse(TestDefinitionSaveOperation.TryConfirmUploaded(
                response,
                out var confirmationError));
            StringAssert.Contains("input trace", confirmationError);
            StringAssert.Contains("artifact 2 of 2", confirmationError);
            StringAssert.Contains("pending", confirmationError);

            response.artifacts[1].uploadStatus = "uploaded";
            response.artifacts[1].uploadUrl = null;
            Assert.IsTrue(TestDefinitionSaveOperation.TryConfirmUploaded(
                response,
                out confirmationError), confirmationError);
        }

        [Test]
        public void UploadFailureRetainsArtifactNameAndPosition()
        {
            string failure = TestDefinitionSaveOperation.BuildUploadFailureStatus(
                RecordedTestDefinitionSerializer.TraceArtifactRef,
                2,
                2,
                "Storage rejected the upload.");

            StringAssert.Contains("input trace", failure);
            StringAssert.Contains("artifact 2 of 2", failure);
            StringAssert.Contains("Storage rejected the upload", failure);
        }
    }
}
