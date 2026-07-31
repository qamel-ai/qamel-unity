using NUnit.Framework;
using QamelCapture;
using UnityEngine;
using UnityEngine.TestTools;

namespace QamelCapture.Tests
{
    public class UploaderTests
    {
        sealed class Host : MonoBehaviour { }

        static QamelSettings MakeSettings(string endpoint, string apiKey, bool upload = true)
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.endpoint = endpoint;
            settings.apiKey = apiKey;
            settings.uploadReports = upload;
            return settings;
        }

        [Test]
        public void CanUploadRequiresEndpointKeyAndFlag()
        {
            var hostGo = new GameObject("QamelUploaderTestHost");
            var host = hostGo.AddComponent<Host>();
            try
            {
                var missingKey = MakeSettings("https://ingest.qamel.ai", "", true);
                Assert.IsFalse(new Uploader(missingKey, host).CanUpload);

                var missingEndpoint = MakeSettings("", "qa_ing_test", true);
                Assert.IsFalse(new Uploader(missingEndpoint, host).CanUpload);

                var uploadsOff = MakeSettings("https://ingest.qamel.ai", "qa_ing_test", false);
                Assert.IsFalse(new Uploader(uploadsOff, host).CanUpload);

                var ready = MakeSettings("https://ingest.qamel.ai", "qa_ing_test", true);
                Assert.IsTrue(new Uploader(ready, host).CanUpload);

                Object.DestroyImmediate(missingKey);
                Object.DestroyImmediate(missingEndpoint);
                Object.DestroyImmediate(uploadsOff);
                Object.DestroyImmediate(ready);
            }
            finally
            {
                Object.DestroyImmediate(hostGo);
            }
        }

        [Test]
        public void EnqueueWithoutConfigWarnsOnceAndDoesNotThrow()
        {
            var hostGo = new GameObject("QamelUploaderTestHost");
            var host = hostGo.AddComponent<Host>();
            var settings = MakeSettings("", "", true);
            try
            {
                var uploader = new Uploader(settings, host);
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "No API key / endpoint configured"));
                uploader.Enqueue("{}", new byte[] { 1, 2, 3 }, "report.zip", false);
                // Second enqueue must not warn again.
                uploader.Enqueue("{}", new byte[] { 4 }, "report2.zip", false);
            }
            finally
            {
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(hostGo);
            }
        }
    }
}
