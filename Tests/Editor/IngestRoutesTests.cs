using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Locks the ingest URL contract: versioned paths, and the migration escape
    /// hatches that let an already-shipped build follow the server to a new host.
    /// </summary>
    public class IngestRoutesTests
    {
        [TearDown]
        public void ClearHandoff()
        {
            IngestRoutes.ResetSession();
        }

        [Test]
        public void PathsAreVersioned()
        {
            Assert.AreEqual("/v1/report", IngestRoutes.ReportPath);
            Assert.AreEqual("/v1/chunk", IngestRoutes.ChunkPath);
            Assert.AreEqual("/v1/plugin-error", IngestRoutes.PluginErrorPath);
        }

        [Test]
        public void UrlJoinsBaseAndPathWithoutDoubleSlash()
        {
            Assert.AreEqual("https://ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai", IngestRoutes.ReportPath));
            Assert.AreEqual("https://ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai/", IngestRoutes.ReportPath));
            Assert.AreEqual("https://ingest.qamel.ai/v1/chunk",
                IngestRoutes.Url("  https://ingest.qamel.ai//  ", IngestRoutes.ChunkPath));
        }

        [Test]
        public void IsValidBaseRequiresHttpsExceptOnLoopback()
        {
            Assert.IsTrue(IngestRoutes.IsValidBase("https://ingest.qamel.ai"));
            Assert.IsTrue(IngestRoutes.IsValidBase("https://eu.ingest.qamel.ai/edge"));
            Assert.IsTrue(IngestRoutes.IsValidBase("http://localhost:3000"));
            Assert.IsTrue(IngestRoutes.IsValidBase("http://127.0.0.1:3000"));

            Assert.IsFalse(IngestRoutes.IsValidBase("http://evil.example"));
            Assert.IsFalse(IngestRoutes.IsValidBase("ftp://ingest.qamel.ai"));
            Assert.IsFalse(IngestRoutes.IsValidBase("ingest.qamel.ai"));
            Assert.IsFalse(IngestRoutes.IsValidBase("https://"));
            Assert.IsFalse(IngestRoutes.IsValidBase("https://a b.example"));
            Assert.IsFalse(IngestRoutes.IsValidBase(""));
            Assert.IsFalse(IngestRoutes.IsValidBase(null));
        }

        [Test]
        public void AcceptedHandoffRedirectsSubsequentUrls()
        {
            Assert.IsTrue(IngestRoutes.TryAcceptHandoff("https://eu.ingest.qamel.ai/"));
            Assert.AreEqual("https://eu.ingest.qamel.ai", IngestRoutes.SessionBaseOverride);
            Assert.AreEqual("https://eu.ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai", IngestRoutes.ReportPath));

            // Repeating the same handoff is not a change.
            Assert.IsFalse(IngestRoutes.TryAcceptHandoff("https://eu.ingest.qamel.ai"));
        }

        [Test]
        public void InvalidOrMissingHandoffIsIgnored()
        {
            Assert.IsFalse(IngestRoutes.TryAcceptHandoff("http://evil.example"));
            Assert.IsFalse(IngestRoutes.TryAcceptHandoff(null));
            Assert.IsFalse(IngestRoutes.TryAcceptHandoff(""));
            Assert.IsNull(IngestRoutes.SessionBaseOverride);
            Assert.AreEqual("https://ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai", IngestRoutes.ReportPath));
        }

        [Test]
        public void ResetSessionRestoresConfiguredEndpoint()
        {
            IngestRoutes.TryAcceptHandoff("https://eu.ingest.qamel.ai");
            IngestRoutes.ResetSession();
            Assert.IsNull(IngestRoutes.SessionBaseOverride);
            Assert.AreEqual("https://ingest.qamel.ai/v1/report",
                IngestRoutes.Url("https://ingest.qamel.ai", IngestRoutes.ReportPath));
        }
    }
}
