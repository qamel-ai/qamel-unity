using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Locks ingest auth headers and paths to docs/capture-spec.md so a rename
    /// or Bearer format drift fails in CI before a plugin ships.
    /// </summary>
    public class IngestAuthContractTests
    {
        [Test]
        public void PathsMatchCaptureSpec()
        {
            Assert.AreEqual("/v1/report", IngestRoutes.ReportPath);
            Assert.AreEqual("/v1/chunk", IngestRoutes.ChunkPath);
            Assert.AreEqual("/v1/plugin-error", IngestRoutes.PluginErrorPath);
            Assert.AreEqual("/v1/plugin/latest", IngestRoutes.LatestVersionPath);
        }

        [Test]
        public void BearerHeaderTrimsAndPrefixes()
        {
            Assert.AreEqual("Bearer qa_ing_abc", IngestHeaders.Bearer("  qa_ing_abc  "));
            Assert.AreEqual("Bearer ", IngestHeaders.Bearer(null));
            Assert.AreEqual("Bearer ", IngestHeaders.Bearer(""));
        }

        [Test]
        public void PluginHeaderNamesTheUnityPackage()
        {
            Assert.AreEqual("Authorization", IngestHeaders.Authorization);
            Assert.AreEqual("X-Qamel-Plugin", IngestHeaders.Plugin);
            Assert.AreEqual("unity/" + QamelSettings.PluginVersion, IngestHeaders.PluginValue());
            StringAssert.StartsWith("unity/", IngestHeaders.PluginValue());
        }

        [Test]
        public void ReportAndPluginErrorUrlsJoinWithoutDoubleSlash()
        {
            Assert.AreEqual(
                "https://ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai/", IngestRoutes.ReportPath));
            Assert.AreEqual(
                "https://ingest.qamel.ai/v1/plugin-error",
                IngestRoutes.Url("https://ingest.qamel.ai", IngestRoutes.PluginErrorPath));
        }
    }
}
