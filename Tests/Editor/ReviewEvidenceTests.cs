using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using QamelCapture.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace QamelCapture.Tests
{
    public class ReviewEvidenceTests
    {
        [Test]
        public void AudioSnapshotRetainsGapsAndWrapsWithAbsoluteTime()
        {
            var buffer = new AudioCaptureBuffer(100, 1, 1);
            buffer.Write(Enumerable.Repeat(0.5f, 20).ToArray(),1,1);
            buffer.Write(Enumerable.Repeat(-0.5f, 20).ToArray(),1,1.5);
            var audio = buffer.Snapshot(1,2);
            Assert.AreEqual(1, audio.StartT);
            Assert.AreEqual(70,audio.Samples.Length);
            Assert.AreEqual(0,audio.Samples[30]);
            Assert.Less(audio.Samples[60],0);
            buffer.Write(Enumerable.Repeat(0.8f,100).ToArray(),1,2);
            audio = buffer.Snapshot(0,3);
            Assert.AreEqual(2,audio.StartT);
            Assert.AreEqual(100,audio.Samples.Length);
        }

        [Test]
        public void AudioBundleIsOptionalAndContainsConsistentPcmHeader()
        {
            var buffer = new AudioCaptureBuffer(8000,2,1);
            buffer.Write(new float[1600],2,4);
            var audio = buffer.Snapshot(4,5);
            var bytes = ReportBundler.BuildBundle("{}",new List<string>(),new List<CapturedFrame>(),audio);
            using (var zip = new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read))
            {
                Assert.NotNull(zip.GetEntry("audio.json"));
                using (var reader = new BinaryReader(zip.GetEntry("audio.wav").Open()))
                {
                    reader.BaseStream.CopyTo(Stream.Null);
                }
                Assert.AreEqual(44+3200,zip.GetEntry("audio.wav").Length);
            }
            bytes = ReportBundler.BuildBundle("{}",new List<string>(),new List<CapturedFrame>());
            using (var zip = new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read)) Assert.IsNull(zip.GetEntry("audio.wav"));
        }

        [Test]
        public void BuildExclusionMovesAndRestoresWithoutChangingTheKey()
        {
            string path = "Assets/QamelExcludeTest-"+Guid.NewGuid().ToString("N")+".asset";
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.apiKey="test-only-not-a-live-key";
            Assert.IsFalse(settings.includeInPlayerBuild);
            try
            {
                AssetDatabase.CreateAsset(settings,path);
                QamelBuildInclusion.ExcludeSettings(new[]{settings});
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<QamelSettings>(path));
                QamelBuildInclusion.Restore();
                Assert.AreEqual("test-only-not-a-live-key",AssetDatabase.LoadAssetAtPath<QamelSettings>(path).apiKey);
            }
            finally { QamelBuildInclusion.Restore(); AssetDatabase.DeleteAsset(path); }
        }
    }
}
