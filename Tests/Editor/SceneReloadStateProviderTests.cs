using System;
using System.Text;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class SceneReloadStateProviderTests
    {
        sealed class Environment : ISceneReloadEnvironment
        {
            public string ScenePath { get; set; } = "Assets/Scenes/Game.unity";
            public int Frame { get; set; }
            public string BlockedReason { get; set; }
            public bool IsLoading { get; set; }
            public int Reloads;
            public bool ThrowOnLoad;
            public void Reload(string path)
            {
                Reloads++;
                if (ThrowOnLoad) throw new InvalidOperationException();
                IsLoading = true;
            }
        }

        [Test]
        public void SceneRecipeCapturesAndRestoresWithoutAGameOwnedProviderOrOutcome()
        {
            var environment = new Environment();
            var provider = new SceneReloadStateProvider(environment);
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.RegisterFallback(provider).Succeeded);
            Assert.IsFalse(registry.HasOutcomeProvider);
            var coordinator = new TestStateOperationCoordinator(registry);
            var capture = coordinator.Capture(10);
            FinishReload(provider, environment);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status);
            Assert.AreEqual(environment.ScenePath, Encoding.UTF8.GetString(capture.Anchor.GetPayloadCopy()));
            StringAssert.StartsWith("Reload scene:", capture.Anchor.StateLabel);
            var restore = coordinator.Restore(capture.Anchor, 10);
            FinishReload(provider, environment);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, restore.Status);
            Assert.AreEqual(2, environment.Reloads);
        }

        [Test]
        public void CompletionWaitsForLoadingAndTwoGameplayFrames()
        {
            var environment = new Environment();
            var provider = new SceneReloadStateProvider(environment);
            var registry = new TestStateRegistry();
            registry.Register(provider);
            var capture = new TestStateOperationCoordinator(registry).Capture(10);
            provider.Tick();
            Assert.AreNotEqual(TestStateOperationStatus.Succeeded, capture.Status);
            environment.IsLoading = false;
            provider.Tick();
            environment.Frame++;
            provider.Tick();
            Assert.AreNotEqual(TestStateOperationStatus.Succeeded, capture.Status);
            environment.Frame++;
            provider.Tick();
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status);
        }

        [TestCase("")]
        [TestCase("Packages/Game.unity")]
        [TestCase("Assets/../Game.unity")]
        public void InvalidSceneFailsBeforeReload(string path)
        {
            var environment = new Environment { ScenePath = path };
            var registry = new TestStateRegistry();
            registry.Register(new SceneReloadStateProvider(environment));
            var capture = new TestStateOperationCoordinator(registry).Capture(10);
            Assert.AreEqual(TestStateOperationStatus.Failed, capture.Status);
            Assert.AreEqual(0, environment.Reloads);
        }

        [Test]
        public void UnsupportedEnvironmentFailsBeforeReload()
        {
            var environment = new Environment { BlockedReason = "More than one scene is loaded." };
            var registry = new TestStateRegistry();
            registry.Register(new SceneReloadStateProvider(environment));
            var capture = new TestStateOperationCoordinator(registry).Capture(10);
            Assert.AreEqual(TestStateOperationStatus.Failed, capture.Status);
            StringAssert.Contains(environment.BlockedReason, capture.Error);
            Assert.AreEqual(0, environment.Reloads);
        }

        [Test]
        public void CancelledLoadCannotCompleteLateOrOverlapAnotherLoad()
        {
            var environment = new Environment();
            var provider = new SceneReloadStateProvider(environment);
            var registry = new TestStateRegistry();
            registry.Register(provider);
            var coordinator = new TestStateOperationCoordinator(registry);
            var capture = coordinator.Capture(10);
            coordinator.Cancel();
            var premature = coordinator.Capture(10);
            Assert.AreEqual(TestStateOperationStatus.Failed, premature.Status);
            Assert.AreEqual(1, environment.Reloads);
            environment.IsLoading = false;
            provider.Tick();
            Assert.AreEqual(TestStateOperationStatus.Cancelled, capture.Status);
            var retry = coordinator.Capture(10);
            FinishReload(provider, environment);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, retry.Status);
        }

        [Test]
        public void GameSceneTransitionDuringSetupIsNotReportedAsReady()
        {
            var environment = new Environment();
            var provider = new SceneReloadStateProvider(environment);
            var registry = new TestStateRegistry();
            registry.Register(provider);
            var capture = new TestStateOperationCoordinator(registry).Capture(10);
            environment.ScenePath = "Assets/Scenes/Other.unity";
            FinishReload(provider, environment);
            Assert.AreEqual(TestStateOperationStatus.Failed, capture.Status);
        }

        [Test]
        public void FailedLoadAllowsRetry()
        {
            var environment = new Environment { ThrowOnLoad = true };
            var provider = new SceneReloadStateProvider(environment);
            var registry = new TestStateRegistry();
            registry.Register(provider);
            var coordinator = new TestStateOperationCoordinator(registry);
            Assert.AreEqual(TestStateOperationStatus.Failed, coordinator.Capture(10).Status);
            environment.ThrowOnLoad = false;
            var retry = coordinator.Capture(10);
            FinishReload(provider, environment);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, retry.Status);
        }

        [Test]
        public void GameOwnedProviderCanArriveAfterFallbackAndStillTakesPriority()
        {
            var registry = new TestStateRegistry();
            var fallback = new SceneReloadStateProvider(new Environment());
            var primary = new SceneReloadStateProvider(new Environment());
            registry.RegisterFallback(fallback);
            var fallbackRevision = registry.Revision;
            Assert.IsTrue(registry.Register(primary).Succeeded);
            Assert.Greater(registry.Revision, fallbackRevision);
            registry.TryGetProvider(out var active);
            Assert.AreSame(primary, active.Provider);
            Assert.IsTrue(registry.Unregister(primary));
            registry.TryGetProvider(out active);
            Assert.AreSame(fallback, active.Provider);
            registry.Clear();
            Assert.IsFalse(registry.HasProvider);
        }

        [Test]
        public void GenericFallbackUsesNormalDurationReplayWithoutChangingIntegrations()
        {
            Assert.AreEqual(
                TestInputReplayMode.RecordedTiming,
                TestLabSession.ReplayModeForProvider(SceneReloadStateProvider.Id));
            Assert.AreEqual(
                TestInputReplayMode.RecordedFrames,
                TestLabSession.ReplayModeForProvider("studio.game_state"));
        }

        static void FinishReload(SceneReloadStateProvider provider, Environment environment)
        {
            environment.IsLoading = false;
            provider.Tick();
            environment.Frame += 2;
            provider.Tick();
        }
    }
}
