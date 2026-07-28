using NUnit.Framework;
using QamelCapture;
using UnityEngine;

namespace QamelCapture.Tests
{
    public class ParticipantIdentityTests
    {
        sealed class MemoryStore : IInstallationIdStore
        {
            public string Value;
            public string Load() => Value;
            public void Save(string value) => Value = value;
        }

        [Test]
        public void InstallationIdIsGeneratedAndReused()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            var store = new MemoryStore();

            var first = new ParticipantIdentity(settings, store);
            var second = new ParticipantIdentity(settings, store);

            Assert.AreEqual(32, first.InstallationId.Length);
            Assert.AreEqual(first.InstallationId, second.InstallationId);
            Object.DestroyImmediate(settings);
        }

        [Test]
        public void ExternalPlayerOverridesAndClearsWithoutChangingInstallation()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            var identity = new ParticipantIdentity(settings, new MemoryStore());
            string installationId = identity.InstallationId;

            Assert.IsTrue(identity.TrySetPlayer("studio.player-42", out string error), error);
            Assert.AreEqual("studio.player-42", identity.Snapshot().ExternalPlayerId);

            identity.ClearPlayer();
            Assert.AreEqual("", identity.Snapshot().ExternalPlayerId);
            Assert.AreEqual(installationId, identity.Snapshot().InstallationId);
            Object.DestroyImmediate(settings);
        }

        [TestCase("")]
        [TestCase("email@example.com")]
        [TestCase("contains spaces")]
        [TestCase("contains\ncontrol")]
        public void InvalidExternalPlayerIdsAreRejected(string value)
        {
            Assert.IsFalse(ParticipantIdentity.IsValidExternalPlayerId(value));
        }
    }
}
