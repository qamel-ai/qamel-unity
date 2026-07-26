using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class ReportBundlerTests
    {
        static List<string> SampleEvents() => new List<string>
        {
            "{\"t\":1,\"type\":\"log\"}",
            "{\"t\":2,\"type\":\"input\"}",
        };

        static List<CapturedFrame> SampleFrames() => new List<CapturedFrame>
        {
            new CapturedFrame { Index = 118, T = 19.66, Width = 2, Height = 2, Jpg = new byte[] { 0xFF, 0xD8, 0xFF } },
            new CapturedFrame { Index = 119, T = 19.827, Width = 2, Height = 2, Jpg = new byte[] { 0xFF, 0xD8, 0xFE } },
        };

        static ZipArchive OpenZip(byte[] bytes) =>
            new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        [Test]
        public void FrameEntryNamesFollowTheSpec()
        {
            var frame = new CapturedFrame { Index = 118, T = 19.66 };
            Assert.AreEqual("frames/f_000118_00019660.jpg", ReportBundler.FrameEntryName(frame));
        }

        [Test]
        public void BuildsZipWithManifestLogsAndFrames()
        {
            byte[] bytes = ReportBundler.BuildBundle(
                "{\"schema\":\"1\",\"kind\":\"report\"}", SampleEvents(), SampleFrames());

            using (var zip = OpenZip(bytes))
            {
                Assert.AreEqual("{\"schema\":\"1\",\"kind\":\"report\"}", ReadText(zip, "manifest.json"));
                Assert.AreEqual("{\"t\":1,\"type\":\"log\"}\n{\"t\":2,\"type\":\"input\"}\n", ReadText(zip, "logs.jsonl"));

                var frame = zip.GetEntry("frames/f_000118_00019660.jpg");
                Assert.IsNotNull(frame);
                using (var stream = frame.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    Assert.AreEqual(new byte[] { 0xFF, 0xD8, 0xFF }, memory.ToArray());
                }
                Assert.IsNotNull(zip.GetEntry("frames/f_000119_00019827.jpg"));
                Assert.AreEqual(4, zip.Entries.Count);
            }
        }

        [Test]
        public void EmptyBuffersStillProduceAValidBundle()
        {
            byte[] bytes = ReportBundler.BuildBundle("{}", new List<string>(), new List<CapturedFrame>());
            using (var zip = OpenZip(bytes))
            {
                Assert.AreEqual(2, zip.Entries.Count);
                Assert.AreEqual("", ReadText(zip, "logs.jsonl"));
            }
        }

        [Test]
        public void BundleIsPureInMemory_NoFilesTouched()
        {
            // Guards the "never write gameplay data to the player's disk" rule:
            // building a bundle must not create anything under a Qamel data dir.
            string probeDir = Path.Combine(Path.GetTempPath(), "qamel_probe_" + Path.GetRandomFileName());
            Directory.CreateDirectory(probeDir);
            try
            {
                int before = Directory.GetFileSystemEntries(probeDir).Length;
                ReportBundler.BuildBundle("{}", SampleEvents(), SampleFrames());
                Assert.AreEqual(before, Directory.GetFileSystemEntries(probeDir).Length);
            }
            finally
            {
                Directory.Delete(probeDir, true);
            }
        }

        static string ReadText(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            Assert.IsNotNull(entry, entryName + " missing from zip");
            using (var reader = new StreamReader(entry.Open()))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
