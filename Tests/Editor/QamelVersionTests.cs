using NUnit.Framework;
using QamelCapture;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public class QamelVersionTests
    {
        [Test]
        public void ComparesNumericallyNotAlphabetically()
        {
            Assert.IsTrue(QamelVersion.IsNewer("0.1.10", "0.1.9"));
            Assert.IsFalse(QamelVersion.IsNewer("0.1.9", "0.1.10"));
            Assert.IsTrue(QamelVersion.IsNewer("0.2.0", "0.1.99"));
            Assert.IsTrue(QamelVersion.IsNewer("1.0.0", "0.99.99"));
        }

        [Test]
        public void EqualVersionsAreNeitherNewerNorOlder()
        {
            Assert.IsFalse(QamelVersion.IsNewer("0.1.2", "0.1.2"));
            Assert.IsFalse(QamelVersion.IsOlder("0.1.2", "0.1.2"));
        }

        [Test]
        public void AcceptsTagPrefixesAndShortVersions()
        {
            Assert.IsTrue(QamelVersion.IsNewer("v0.2.0", "0.1.2"));
            Assert.IsTrue(QamelVersion.IsNewer("0.2", "0.1.9"));
            Assert.IsTrue(QamelVersion.IsNewer("1", "0.9.9"));
            Assert.IsFalse(QamelVersion.IsNewer(" 0.1.2 ", "0.1.2"));
        }

        [Test]
        public void PreReleasesSortBeforeTheirRelease()
        {
            Assert.IsTrue(QamelVersion.IsNewer("0.2.0", "0.2.0-rc.1"));
            Assert.IsFalse(QamelVersion.IsNewer("0.2.0-rc.1", "0.2.0"));
            Assert.IsTrue(QamelVersion.IsNewer("0.2.0-rc.1", "0.1.9"));
        }

        [Test]
        public void BuildMetadataIsIgnored()
        {
            Assert.IsFalse(QamelVersion.IsNewer("0.1.2+abc", "0.1.2"));
            Assert.IsTrue(QamelVersion.IsNewer("0.1.3+abc", "0.1.2"));
        }

        /// <summary>
        /// A proxy error page or an empty response must never read as an update:
        /// unparseable input has no opinion at all.
        /// </summary>
        [Test]
        public void GarbageNeverCountsAsNewer()
        {
            foreach (string garbage in new[]
                     { null, "", "   ", "latest", "<html>", "1.2.3.4", "0.-1.0", "0.1.x", "+1.0.0" })
            {
                Assert.IsFalse(QamelVersion.IsNewer(garbage, "0.1.2"), garbage ?? "null");
                Assert.IsFalse(QamelVersion.IsOlder(garbage, "0.1.2"), garbage ?? "null");
                Assert.IsFalse(QamelVersion.TryCompare(garbage, "0.1.2", out _), garbage ?? "null");
            }
        }

        [Test]
        public void GitInstallPinnedToATagMovesToTheNewTag()
        {
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git", "v0.1.2", "0.2.0"));

            // A tag written without the prefix still resolves to our tag naming.
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git", "0.1.2", "v0.2.0"));
        }

        [Test]
        public void UnpinnedGitInstallIsPinnedToTheReleaseTag()
        {
            // Re-adding the bare URL would leave packages-lock.json pinned to the
            // commit it first resolved to, so an update has to name the tag.
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git", "", "0.2.0"));

            // The URL Unity reports may already carry the revision inline.
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git#v0.1.2", null, "0.2.0"));
        }

        [Test]
        public void OnlyAnUnpinnedInstallGetsPinnedByUpdating()
        {
            Assert.IsTrue(QamelUpdateTargets.PinsAPreviouslyUnpinnedInstall(""));
            Assert.IsTrue(QamelUpdateTargets.PinsAPreviouslyUnpinnedInstall(null));
            Assert.IsFalse(QamelUpdateTargets.PinsAPreviouslyUnpinnedInstall("v0.1.2"));
            Assert.IsFalse(QamelUpdateTargets.PinsAPreviouslyUnpinnedInstall("main"));
        }

        [Test]
        public void BranchInstallKeepsTrackingTheBranch()
        {
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#main",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git", "main", "0.2.0"));
        }

        [Test]
        public void CommitPinMovesToTheReleaseTag()
        {
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git", "3f7a91c", "0.2.0"));
        }

        [Test]
        public void SubfolderQueryStringSurvivesTheRewrite()
        {
            Assert.AreEqual(
                "https://github.com/qamel-ai/qamel-unity.git?path=/package#v0.2.0",
                QamelUpdateTargets.ForGitInstall(
                    "https://github.com/qamel-ai/qamel-unity.git?path=/package#v0.1.2",
                    "v0.1.2", "0.2.0"));
        }

        [Test]
        public void RegistryInstallAsksForTheExactVersion()
        {
            Assert.AreEqual("com.qamel.unity@0.2.0",
                QamelUpdateTargets.ForRegistryInstall("com.qamel.unity", "0.2.0"));
            Assert.AreEqual("com.qamel.unity@0.2.0",
                QamelUpdateTargets.ForRegistryInstall("com.qamel.unity", "v0.2.0"));
            Assert.AreEqual("com.qamel.unity",
                QamelUpdateTargets.ForRegistryInstall("com.qamel.unity", ""));
        }

        [Test]
        public void MissingInputYieldsNoTarget()
        {
            Assert.IsNull(QamelUpdateTargets.ForGitInstall(null, "v0.1.2", "0.2.0"));
            Assert.IsNull(QamelUpdateTargets.ForGitInstall(
                "https://github.com/qamel-ai/qamel-unity.git", "v0.1.2", ""));
            Assert.IsNull(QamelUpdateTargets.ForRegistryInstall(null, "0.2.0"));
        }

        [Test]
        public void RevisionOfReadsTheFragment()
        {
            Assert.AreEqual("v0.1.2",
                QamelUpdateTargets.RevisionOf("https://host/repo.git#v0.1.2"));
            Assert.AreEqual("", QamelUpdateTargets.RevisionOf("https://host/repo.git"));
            Assert.AreEqual("", QamelUpdateTargets.RevisionOf("https://host/repo.git#"));
            Assert.AreEqual("", QamelUpdateTargets.RevisionOf(null));
        }
    }
}
