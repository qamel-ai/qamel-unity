using System;
using System.Text;
using QamelCapture.TestAuthoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QamelCapture.Editor.TestAuthoring
{
    internal interface ISceneReloadEnvironment
    {
        string ScenePath { get; }
        int Frame { get; }
        string BlockedReason { get; }
        bool IsLoading { get; }
        void Reload(string path);
    }

    // A scene recipe, not a snapshot: static state, persistent objects, saved
    // progress and external services are deliberately not claimed as restored.
    internal sealed class SceneReloadStateProvider : IQamelTestStateProvider
    {
        internal const string Id = "qamel.unity.scene_reload";
        internal const string Limitation = "Recording restarts the current scene. Saved progress, " +
            "persistent objects and static game state are not reset. This is not a mid-game snapshot. " +
            "The result needs human review until an automatic check is configured.";
        readonly ISceneReloadEnvironment _environment;
        Action _complete;
        Action<string> _fail;
        Func<bool> _cancelled;
        string _path;
        int _loadedFrame = -1;

        public SceneReloadStateProvider(ISceneReloadEnvironment environment)
        {
            _environment = environment;
        }

        public string ProviderId => Id;
        public int StateFormatVersion => 1;

        public void CaptureState(TestStateCaptureContext context)
        {
            var path = _environment.ScenePath;
            Begin(path, () => context.IsCancellationRequested,
                () => context.Succeed("Reload scene: " + path, Encoding.UTF8.GetBytes(path)),
                error => context.Fail(error));
        }

        public void RestoreState(TestStateRestoreContext context)
        {
            string path;
            try { path = new UTF8Encoding(false, true).GetString(context.GetPayloadCopy()); }
            catch (DecoderFallbackException)
            {
                context.Fail("The scene recipe is not valid UTF-8.");
                return;
            }
            Begin(path, () => context.IsCancellationRequested,
                () => context.ReadyForReplay(), error => context.Fail(error));
        }

        void Begin(string path, Func<bool> cancelled, Action complete, Action<string> fail)
        {
            if (cancelled()) return;
            if (_complete != null)
            {
                fail("Wait for the previous scene reload to finish.");
                return;
            }
            if (!TestLabLaunch.IsProjectScenePath(path) || path != _environment.ScenePath)
            {
                fail("Open the saved scene before recording or replaying this Test.");
                return;
            }
            if (!string.IsNullOrEmpty(_environment.BlockedReason))
            {
                fail(_environment.BlockedReason);
                return;
            }
            _path = path;
            _complete = complete;
            _fail = fail;
            _cancelled = cancelled;
            _loadedFrame = -1;
            try { _environment.Reload(path); }
            catch (Exception)
            {
                Reset();
                fail("Unity could not reload the saved scene.");
            }
        }

        public void Tick()
        {
            if (_complete == null || _environment.IsLoading) return;
            // Unity cannot cancel a scene load already in flight. Keep the
            // operation occupied until it finishes, but suppress late callbacks.
            if (_cancelled()) { Reset(); return; }
            if (_loadedFrame < 0) { _loadedFrame = _environment.Frame; return; }
            if (_environment.Frame - _loadedFrame < 2) return;
            var complete = _complete;
            var fail = _fail;
            bool matches = _environment.ScenePath == _path &&
                           string.IsNullOrEmpty(_environment.BlockedReason);
            Reset();
            if (matches) complete();
            else fail("The game changed scenes during setup. This flow needs a different starting-state integration.");
        }

        void Reset()
        {
            _complete = null;
            _fail = null;
            _cancelled = null;
            _path = null;
        }
    }

    internal sealed class UnitySceneReloadEnvironment : ISceneReloadEnvironment
    {
        AsyncOperation _load;
        public string ScenePath => SceneManager.GetActiveScene().path;
        public int Frame => Time.frameCount;
        public bool IsLoading => _load != null && !_load.isDone;
        public string BlockedReason
        {
            get
            {
                if (!EditorApplication.isPlaying) return "Enter Play Mode to record.";
                if (SceneManager.sceneCount != 1) return "Scene reload currently supports one loaded scene only.";
                var scene = SceneManager.GetActiveScene();
                if (!TestLabLaunch.IsProjectScenePath(scene.path) ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null)
                    return "Open a saved scene before recording.";
                if (scene.isDirty) return "Save scene changes in Edit Mode before recording.";
                return null;
            }
        }
        public void Reload(string path)
        {
            _load = EditorSceneManager.LoadSceneAsyncInPlayMode(path,
                new LoadSceneParameters(LoadSceneMode.Single));
            if (_load == null) throw new InvalidOperationException("Scene load did not start.");
        }
    }
}
