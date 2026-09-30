using System;
using System.Collections;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace QamelCapture.Tests
{
    public sealed class SceneReloadIntegrationTests
    {
        const string AssetKey = "Qamel.Tests.SceneReload.Asset";

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!Application.isBatchMode)
                Assert.Ignore("Run this scene-changing test in the CLI verification project.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var marker = new GameObject("Scene reload marker");
            marker.transform.position = new Vector3(1, 2, 3);
            var path = "Assets/QamelSceneReloadProbe_" + Guid.NewGuid().ToString("N") + ".unity";
            SessionState.SetString(AssetKey, path);
            Assert.IsTrue(EditorSceneManager.SaveScene(marker.scene, path));
            yield return new EnterPlayMode();
        }

        [UnityTest]
        public IEnumerator ReloadRestoresSavedSceneWithoutAnyGameScripts()
        {
            Assert.IsFalse(TestStateRegistry.Global.HasOutcomeProvider);
            var provider = new SceneReloadStateProvider(new UnitySceneReloadEnvironment());
            var registry = new TestStateRegistry();
            registry.RegisterFallback(provider);
            var coordinator = new TestStateOperationCoordinator(registry);
            GameObject.Find("Scene reload marker").transform.position = Vector3.zero;
            var capture = coordinator.Capture(10);
            yield return Finish(provider, coordinator, capture);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, capture.Status, capture.Error);
            Assert.AreEqual(new Vector3(1, 2, 3), GameObject.Find("Scene reload marker").transform.position);
            GameObject.Find("Scene reload marker").transform.position = new Vector3(8, 9, 10);
            var replay = coordinator.Restore(capture.Anchor, 10);
            yield return Finish(provider, coordinator, replay);
            Assert.AreEqual(TestStateOperationStatus.Succeeded, replay.Status, replay.Error);
            Assert.AreEqual(new Vector3(1, 2, 3), GameObject.Find("Scene reload marker").transform.position);
        }

        static IEnumerator Finish(SceneReloadStateProvider provider,
            TestStateOperationCoordinator coordinator, TestStateOperation operation)
        {
            var last = Time.realtimeSinceStartup;
            while (!operation.IsFinished)
            {
                yield return null;
                provider.Tick();
                var now = Time.realtimeSinceStartup;
                coordinator.Tick(now - last);
                last = now;
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            var path = SessionState.GetString(AssetKey, "");
            if (!string.IsNullOrEmpty(path))
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(path);
                SessionState.EraseString(AssetKey);
            }
        }
    }
}
