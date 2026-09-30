using System;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using UnityEditor;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class ExperimentalTestingOptInTests
    {
        const string Project = "018f0000-0000-7000-8000-000000000001";
        const string Version = "018f0000-0000-7000-8000-000000000002";
        const string Prefix = "com.unity.editor://editor/qamelcapture/";
        bool _wasEnabled;
        string _pendingConnection;
        string _endpoint;
        string _apiKey;
        string _launchStatus;

        [SetUp]
        public void DisableExperiments()
        {
            _wasEnabled = TestLabPreferences.IsEnabled;
            _pendingConnection = TestLabPreferences.PendingConnection;
            _endpoint = TestLabPreferences.AuthoringEndpoint;
            _apiKey = TestLabPreferences.AuthoringApiKey;
            _launchStatus = TestLabLaunch.Status;
            TestLabPreferences.IsEnabled = false;
        }

        [TearDown]
        public void RestorePreferences()
        {
            TestLabPreferences.IsEnabled = _wasEnabled;
            TestLabPreferences.PendingConnection = _pendingConnection;
            SessionState.SetString("Qamel.TestLab.LaunchStatus", _launchStatus);
        }

        [TestCase("connect/" + Project + "/production")]
        [TestCase("open/" + Project)]
        [TestCase("prepare/" + Project + "/" + Version)]
        public void BrowserLinksCannotEnableExperimentsOrChangeCredentials(string route)
        {
            TestLabLaunch.Open(new Uri(Prefix + route));

            Assert.IsFalse(TestLabPreferences.IsEnabled);
            Assert.AreEqual(_pendingConnection, TestLabPreferences.PendingConnection);
            Assert.AreEqual(_endpoint, TestLabPreferences.AuthoringEndpoint);
            Assert.AreEqual(_apiKey, TestLabPreferences.AuthoringApiKey);
            StringAssert.Contains("Project Settings > Qamel", TestLabLaunch.Status);
        }

        [Test]
        public void ConnectionAndPresenceHandlersAlsoRequireLocalOptIn()
        {
            Assert.IsTrue(ClientConnectionSession.HandleLink(new Uri(Prefix + "connect/" + Project + "/production")));
            Assert.IsTrue(ClientPresenceSession.HandleOpenLink(new Uri(Prefix + "open/" + Project)));
            Assert.IsFalse(TestLabPreferences.IsEnabled);
            Assert.AreEqual(_pendingConnection, TestLabPreferences.PendingConnection);
            Assert.IsFalse(TestAuthoringHealthCheck.CanCheck);
            Assert.IsFalse(TestAuthoringHealthCheck.IsConnected);
        }

        [Test]
        public void DisabledWindowCannotBeOpenedProgrammatically()
        {
            int openWindows = Resources.FindObjectsOfTypeAll<TestLabWindow>().Length;
            TestLabWindow.Open();
            Assert.AreEqual(openWindows, Resources.FindObjectsOfTypeAll<TestLabWindow>().Length);
            Assert.IsFalse(TestLabPreferences.IsEnabled);
        }
    }
}
