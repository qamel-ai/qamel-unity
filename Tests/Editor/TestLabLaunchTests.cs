using System;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class TestLabLaunchTests
    {
        const string Project = "018f0000-0000-7000-8000-000000000001";
        const string Version = "018f0000-0000-7000-8000-000000000002";
        const string Link = "com.unity.editor://editor/qamelcapture/prepare/" + Project + "/" + Version;

        [Test]
        public void AcceptsOnlyIdentifiersFromTheUnityCallback()
        {
            Assert.IsTrue(TestLabLaunch.TryReadRequest(new Uri(Link), out var project, out var version));
            Assert.AreEqual(Project, project);
            Assert.AreEqual(Version, version);
        }

        [Test]
        public void AcceptsObservedHubForwardingAndRejectsTheBrokenDoubleEditorRoute()
        {
            const string hubLink = "unityhub://editor/qamelcapture/prepare/" + Project + "/" + Version;
            var forwarded = new Uri(hubLink.Replace("unityhub:", "com.unity.editor:"));
            Assert.IsTrue(TestLabLaunch.TryReadRequest(forwarded, out _, out _));
            var broken = new Uri("com.unity.editor://editor/editor/qamelcapture/prepare/" + Project + "/" + Version);
            Assert.IsFalse(TestLabLaunch.TryReadRequest(broken, out _, out _));
        }

        [TestCase(TestAuthoringHealthState.Idle, true)]
        [TestCase(TestAuthoringHealthState.Unreachable, true)]
        [TestCase(TestAuthoringHealthState.Rejected, false)]
        [TestCase(TestAuthoringHealthState.InvalidConfiguration, false)]
        [TestCase(TestAuthoringHealthState.InvalidResponse, false)]
        [TestCase(TestAuthoringHealthState.Checking, false)]
        [TestCase(TestAuthoringHealthState.Connected, false)]
        public void StartupRetriesTransientFailuresButNotRejectedCredentials(object state, bool expected)
        {
            Assert.AreEqual(expected, TestLabLaunch.ShouldRetryConnection((TestAuthoringHealthState)state));
        }

        [TestCase("?endpoint=https://example.com")]
        [TestCase("?key=secret")]
        [TestCase("#fragment")]
        [TestCase("/extra")]
        [TestCase("/")]
        public void RejectsAdditionalLinkInstructions(string suffix)
        {
            Assert.IsFalse(TestLabLaunch.TryReadRequest(new Uri(Link + suffix), out _, out _));
        }

        [TestCase("https://editor/qamelcapture/prepare/")]
        [TestCase("com.unity.editor://other/qamelcapture/prepare/")]
        [TestCase("com.unity.editor://user@editor/qamelcapture/prepare/")]
        [TestCase("com.unity.editor://editor:42/qamelcapture/prepare/")]
        [TestCase("com.unity.editor://editor/qamelcapture/run/")]
        [TestCase("com.unity.editor://editor/other/prepare/")]
        public void RejectsOtherRoutesAndOrigins(string prefix)
        {
            Assert.IsFalse(TestLabLaunch.TryReadRequest(new Uri(prefix + Project + "/" + Version), out _, out _));
        }

        [Test]
        public void RejectsMissingAndMalformedIdentifiers()
        {
            Assert.IsFalse(TestLabLaunch.TryReadRequest(null, out _, out _));
            Assert.IsFalse(TestLabLaunch.TryReadRequest(new Uri("relative", UriKind.Relative), out _, out _));
            Assert.IsFalse(TestLabLaunch.TryReadRequest(new Uri(Link.Replace(Version, "invalid")), out _, out _));
            Assert.IsFalse(TestLabLaunch.TryReadRequest(new Uri(Link.Replace(Project, Guid.Empty.ToString())), out _, out _));
        }

        [TestCase("Assets/FPS/Scenes/MainScene.unity", true)]
        [TestCase("Assets/My Game/Scene.unity", true)]
        [TestCase("/tmp/Scene.unity", false)]
        [TestCase("Assets/../Other/Scene.unity", false)]
        [TestCase("Assets//Scene.unity", false)]
        [TestCase("Assets/./Scene.unity", false)]
        [TestCase("Assets/Scene.cs", false)]
        [TestCase("Assets\\Scene.unity", false)]
        [TestCase(null, false)]
        public void OnlyOpensSavedProjectSceneAssets(string path, bool expected)
        {
            Assert.AreEqual(expected, TestLabLaunch.IsProjectScenePath(path));
        }

#if UNITY_6000_3_OR_NEWER
        [Test]
        public void HandlerIsDiscoverableByUnity()
        {
            var method = typeof(TestLabLaunch).GetMethod("Open");
            var attribute = (UnityEditor.DeeplinkHandlerAttribute)Attribute.GetCustomAttribute(
                method, typeof(UnityEditor.DeeplinkHandlerAttribute));
            Assert.AreEqual("qamelcapture", attribute.HandlerNamespace);
            StringAssert.StartsWith(attribute.HandlerNamespace,
                method.ReflectedType.AssemblyQualifiedName.ToLowerInvariant());
            Assert.AreEqual(typeof(Uri), method.GetParameters()[0].ParameterType);
        }
#endif
    }
}
