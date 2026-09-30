using System;
using NUnit.Framework;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class TestStateProviderTests
    {
        sealed class Provider : IQamelTestStateProvider
        {
            public string Id = "fixture.state";
            public int Version = 1;
            public Action<TestStateCaptureContext> Capture =
                context => context.Succeed("Fixture state", new byte[] { 1, 2, 3 });
            public Action<TestStateRestoreContext> Restore =
                context => context.ReadyForReplay();

            public string ProviderId => Id;
            public int StateFormatVersion => Version;

            public void CaptureState(TestStateCaptureContext context)
            {
                Capture(context);
            }

            public void RestoreState(TestStateRestoreContext context)
            {
                Restore(context);
            }
        }

        [Test]
        public void RegistryRejectsDuplicateProviderAndSupportsExactTeardown()
        {
            var registry = new TestStateRegistry();
            var first = new Provider();
            var second = new Provider { Id = "fixture.other" };

            Assert.IsTrue(registry.Register(first).Succeeded);
            var duplicate = registry.Register(second);

            Assert.IsFalse(duplicate.Succeeded);
            Assert.AreEqual(TestStateErrorCode.DuplicateProvider, duplicate.ErrorCode);
            Assert.AreEqual("fixture.state", registry.ProviderId);
            Assert.IsFalse(registry.Unregister(second));
            Assert.IsTrue(registry.Unregister(first));
            Assert.IsFalse(registry.HasProvider);
            Assert.IsTrue(registry.Register(second).Succeeded);
        }

        [Test]
        public void MissingTimedOutAndThrowingProvidersHaveDistinctErrors()
        {
            var missingRegistry = new TestStateRegistry();
            var missing = new TestStateOperationCoordinator(missingRegistry).Capture(1);
            Assert.AreEqual(TestStateErrorCode.MissingProvider, missing.ErrorCode);

            var duplicateRegistry = new TestStateRegistry();
            Assert.IsTrue(duplicateRegistry.Register(new Provider()).Succeeded);
            var duplicate = duplicateRegistry.Register(new Provider());
            Assert.AreEqual(TestStateErrorCode.DuplicateProvider, duplicate.ErrorCode);

            TestStateCaptureContext pendingContext = null;
            var timeoutProvider = new Provider
            {
                Capture = context => pendingContext = context
            };
            var timeoutRegistry = new TestStateRegistry();
            Assert.IsTrue(timeoutRegistry.Register(timeoutProvider).Succeeded);
            var timeoutCoordinator = new TestStateOperationCoordinator(timeoutRegistry);
            var timedOut = timeoutCoordinator.Capture(0.5f);
            timeoutCoordinator.Tick(0.5f);
            Assert.AreEqual(TestStateErrorCode.Timeout, timedOut.ErrorCode);
            Assert.IsTrue(pendingContext.IsCancellationRequested);

            var throwingProvider = new Provider
            {
                Capture = _ => throw new InvalidOperationException("fixture capture exploded")
            };
            var throwingRegistry = new TestStateRegistry();
            Assert.IsTrue(throwingRegistry.Register(throwingProvider).Succeeded);

            var originalTimeScale = Time.timeScale;
            Time.timeScale = 0.375f;
            try
            {
                var throwing = new TestStateOperationCoordinator(throwingRegistry).Capture(1);
                Assert.AreEqual(TestStateErrorCode.ProviderException, throwing.ErrorCode);
                StringAssert.Contains("fixture capture exploded", throwing.Error);
                Assert.AreEqual(0.375f, Time.timeScale,
                    "provider errors must not pause or change the game clock");
            }
            finally
            {
                Time.timeScale = originalTimeScale;
            }

            Assert.AreNotEqual(missing.ErrorCode, duplicate.ErrorCode);
            Assert.AreNotEqual(duplicate.ErrorCode, timedOut.ErrorCode);
            Assert.AreNotEqual(timedOut.ErrorCode, TestStateErrorCode.ProviderException);
        }

        [Test]
        public void AnchorAndRestoreUseDefensivePayloadCopies()
        {
            var providerPayload = new byte[] { 7, 11, 13, 17 };
            byte[] restoreCopy = null;
            var provider = new Provider
            {
                Capture = context =>
                {
                    Assert.IsTrue(context.Succeed("Opaque fixture state", providerPayload));
                    providerPayload[0] = 99;
                },
                Restore = context =>
                {
                    restoreCopy = context.GetPayloadCopy();
                    restoreCopy[1] = 88;
                    Assert.IsTrue(context.ReadyForReplay());
                }
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var coordinator = new TestStateOperationCoordinator(registry);

            var capture = coordinator.Capture(1);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status);
            Assert.AreEqual(4, capture.Anchor.PayloadByteCount);
            Assert.IsNotEmpty(capture.Anchor.PayloadChecksum);
            CollectionAssert.AreEqual(new byte[] { 7, 11, 13, 17 }, capture.Anchor.GetPayloadCopy());

            var restore = coordinator.Restore(capture.Anchor, 1);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, restore.Status);
            Assert.IsTrue(restore.ReadyForReplay);
            CollectionAssert.AreEqual(new byte[] { 7, 88, 13, 17 }, restoreCopy);
            CollectionAssert.AreEqual(new byte[] { 7, 11, 13, 17 }, capture.Anchor.GetPayloadCopy(),
                "the provider must not be able to mutate the anchor through restore input");
        }

        [Test]
        public void PendingOperationCanBeCancelledAndRejectsLateCompletion()
        {
            TestStateCaptureContext pendingContext = null;
            var provider = new Provider
            {
                Capture = context => pendingContext = context
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var coordinator = new TestStateOperationCoordinator(registry);

            var operation = coordinator.Capture(5);
            Assert.IsTrue(coordinator.Cancel());
            Assert.AreEqual(TestStateOperationStatus.Cancelled, operation.Status);
            Assert.AreEqual(TestStateErrorCode.Cancelled, operation.ErrorCode);
            Assert.IsTrue(pendingContext.IsCancellationRequested);
            Assert.IsFalse(pendingContext.Succeed("Too late", new byte[] { 1 }));
            Assert.AreEqual(TestStateOperationStatus.Cancelled, operation.Status);
        }

        [Test]
        public void ProviderFailureAndIncompatibleAnchorRemainInfrastructureErrors()
        {
            var first = new Provider();
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(first).Succeeded);
            var coordinator = new TestStateOperationCoordinator(registry);
            var capture = coordinator.Capture(1);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status);

            Assert.IsTrue(registry.Unregister(first));
            var second = new Provider { Version = 2 };
            Assert.IsTrue(registry.Register(second).Succeeded);
            var incompatible = coordinator.Restore(capture.Anchor, 1);
            Assert.AreEqual(TestStateErrorCode.IncompatibleAnchor, incompatible.ErrorCode);

            second.Capture = context => context.Fail("fixture save unavailable");
            var failed = coordinator.Capture(1);
            Assert.AreEqual(TestStateOperationStatus.Failed, failed.Status);
            Assert.AreEqual(TestStateErrorCode.ProviderError, failed.ErrorCode);
            StringAssert.Contains("fixture save unavailable", failed.Error);
        }
    }
}
