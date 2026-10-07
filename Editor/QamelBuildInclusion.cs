using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Excludes non-opted-in settings from Resources without copying credentials.
    /// A path-only journal restores moved assets after success, failure or restart.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class QamelBuildInclusion : IPostprocessBuildWithReport
    {
        const string JournalPath = "Library/Qamel/build-inclusion.json";
        const string ExcludedFolder = "Assets/Qamel/Editor/BuildExcluded";
        [Serializable] internal sealed class Move { public string from; public string to; }
        [Serializable] sealed class Journal { public List<Move> moves = new List<Move>(); }
        public int callbackOrder => int.MaxValue;
        static QamelBuildInclusion() { EditorApplication.update += RecoverWhenIdle; }
        static void RecoverWhenIdle()
        {
            if (!BuildPipeline.isBuildingPlayer && File.Exists(JournalPath)) Restore();
        }
        public void OnPostprocessBuild(BuildReport report) { Restore(); }

        internal static void ExcludeSettings(IEnumerable<QamelSettings> settings)
        {
            Restore();
            var paths = settings.Where(s => s != null && !s.includeInPlayerBuild)
                .Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
            if (paths.Length == 0) return;
            var scenePaths = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var resources = AssetDatabase.GetAllAssetPaths().Where(p => p.Contains("/Resources/") && !p.Contains("/Editor/") && !paths.Contains(p)).ToArray();
            var preloaded = PlayerSettings.GetPreloadedAssets().Where(a => a != null).Select(AssetDatabase.GetAssetPath);
            var dependencies = new HashSet<string>(AssetDatabase.GetDependencies(scenePaths.Concat(resources).Concat(preloaded).ToArray(), true));
            foreach (string path in paths)
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || dependencies.Contains(path))
                    throw new BuildFailedException("[Qamel] A settings asset excluded from player builds is directly referenced by a build scene, Resources or preloaded asset, or is outside Assets. Remove that reference or explicitly include capture in Project Settings > Qamel.");
            }
            EnsureFolder(ExcludedFolder);
            var journal = new Journal();
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            try
            {
                foreach (string path in paths)
                {
                    string target = ExcludedFolder + "/" + AssetDatabase.AssetPathToGUID(path) + ".asset";
                    // Write intent before moving; recovery also handles a failed move.
                    journal.moves.Add(new Move { from = path, to = target });
                    File.WriteAllText(JournalPath, JsonUtility.ToJson(journal));
                    string error = AssetDatabase.MoveAsset(path, target);
                    if (!string.IsNullOrEmpty(error)) throw new BuildFailedException("[Qamel] Could not exclude capture settings: " + error);
                }
                AssetDatabase.Refresh();
            }
            catch { Restore(); throw; }
        }
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        internal static void Restore()
        {
            if (!File.Exists(JournalPath)) return;
            var journal = JsonUtility.FromJson<Journal>(File.ReadAllText(JournalPath));
            var failures = new List<Move>();
            foreach (var move in journal.moves)
            {
                if (AssetDatabase.LoadMainAssetAtPath(move.to) == null) continue;
                string error = AssetDatabase.MoveAsset(move.to, move.from);
                if (!string.IsNullOrEmpty(error)) failures.Add(move);
            }
            if (failures.Count > 0)
            {
                journal.moves = failures;
                File.WriteAllText(JournalPath, JsonUtility.ToJson(journal));
                throw new BuildFailedException("[Qamel] Could not restore capture settings after building. Resolve conflicting assets and reopen the project; recovery paths are in Library/Qamel/build-inclusion.json.");
            }
            File.Delete(JournalPath);
            if (Directory.Exists(ExcludedFolder) && !Directory.EnumerateFileSystemEntries(ExcludedFolder).Any())
                AssetDatabase.DeleteAsset(ExcludedFolder);
        }
    }
}
