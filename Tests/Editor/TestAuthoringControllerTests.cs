using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class TestAuthoringControllerTests
    {
        class Provider : IQamelTestStateProvider
        {
            public Action<TestStateCaptureContext> Capture;
            public Action<TestStateRestoreContext> Restore = context => context.ReadyForReplay();

            public string ProviderId => "fixture.authoring";
            public int StateFormatVersion => 1;

            public void CaptureState(TestStateCaptureContext context)
            {
                Capture(context);
            }

            public void RestoreState(TestStateRestoreContext context)
            {
                Restore(context);
            }
        }

        sealed class OutcomeProvider : Provider, IQamelTestOutcomeProvider
        {
            public string OutcomeValue = "expected";
            public string OutcomeSummary = "Expected fixture state";
            public bool ThrowOnObserve;

            public int OutcomeFormatVersion => 1;

            public TestOutcomeObservation ObserveOutcome()
            {
                if (ThrowOnObserve)
                    throw new InvalidOperationException("Forced outcome failure.");
                return new TestOutcomeObservation(OutcomeValue, OutcomeSummary);
            }
        }

        sealed class FakeTraceAdapter : ITestInputTraceAdapter
        {
            readonly TestInputTraceMetrics _metrics;

            public FakeTraceAdapter(byte[] liveBytes, bool overwritten = false, int maximumBytes = 1024)
            {
                LiveBytes = liveBytes;
                _metrics = MetricsFor(liveBytes.Length, overwritten, maximumBytes);
            }

            public byte[] LiveBytes { get; }
            public int StartCount { get; private set; }
            public int PauseCount { get; private set; }
            public int ResumeCount { get; private set; }
            public int ReplayStartCount { get; private set; }
            public byte[] LastReplayBytes { get; private set; }
            public bool IsAvailable => true;
            public string UnavailableReason => null;
            public TestInputTraceStatus Status { get; private set; } = TestInputTraceStatus.Idle;
            public TestInputTraceMetrics Metrics => _metrics;
            public TestInputReplayStatus ReplayStatus { get; private set; } =
                TestInputReplayStatus.Idle;
            public TestInputReplayDiagnostics ReplayDiagnostics { get; private set; } =
                TestInputReplayDiagnostics.Empty();
            public TestInputTraceErrorCode ReplayErrorCode { get; private set; }
            public string ReplayError { get; private set; }

            public TestInputTraceResult StartCapture()
            {
                StartCount++;
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceResult PauseCapture()
            {
                if (Status != TestInputTraceStatus.Capturing)
                    return TestInputTraceResult.Failure(
                        TestInputTraceErrorCode.InvalidState, "fake capture is not running");
                PauseCount++;
                Status = TestInputTraceStatus.Paused;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceResult ResumeCapture()
            {
                if (Status != TestInputTraceStatus.Paused)
                    return TestInputTraceResult.Failure(
                        TestInputTraceErrorCode.InvalidState, "fake capture is not paused");
                ResumeCount++;
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }

            public TestInputTraceSnapshotResult PauseAndSnapshot()
            {
                var pause = PauseCapture();
                if (!pause.Succeeded)
                    return TestInputTraceSnapshotResult.Failure(pause.ErrorCode, pause.Error);
                if (_metrics.OverwroteEvents)
                {
                    return TestInputTraceSnapshotResult.Failure(
                        TestInputTraceErrorCode.Overwritten,
                        "The input buffer overwrote actions recorded after the state anchor.");
                }

                return TestInputTraceSnapshotResult.Success(
                    new TestInputTraceSnapshot(LiveBytes, _metrics));
            }

            public void StopCapture()
            {
                Status = TestInputTraceStatus.Idle;
            }

            public TestInputTraceResult StartReplay(TestInputTraceSnapshot snapshot)
            {
                if (Status != TestInputTraceStatus.Paused)
                {
                    return TestInputTraceResult.Failure(
                        TestInputTraceErrorCode.InvalidState,
                        "fake capture must be paused");
                }

                ReplayStartCount++;
                LastReplayBytes = snapshot.GetBytesCopy();
                ReplayStatus = TestInputReplayStatus.Replaying;
                ReplayDiagnostics = new TestInputReplayDiagnostics(
                    "Frame by frame", 2, 0, new[] { "Keyboard -> Fake keyboard" });
                return TestInputTraceResult.Success();
            }

            public bool CancelReplay()
            {
                if (ReplayStatus != TestInputReplayStatus.Replaying)
                    return false;
                ReplayStatus = TestInputReplayStatus.Cancelled;
                return true;
            }

            public void CompleteReplay()
            {
                ReplayStatus = TestInputReplayStatus.Completed;
            }

            public void ReportPhysicalInput(long eventCount)
            {
                ReplayDiagnostics = new TestInputReplayDiagnostics(
                    "Frame by frame", 2, eventCount, new[] { "Keyboard -> Fake keyboard" });
            }

            public void Dispose()
            {
                Status = TestInputTraceStatus.Disposed;
                ReplayStatus = TestInputReplayStatus.Disposed;
            }

            static TestInputTraceMetrics MetricsFor(int bytes, bool overwritten, int maximumBytes)
            {
                return new TestInputTraceMetrics(
                    retainedEventCount: 4,
                    recordedEventCount: overwritten ? 7 : 4,
                    retainedStateEventCount: 2,
                    retainedEventBytes: bytes,
                    allocatedBytes: maximumBytes,
                    maximumBytes: maximumBytes,
                    hasEventTimes: true,
                    oldestEventTime: 100,
                    newestEventTime: 102.5,
                    deviceLayouts: new[] { "Keyboard" });
            }
        }

        [Test]
        public void ControllerCapturesAnchorFreezesDraftAndResumesAroundReplay()
        {
            TestStateCaptureContext pendingCapture = null;
            var restoreCount = 0;
            var provider = new Provider
            {
                Capture = context => pendingCapture = context,
                Restore = context =>
                {
                    restoreCount++;
                    context.ReadyForReplay();
                }
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 3, 5, 8, 13 });
            double sessionTime = 10;
            var frame = 1;
            var evidence = new TestEvidenceBoundary(() => sessionTime);

            using (var controller = new TestAuthoringController(
                       registry, adapter, evidence, () => frame))
            {
                var stateCapture = controller.CaptureStartingState(2);
                Assert.AreEqual(TestStateOperationStatus.Pending, stateCapture.Status);
                Assert.AreEqual(
                    TestAuthoringControllerState.CapturingStartingState,
                    controller.State);

                Assert.IsTrue(pendingCapture.Succeed("Fixture start", new byte[] { 21, 34 }));
                controller.Tick(0);
                Assert.AreEqual(
                    TestAuthoringControllerState.ReadyForDemonstration,
                    controller.State);
                Assert.IsFalse(controller.IsBackgroundRecording);
                Assert.AreEqual(0, adapter.StartCount);
                Assert.IsFalse(controller.CanCreateDraft);
                Assert.IsFalse(controller.TryCreateDraft(
                    "Too early",
                    "No input has been recorded.",
                    out _));

                sessionTime = 11;
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                Assert.AreEqual(TestAuthoringControllerState.Recording, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);
                Assert.AreEqual(1, adapter.StartCount);
                Assert.AreEqual(1, restoreCount);

                sessionTime = 14;
                Assert.IsTrue(controller.TryCreateDraft(
                    "Open locked door",
                    "The key is consumed and the door opens.",
                    out var draft), controller.LastError);
                Assert.AreSame(draft, controller.CurrentDraft);
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);
                Assert.AreEqual(1, adapter.ResumeCount);
                Assert.AreEqual("Open locked door", draft.Name);
                Assert.AreEqual("The key is consumed and the door opens.", draft.ExpectedOutcome);
                Assert.IsTrue(draft.DemonstrationEvidence.HasSessionTimes);
                Assert.AreEqual(11, draft.DemonstrationEvidence.StartSessionTime);
                Assert.AreEqual(14, draft.DemonstrationEvidence.EndSessionTime);
                Assert.AreEqual(2.5, draft.InputTrace.Metrics.RetainedDurationSeconds);

                adapter.LiveBytes[0] = 99;
                CollectionAssert.AreEqual(
                    new byte[] { 3, 5, 8, 13 },
                    draft.InputTrace.GetBytesCopy(),
                    "continued background capture must not mutate the draft snapshot");

                sessionTime = 20;
                Assert.IsTrue(controller.StartReplay(2, 10, 0), controller.LastError);
                Assert.AreEqual(TestAuthoringControllerState.Settling, controller.State);
                frame++;
                controller.Tick(0);
                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);
                Assert.AreEqual(1, adapter.ReplayStartCount);
                sessionTime = 23;
                adapter.CompleteReplay();
                controller.Tick(3);
                Assert.AreEqual(TestAuthoringControllerState.NeedsReview, controller.State);
                Assert.AreEqual(3, controller.LastReplayEvidence.DurationSeconds);
                Assert.IsTrue(controller.RecordHumanVerdict(
                    TestHumanVerdict.Worked, "Door opened."));
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);
                Assert.AreEqual(2, adapter.ResumeCount);
                Assert.AreEqual(1, draft.ValidationHistory.Count);
                Assert.AreEqual(LocalTestRunStatus.Pass, draft.ValidationHistory[0].Status);
                Assert.AreEqual("Door opened.", draft.ValidationHistory[0].ReviewNote);
            }
        }

        [Test]
        public void DraftDetailsCanBeReviewedOnceBeforeValidation()
        {
            var provider = new OutcomeProvider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 1;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                Assert.IsTrue(controller.ReviseDraftMetadata(
                    "  Browser reviewed name  ",
                    "  The player lands safely.  "));
                Assert.AreEqual("Browser reviewed name", controller.CurrentDraft.Name);
                Assert.AreEqual(
                    "The player lands safely.",
                    controller.CurrentDraft.ExpectedOutcome);

                Assert.IsFalse(controller.ReviseDraftMetadata("", "Still valid"));
                Assert.AreEqual("Browser reviewed name", controller.CurrentDraft.Name);

                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.CompleteReplay();
                controller.Tick(0.1f);
                Assert.AreEqual(1, controller.CurrentDraft.ValidationHistory.Count);

                Assert.IsFalse(controller.ReviseDraftMetadata(
                    "Changed after validation",
                    "This must not replace reviewed history."));
                Assert.AreEqual("Browser reviewed name", controller.CurrentDraft.Name);
            }
        }

        [Test]
        public void OverwrittenAnchorRangeCannotCreateIncompleteDraft()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 }, overwritten: true);

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                var capture = controller.CaptureStartingState(1);
                Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status);
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                Assert.IsFalse(controller.CanCreateDraft);
                Assert.IsFalse(controller.TryCreateDraft(
                    "Incomplete flow", "The flow completes.", out var draft));
                Assert.IsNull(draft);
                Assert.IsNull(controller.CurrentDraft);
                Assert.AreEqual(TestInputTraceErrorCode.Overwritten, controller.LastInputErrorCode);
                Assert.AreEqual(TestAuthoringControllerState.AnchorExpired, controller.State);
                StringAssert.Contains("overwrote", controller.LastError);
                Assert.IsFalse(controller.IsBackgroundRecording);
            }
        }

        [Test]
        public void AuthoringTraceRemainsSeparateFromReportInputEvidence()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.captureInput = true;
            var reportBuffer = new SessionBuffer(30, 4);
            var reportInput = new InputRecorder(settings, reportBuffer, () => 7.5);
            reportInput.Emit("key_down", "Space");

            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 222, 173, 190, 239 });

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                controller.CaptureStartingState(1);
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                Assert.IsTrue(controller.TryCreateDraft(
                    "Report coexistence", "Readable input remains separate.", out var draft),
                    controller.LastError);
                Assert.AreEqual(4, draft.InputTrace.SerializedByteCount);
            }

            var reportEvents = new List<string>();
            var reportFrames = new List<CapturedFrame>();
            reportBuffer.Snapshot(reportEvents, reportFrames);
            Assert.AreEqual(1, reportEvents.Count);
            StringAssert.Contains("\"type\":\"input\"", reportEvents[0]);
            StringAssert.Contains("\"key\":\"Space\"", reportEvents[0]);
            Assert.AreEqual(0, reportFrames.Count);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        [Test]
        public void FailedRecaptureKeepsThePreviousAnchorDraftAndRecording()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("First start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 2, 3, 5, 7 });

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                var first = controller.CaptureStartingState(1);
                Assert.AreEqual(TestStateOperationStatus.Succeeded, first.Status);
                Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
                Assert.IsTrue(controller.TryCreateDraft(
                    "First draft", "The first flow completes.", out var firstDraft));
                var firstAnchor = controller.CurrentAnchor;

                provider.Capture = context => context.Fail("fixture save unavailable");
                var failed = controller.CaptureStartingState(1);

                Assert.AreEqual(TestStateOperationStatus.Failed, failed.Status);
                Assert.AreEqual(TestStateErrorCode.ProviderError, failed.ErrorCode);
                Assert.AreSame(firstAnchor, controller.CurrentAnchor);
                Assert.AreSame(firstDraft, controller.CurrentDraft);
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);
                StringAssert.Contains("fixture save unavailable", controller.LastError);
            }
        }

        [Test]
        public void PendingStartingStateCaptureCanBeCancelledFromTheController()
        {
            TestStateCaptureContext pending = null;
            var provider = new Provider { Capture = context => pending = context };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);

            using (var controller = new TestAuthoringController(
                       registry,
                       new FakeTraceAdapter(new byte[] { 1 })))
            {
                var operation = controller.CaptureStartingState(5);
                Assert.AreEqual(TestStateOperationStatus.Pending, operation.Status);
                Assert.IsTrue(controller.CancelStateCapture());
                Assert.AreEqual(TestStateOperationStatus.Cancelled, operation.Status);
                Assert.AreEqual(TestAuthoringControllerState.Error, controller.State);
                Assert.AreEqual(TestStateErrorCode.Cancelled, controller.LastStateErrorCode);
                Assert.IsTrue(pending.IsCancellationRequested);
            }
        }

        [Test]
        public void ProviderReplacementExpiresTheCurrentAnchorWithAGuidedReason()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);

            using (var controller = new TestAuthoringController(
                       registry,
                       new FakeTraceAdapter(new byte[] { 1, 2, 3 })))
            {
                controller.CaptureStartingState(1);
                Assert.AreEqual(
                    TestAuthoringControllerState.ReadyForDemonstration,
                    controller.State);
                Assert.IsTrue(registry.Unregister(provider));

                controller.Tick(0);

                Assert.AreEqual(TestAuthoringControllerState.AnchorExpired, controller.State);
                Assert.AreEqual(TestStateErrorCode.IncompatibleAnchor, controller.LastStateErrorCode);
                Assert.IsFalse(controller.IsBackgroundRecording);
                StringAssert.Contains("Capture a new starting state", controller.LastError);
            }
        }

        [Test]
        public void RestoreProviderErrorIsRunErrorNotGameplayFail()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });

            using (var controller = CreateDraftReady(provider, adapter))
            {
                provider.Restore = context => context.Fail("forced restore error");
                Assert.IsFalse(controller.StartReplay(1, 5, 0));
                Assert.AreEqual(TestAuthoringControllerState.ReplayError, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Error, controller.CurrentRun.Status);
                Assert.AreEqual(TestStateErrorCode.ProviderError, controller.CurrentRun.StateErrorCode);
                Assert.AreNotEqual(LocalTestRunStatus.Fail, controller.CurrentRun.Status);
                Assert.AreEqual(LocalTestStageStatus.Error, controller.CurrentRun.StateStageStatus);
                Assert.IsTrue(controller.IsBackgroundRecording);
                StringAssert.Contains("forced restore error", controller.LastError);
            }
        }

        [Test]
        public void CancellingRestoreOrReplayLeavesTheDraftRunnable()
        {
            TestStateRestoreContext pendingRestore = null;
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 4;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                provider.Restore = context => pendingRestore = context;
                Assert.IsTrue(controller.StartReplay(5, 5, 0));
                Assert.AreEqual(TestAuthoringControllerState.Restoring, controller.State);
                Assert.IsTrue(controller.CancelRun());
                Assert.IsTrue(pendingRestore.IsCancellationRequested);
                Assert.AreEqual(LocalTestRunStatus.Cancelled, controller.CurrentRun.Status);
                Assert.AreEqual(TestAuthoringControllerState.ReplayCancelled, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);

                provider.Restore = context => context.ReadyForReplay();
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);
                Assert.IsTrue(controller.CancelRun());
                Assert.AreEqual(TestInputReplayStatus.Cancelled, adapter.ReplayStatus);
                Assert.AreEqual(LocalTestRunStatus.Cancelled, controller.CurrentRun.Status);
                Assert.AreEqual(TestAuthoringControllerState.ReplayCancelled, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);
                Assert.AreEqual(2, controller.CurrentDraft.ValidationHistory.Count);
            }
        }

        [Test]
        public void ProviderReplacementDuringPendingRestoreStopsBeforeInputReplay()
        {
            TestStateRestoreContext pendingRestore = null;
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });

            using (var controller = CreateDraftReady(registry, adapter))
            {
                provider.Restore = context => pendingRestore = context;
                Assert.IsTrue(controller.StartReplay(5, 5, 1), controller.LastError);
                Assert.AreEqual(TestAuthoringControllerState.Restoring, controller.State);
                Assert.NotNull(pendingRestore);
                Assert.IsTrue(registry.Unregister(provider));
                Assert.IsTrue(registry.Register(new Provider
                {
                    Capture = context => context.Succeed("Replacement", new byte[] { 9 })
                }).Succeeded);
                Assert.IsTrue(pendingRestore.ReadyForReplay());

                controller.Tick(0);

                Assert.AreEqual(TestAuthoringControllerState.ReplayError, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Error, controller.CurrentRun.Status);
                Assert.AreEqual(
                    TestStateErrorCode.IncompatibleAnchor,
                    controller.CurrentRun.StateErrorCode);
                Assert.AreEqual(0, adapter.ReplayStartCount);
                StringAssert.Contains("before input was applied", controller.LastError);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProviderReplacementDuringSettleOrCountdownStopsBeforeInputReplay(
            bool reachCountdown)
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 10;

            using (var controller = CreateDraftReady(
                       registry,
                       adapter,
                       () => frame))
            {
                Assert.IsTrue(controller.StartReplay(1, 5, 3), controller.LastError);
                Assert.AreEqual(TestAuthoringControllerState.Settling, controller.State);
                if (reachCountdown)
                {
                    frame++;
                    controller.Tick(0);
                    Assert.AreEqual(
                        TestAuthoringControllerState.ReplayCountdown,
                        controller.State);
                }

                Assert.IsTrue(registry.Unregister(provider));
                Assert.IsTrue(registry.Register(new Provider
                {
                    Capture = context => context.Succeed("Replacement", new byte[] { 9 })
                }).Succeeded);
                controller.Tick(0);

                Assert.AreEqual(TestAuthoringControllerState.ReplayError, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Error, controller.CurrentRun.Status);
                Assert.AreEqual(0, adapter.ReplayStartCount);
                StringAssert.Contains("before input was applied", controller.LastError);
            }
        }

        [Test]
        public void ProviderTransitionAfterInputReplayStartsDoesNotCancelTheAction()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 20;

            using (var controller = CreateDraftReady(
                       registry,
                       adapter,
                       () => frame))
            {
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);

                Assert.IsTrue(registry.Unregister(provider));
                controller.Tick(0);

                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);
                Assert.AreEqual(1, adapter.ReplayStartCount);
            }
        }

        [Test]
        public void ReplayTimeoutIsAnInfrastructureError()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 8;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                Assert.IsTrue(controller.StartReplay(1, 0.5f, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                Assert.AreEqual(TestAuthoringControllerState.ReplayRunning, controller.State);

                controller.Tick(0.5f);
                Assert.AreEqual(TestAuthoringControllerState.ReplayError, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Error, controller.CurrentRun.Status);
                Assert.AreEqual(TestInputTraceErrorCode.ReplayTimeout, controller.CurrentRun.InputErrorCode);
                Assert.AreNotEqual(LocalTestRunStatus.Fail, controller.CurrentRun.Status);
                Assert.IsTrue(controller.IsBackgroundRecording);
                StringAssert.Contains("timed out", controller.LastError);
            }
        }

        [Test]
        public void HumanDidNotWorkMarksTheRunAsFail()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 11;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.CompleteReplay();
                controller.Tick(0.1f);

                Assert.AreEqual(TestAuthoringControllerState.NeedsReview, controller.State);
                Assert.IsTrue(controller.RecordHumanVerdict(
                    TestHumanVerdict.DidNotWork, "Door stayed closed."));
                Assert.AreEqual(LocalTestRunStatus.Fail, controller.CurrentRun.Status);
                Assert.AreEqual(LocalTestStageStatus.Fail, controller.CurrentRun.CheckStageStatus);
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.AreEqual("Door stayed closed.", controller.CurrentDraft.ValidationHistory[0].ReviewNote);
            }
        }

        [Test]
        public void GameOwnedOutcomeSignalProducesAutomaticPassWithoutHumanReview()
        {
            var provider = new OutcomeProvider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 11;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                Assert.IsNotNull(controller.CurrentDraft.ExpectedObservation);
                Assert.AreEqual(
                    "expected",
                    controller.CurrentDraft.ExpectedObservation.Observation.Value);

                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.CompleteReplay();
                controller.Tick(0.1f);

                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Pass, controller.CurrentRun.Status);
                Assert.AreEqual(LocalTestStageStatus.Pass, controller.CurrentRun.CheckStageStatus);
                Assert.IsTrue(controller.CurrentRun.OutcomeComparison.Matches);
                Assert.AreEqual(
                    "Expected fixture state",
                    controller.CurrentRun.OutcomeComparison.Actual.Summary);
                Assert.IsTrue(controller.CurrentRun.IsTerminal);
            }
        }

        [Test]
        public void PhysicalInputWarningDoesNotOverrideReliableAutomaticPass()
        {
            var provider = new OutcomeProvider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 11;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.ReportPhysicalInput(2);
                adapter.CompleteReplay();
                controller.Tick(0.1f);

                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.AreEqual(LocalTestRunStatus.Pass, controller.CurrentRun.Status);
                Assert.AreEqual(LocalTestStageStatus.Pass, controller.CurrentRun.CheckStageStatus);
                Assert.AreEqual(2, controller.CurrentRun.AdapterDiagnostics.PhysicalInputEventCount);
                Assert.IsTrue(controller.CurrentRun.AdapterDiagnostics.PhysicalInputDetected);
            }
        }

        [Test]
        public void GameOwnedOutcomeMismatchIsFailAndProviderFaultIsError()
        {
            var provider = new OutcomeProvider
            {
                Capture = context => context.Succeed("Fixture start", new byte[] { 1 })
            };
            var adapter = new FakeTraceAdapter(new byte[] { 1, 2, 3 });
            var frame = 11;

            using (var controller = CreateDraftReady(provider, adapter, () => frame))
            {
                provider.OutcomeValue = "actual-mismatch";
                provider.OutcomeSummary = "Door stayed closed";
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.CompleteReplay();
                controller.Tick(0.1f);

                Assert.AreEqual(LocalTestRunStatus.Fail, controller.CurrentRun.Status);
                Assert.IsFalse(controller.CurrentRun.OutcomeComparison.Matches);
                Assert.AreEqual(LocalTestStageStatus.Fail, controller.CurrentRun.CheckStageStatus);
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);

                provider.ThrowOnObserve = true;
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                adapter.CompleteReplay();
                controller.Tick(0.1f);

                Assert.AreEqual(LocalTestRunStatus.Error, controller.CurrentRun.Status);
                Assert.AreEqual(
                    TestOutcomeErrorCode.ProviderError,
                    controller.CurrentRun.OutcomeErrorCode);
                Assert.AreEqual(LocalTestStageStatus.Error, controller.CurrentRun.CheckStageStatus);
                Assert.AreEqual(TestAuthoringControllerState.ReplayError, controller.State);
                StringAssert.Contains("Forced outcome failure", controller.CurrentRun.Error);
            }
        }

        [Test]
        public void DownloadedDraftBindsCurrentProviderAndReplaysExactTraceBytes()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Unused", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 99 });
            var frame = 1;
            var traceBytes = new byte[] { 3, 5, 8, 13, 21 };
            var anchor = new TestStateAnchor(
                provider.ProviderId,
                provider.StateFormatVersion,
                "Downloaded start",
                new byte[] { 34, 55 },
                "Fixture",
                "Assets/Fixture.unity");
            var snapshot = new TestInputTraceSnapshot(
                traceBytes,
                new TestInputTraceMetrics(
                    retainedEventCount: 2,
                    recordedEventCount: 2,
                    retainedStateEventCount: 2,
                    retainedEventBytes: traceBytes.Length,
                    allocatedBytes: traceBytes.Length,
                    maximumBytes: traceBytes.Length,
                    hasEventTimes: true,
                    oldestEventTime: 0,
                    newestEventTime: 1.25,
                    deviceLayouts: new[] { "Keyboard" }));
            var draft = new RecordedTestDraft(
                "Downloaded Test",
                "The saved behavior completes.",
                anchor,
                snapshot,
                new TestEvidenceRange(false, 0, 0));

            using (var controller = new TestAuthoringController(
                       registry,
                       adapter,
                       frameCounter: () => frame))
            {
                Assert.IsTrue(controller.TryLoadDraftForReplay(draft), controller.LastError);
                Assert.AreSame(draft, controller.CurrentDraft);
                Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                Assert.IsTrue(controller.IsBackgroundRecording);

                traceBytes[0] = 255;
                Assert.IsTrue(controller.StartReplay(1, 5, 0), controller.LastError);
                frame++;
                controller.Tick(0);
                CollectionAssert.AreEqual(
                    new byte[] { 3, 5, 8, 13, 21 },
                    adapter.LastReplayBytes);
            }
        }

        [Test]
        public void DownloadedDraftRejectsProviderMismatchBeforeCaptureOrRestore()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Unused", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1 });
            var anchor = new TestStateAnchor(
                "another.provider",
                provider.StateFormatVersion,
                "Downloaded start",
                new byte[] { 2 },
                "Fixture",
                "Assets/Fixture.unity");
            var snapshot = new TestInputTraceSnapshot(
                new byte[] { 3 },
                new TestInputTraceMetrics(
                    1, 1, 1, 1, 1, 1, true, 0, 1, new[] { "Keyboard" }));
            var draft = new RecordedTestDraft(
                "Downloaded Test",
                "The saved behavior completes.",
                anchor,
                snapshot,
                new TestEvidenceRange(false, 0, 0));

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                Assert.IsFalse(controller.TryLoadDraftForReplay(draft));
                Assert.AreEqual(0, adapter.StartCount);
                Assert.IsNull(controller.CurrentDraft);
                Assert.AreEqual(TestStateErrorCode.IncompatibleAnchor, controller.LastStateErrorCode);
                StringAssert.Contains("another.provider", controller.LastError);
            }
        }

        [Test]
        public void DownloadedDraftCannotReplaceAnActiveLocalAuthoringFlow()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Local start", new byte[] { 1, 2 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 3, 4 });
            var downloadedDraft = new RecordedTestDraft(
                "Downloaded Test",
                "The saved behavior completes.",
                new TestStateAnchor(
                    provider.ProviderId,
                    provider.StateFormatVersion,
                    "Downloaded start",
                    new byte[] { 5, 6 },
                    "Fixture",
                    "Assets/Fixture.unity"),
                new TestInputTraceSnapshot(
                    new byte[] { 7, 8 },
                    new TestInputTraceMetrics(
                        1, 1, 1, 2, 2, 2, true, 0, 1, new[] { "Keyboard" })),
                new TestEvidenceRange(false, 0, 0));

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                Assert.IsNull(TestLabSession.SavedReplayLocalStateBlockReason(controller));
                controller.CaptureStartingState(1);
                Assert.AreEqual(
                    TestAuthoringControllerState.ReadyForDemonstration,
                    controller.State);
                var localAnchor = controller.CurrentAnchor;

                StringAssert.Contains(
                    "authoring flow",
                    TestLabSession.SavedReplayLocalStateBlockReason(controller));

                Assert.IsFalse(controller.TryLoadDraftForReplay(downloadedDraft));

                Assert.AreSame(localAnchor, controller.CurrentAnchor);
                Assert.IsNull(controller.CurrentDraft);
                Assert.AreEqual(
                    TestAuthoringControllerState.ReadyForDemonstration,
                    controller.State);
                Assert.IsFalse(controller.IsBackgroundRecording);
                StringAssert.Contains("clean, idle", controller.LastError);
            }
        }

        [Test]
        public void ProviderReplacementAfterDownloadedDraftLoadExpiresIt()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Unused", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1 });
            var anchor = new TestStateAnchor(
                provider.ProviderId,
                provider.StateFormatVersion,
                "Downloaded start",
                new byte[] { 2 },
                "Fixture",
                "Assets/Fixture.unity");
            var snapshot = new TestInputTraceSnapshot(
                new byte[] { 3 },
                new TestInputTraceMetrics(
                    1, 1, 1, 1, 1, 1, true, 0, 1, new[] { "Keyboard" }));
            var draft = new RecordedTestDraft(
                "Downloaded Test",
                "The saved behavior completes.",
                anchor,
                snapshot,
                new TestEvidenceRange(false, 0, 0));

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                Assert.IsTrue(controller.TryLoadDraftForReplay(draft), controller.LastError);
                Assert.IsTrue(registry.Unregister(provider));
                Assert.IsTrue(registry.Register(provider).Succeeded);

                controller.Tick(0);

                Assert.AreEqual(TestAuthoringControllerState.AnchorExpired, controller.State);
                Assert.IsFalse(controller.IsBackgroundRecording);
                Assert.AreEqual(TestStateErrorCode.IncompatibleAnchor, controller.LastStateErrorCode);

                var model = TestLabWindowModel.Create(
                    true, true, true, null, false, controller, "", "");
                Assert.IsTrue(model.ShowDraft);
                Assert.IsTrue(model.CanClearDraft);

                Assert.IsTrue(controller.ClearDraft());
                Assert.AreEqual(TestAuthoringControllerState.Idle, controller.State);
                Assert.IsNull(controller.CurrentAnchor);
                Assert.IsNull(controller.CurrentDraft);
                Assert.IsFalse(controller.IsBackgroundRecording);
            }
        }

        [Test]
        public void LoadedSavedProvenanceSurvivesFailedRecaptureUntilDraftIsReplaced()
        {
            var provider = new Provider
            {
                Capture = context => context.Fail("Fixture recapture failed.")
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 1 });
            var draft = new RecordedTestDraft(
                "Downloaded Test",
                "The saved behavior completes.",
                new TestStateAnchor(
                    provider.ProviderId,
                    provider.StateFormatVersion,
                    "Downloaded start",
                    new byte[] { 2 },
                    "Fixture",
                    "Assets/Fixture.unity"),
                new TestInputTraceSnapshot(
                    new byte[] { 3 },
                    new TestInputTraceMetrics(
                        1, 1, 1, 1, 1, 1, true, 0, 1, new[] { "Keyboard" })),
                new TestEvidenceRange(false, 0, 0));

            using (var controller = new TestAuthoringController(registry, adapter))
            {
                Assert.IsTrue(controller.TryLoadDraftForReplay(draft), controller.LastError);

                var failedCapture = controller.CaptureStartingState(1);

                Assert.AreEqual(TestStateOperationStatus.Failed, failedCapture.Status);
                Assert.AreSame(draft, controller.CurrentDraft);
                Assert.IsFalse(
                    TestLabSession.ShouldClearLoadedSavedVersionAfterDraftReplacement(
                        hasLoadedSavedVersion: true,
                        controller.CurrentDraft));

                provider.Capture = context =>
                    context.Succeed("Replacement start", new byte[] { 4 });
                var successfulCapture = controller.CaptureStartingState(1);

                Assert.AreEqual(TestStateOperationStatus.Succeeded, successfulCapture.Status);
                Assert.IsNull(controller.CurrentDraft);
                Assert.IsTrue(
                    TestLabSession.ShouldClearLoadedSavedVersionAfterDraftReplacement(
                        hasLoadedSavedVersion: true,
                        controller.CurrentDraft));
            }
        }

        [Test]
        public void DisablingExperimentsRetainsActiveRecordingReplayAndDeclinedDraftDiscard()
        {
            var provider = new Provider
            {
                Capture = context => context.Succeed("Starting state", new byte[] { 1 })
            };
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            var adapter = new FakeTraceAdapter(new byte[] { 2, 3 });
            using (var controller = new TestAuthoringController(registry, adapter))
            {
                var controllerField = typeof(TestLabSession).GetField("_controller",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var previousController = controllerField.GetValue(null);
                bool wasEnabled = TestLabPreferences.IsEnabled;
                try
                {
                    controllerField.SetValue(null, controller);
                    TestLabPreferences.IsEnabled = true;
                    controller.CaptureStartingState(1);
                    Assert.IsTrue(controller.StartDemonstration(1));

                    Assert.IsFalse(TestLabSession.DisableExperimentalFeature(() =>
                    {
                        Assert.Fail("Active recording must stop before offering to discard a draft.");
                        return true;
                    }));
                    Assert.AreEqual(TestAuthoringControllerState.Recording, controller.State);
                    Assert.IsTrue(controller.IsBackgroundRecording);
                    Assert.IsTrue(TestLabPreferences.IsEnabled);

                    Assert.IsTrue(controller.TryCreateDraft("Recorded action", "Expected result", out var draft));
                    Assert.IsTrue(TestLabSession.CanDisableExperimentalFeature);
                    Assert.IsFalse(TestLabSession.DisableExperimentalFeature());
                    bool asked = false;
                    Assert.IsFalse(TestLabSession.DisableExperimentalFeature(() => { asked = true; return false; }));
                    Assert.IsTrue(asked);
                    Assert.AreSame(draft, controller.CurrentDraft);
                    Assert.AreEqual(TestAuthoringControllerState.DraftReady, controller.State);
                    Assert.IsTrue(TestLabPreferences.IsEnabled);

                    Assert.IsTrue(controller.StartReplay(1, 10, 0));
                    Assert.IsFalse(TestLabSession.DisableExperimentalFeature(() => true));
                    Assert.IsTrue(controller.IsRunActive);
                    Assert.AreSame(draft, controller.CurrentDraft);
                    Assert.IsTrue(TestLabPreferences.IsEnabled);
                }
                finally
                {
                    controllerField.SetValue(null, previousController);
                    TestLabPreferences.IsEnabled = wasEnabled;
                }
            }
        }

        static TestAuthoringController CreateDraftReady(
            Provider provider,
            FakeTraceAdapter adapter,
            Func<int> frameCounter = null)
        {
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(provider).Succeeded);
            return CreateDraftReady(registry, adapter, frameCounter);
        }

        static TestAuthoringController CreateDraftReady(
            TestStateRegistry registry,
            FakeTraceAdapter adapter,
            Func<int> frameCounter = null)
        {
            var controller = new TestAuthoringController(
                registry, adapter, frameCounter: frameCounter);
            var capture = controller.CaptureStartingState(1);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status, capture.Error);
            Assert.IsTrue(controller.StartDemonstration(1), controller.LastError);
            Assert.IsTrue(controller.TryCreateDraft(
                "Open locked door", "The door opens.", out _), controller.LastError);
            return controller;
        }
    }
}
