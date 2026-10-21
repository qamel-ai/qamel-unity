using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QamelCapture.Editor
{
    /// <summary>
    /// Installs newer releases automatically while idle, or on explicit request.
    ///
    /// How it does that depends on how the package was installed: a git install is
    /// asked for the release tag, a branch install for the same branch again, and a
    /// registry install (OpenUPM) for the exact version. Local and embedded installs
    /// are left alone entirely. Those are checkouts someone is working in,
    /// including this monorepo's own test project.
    /// </summary>
    internal static class QamelPackageUpdater
    {
        const string PackageName = "com.qamel.unity";

        static AddRequest _request;
        static string _requestedTarget;

        public static bool AutomaticUpdatesEnabled
        {
            get => EditorPrefs.GetBool(QamelUpdateCheck.ProjectKey("AutomaticUpdates"), true);
            set => EditorPrefs.SetBool(QamelUpdateCheck.ProjectKey("AutomaticUpdates"), value);
        }

        public static string LastError => SessionState.GetString(QamelUpdateCheck.ProjectKey("UpdateError"), "");
        public static string LastResult => SessionState.GetString(QamelUpdateCheck.ProjectKey("UpdateResult"), "");

        public static bool EditorIsIdle
        {
            get
            {
                if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode ||
                    EditorApplication.isCompiling || EditorApplication.isUpdating ||
                    BuildPipeline.isBuildingPlayer || IsUpdating) return false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).isDirty) return false;
                return true;
            }
        }

        public static string AutomaticUpdateReason(UnityEditor.PackageManager.PackageInfo info)
        {
            string reason = UnsupportedReason(info);
            if (reason != null) return reason;
            if (info.source != PackageSource.Git)
                return "Use Update to latest for this registry install.";
            return QamelUpdateTargets.SupportsAutomaticGitUpdate(GitUrlOf(info), RequestedRevisionOf(info))
                ? null
                : "Automatic updates leave custom repositories, branches and commit pins unchanged. Use Update to latest.";
        }

        public static void TryAutomaticUpdate(string latestVersion)
        {
            if (!AutomaticUpdatesEnabled || !EditorIsIdle || !QamelUpdateCheck.ReleaseVerified ||
                latestVersion != QamelUpdateCheck.LatestVersion ||
                AutomaticUpdateReason(Installed) != null ||
                !QamelVersion.TryParse(latestVersion, out var parsed) || parsed.PreRelease ||
                !QamelVersion.IsNewer(latestVersion, QamelUpdateCheck.InstalledVersion)) return;
            string attempt = EditorPrefs.GetString(QamelUpdateCheck.ProjectKey("AutoAttemptVersion"), "");
            if (!AutomaticRetryIsDue(latestVersion, attempt,
                    EditorPrefs.GetString(QamelUpdateCheck.ProjectKey("AutoAttemptTime"), ""), DateTime.UtcNow)) return;
            EditorPrefs.SetString(QamelUpdateCheck.ProjectKey("AutoAttemptVersion"), latestVersion);
            EditorPrefs.SetString(QamelUpdateCheck.ProjectKey("AutoAttemptTime"),
                System.DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            StartUpdate(TargetFor(Installed, latestVersion), latestVersion);
        }

        internal static bool AutomaticRetryIsDue(string latestVersion, string attemptedVersion,
            string attemptedAt, DateTime now) =>
            attemptedVersion != latestVersion || QamelUpdateCheck.IntervalElapsed(attemptedAt, now, 1d / 6);

        public static void ObserveCompletedUpdate()
        {
            string pending = SessionState.GetString(QamelUpdateCheck.ProjectKey("PendingVersion"), "");
            if (pending.Length == 0 || IsUpdating) return;
            if (QamelVersion.TryCompare(QamelUpdateCheck.InstalledVersion, pending, out int comparison) && comparison >= 0)
            {
                SessionState.EraseString(QamelUpdateCheck.ProjectKey("PendingVersion"));
                SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateResult"),
                    "Qamel updated to " + QamelUpdateCheck.InstalledVersion + ".");
            }
            else if (EditorApplication.timeSinceStartup >
                     SessionState.GetFloat(QamelUpdateCheck.ProjectKey("UpdateStarted"), 0) + 60 &&
                     !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                SessionState.EraseString(QamelUpdateCheck.ProjectKey("PendingVersion"));
                SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateError"),
                    "The update did not complete. Try Update to latest or use Package Manager.");
            }
        }

        public static bool IsUpdating => _request != null;

        /// <summary>The resolved install, or null when Unity does not know the package.</summary>
        public static UnityEditor.PackageManager.PackageInfo Installed =>
            UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(QamelSettings).Assembly);

        /// <summary>
        /// Why a one-click update is not possible here, or null when it is. Shown
        /// verbatim in Project Settings, so it has to say what to do instead.
        /// </summary>
        public static string UnsupportedReason(UnityEditor.PackageManager.PackageInfo info)
        {
            if (info == null)
            {
                return "Unity does not report Qamel as a package here, so it cannot be updated " +
                       "automatically. Reinstall it through Package Manager to get updates.";
            }

            switch (info.source)
            {
                case PackageSource.Git:
                case PackageSource.Registry:
                    return null;
                case PackageSource.Embedded:
                case PackageSource.Local:
                case PackageSource.LocalTarball:
                    return "Qamel is installed from a local path (" + info.source + "), so this " +
                           "project uses your own copy. Update that checkout instead.";
                default:
                    return "Qamel was installed from " + info.source + ", which Qamel cannot " +
                           "update for you. Use Package Manager.";
            }
        }

        /// <summary>What would be passed to Package Manager, or null when unsupported.</summary>
        public static string TargetFor(UnityEditor.PackageManager.PackageInfo info, string latestVersion)
        {
            if (info == null) return null;

            if (info.source == PackageSource.Git)
            {
                return QamelUpdateTargets.ForGitInstall(
                    GitUrlOf(info), RequestedRevisionOf(info), latestVersion);
            }

            if (info.source == PackageSource.Registry)
            {
                return QamelUpdateTargets.ForRegistryInstall(PackageName, latestVersion);
            }

            return null;
        }

        /// <summary>
        /// The `#fragment` the package was installed with, or empty for none.
        /// <c>git.revision</c> is the requested revision (<c>git.hash</c> is the
        /// commit it resolved to, which is not what an update should re-request).
        /// </summary>
        static string RequestedRevisionOf(UnityEditor.PackageManager.PackageInfo info)
        {
            if (info.git != null && !string.IsNullOrEmpty(info.git.revision)) return info.git.revision;
            return QamelUpdateTargets.RevisionOf(info.packageId);
        }

        /// <summary>
        /// A git packageId reads `com.qamel.unity@https://host/repo.git#rev`, so the
        /// URL comes from there; <c>repository.url</c> describes where the package
        /// says it is hosted, which is not necessarily where this copy came from.
        /// </summary>
        static string GitUrlOf(UnityEditor.PackageManager.PackageInfo info)
        {
            string packageId = info.packageId;
            if (!string.IsNullOrEmpty(packageId))
            {
                int at = packageId.IndexOf('@');
                if (at >= 0 && at + 1 < packageId.Length) return packageId.Substring(at + 1);
            }
            return info.repository != null ? info.repository.url : null;
        }

        /// <summary>
        /// Manual updates retain the confirmation. The automatic path uses the
        /// project's update preference and waits for an idle editor.
        /// </summary>
        public static void UpdateToLatest(string latestVersion)
        {
            if (IsUpdating) return;
            if (!EditorIsIdle)
            {
                SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateError"),
                    "Save scene changes and leave Play Mode before updating. Unity must finish importing or building first.");
                return;
            }

            var info = Installed;
            string reason = UnsupportedReason(info);
            if (reason != null)
            {
                EditorUtility.DisplayDialog("Qamel cannot update itself here", reason, "OK");
                return;
            }

            string target = TargetFor(info, latestVersion);
            if (string.IsNullOrEmpty(target))
            {
                EditorUtility.DisplayDialog("Qamel update",
                    "Could not work out what to install for version " + latestVersion +
                    ". Update through Package Manager instead.", "OK");
                return;
            }

            string pinNote = info.source == PackageSource.Git &&
                             QamelUpdateTargets.PinsAPreviouslyUnpinnedInstall(RequestedRevisionOf(info))
                ? "\n\nThis pins the install to that release tag, which is how Package Manager " +
                  "is asked for a specific version. Later releases update the same way."
                : "";

            bool confirmed = EditorUtility.DisplayDialog(
                "Update Qamel to " + latestVersion + "?",
                "Unity will resolve and reimport the package:\n\n" + target + pinNote +
                "\n\nSave your scene first. This cannot be undone from here; the previous " +
                "version can be reinstalled the same way.",
                "Update", "Cancel");
            if (!confirmed) return;

            StartUpdate(target, latestVersion);
        }

        static void StartUpdate(string target, string latestVersion)
        {
            if (string.IsNullOrEmpty(target)) return;
            SessionState.EraseString(QamelUpdateCheck.ProjectKey("UpdateError"));
            SessionState.EraseString(QamelUpdateCheck.ProjectKey("UpdateResult"));
            SessionState.SetString(QamelUpdateCheck.ProjectKey("PendingVersion"), latestVersion);
            SessionState.SetFloat(QamelUpdateCheck.ProjectKey("UpdateStarted"), (float)EditorApplication.timeSinceStartup);
            try
            {
                _requestedTarget = target;
                _request = Client.Add(target);
                EditorApplication.update += Poll;
                QLog.Notice("Updating Qamel to " + latestVersion + ".");
                EditorWindow.focusedWindow?.Repaint();
            }
            catch (Exception error)
            {
                _request = null;
                SessionState.EraseString(QamelUpdateCheck.ProjectKey("PendingVersion"));
                SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateError"), "Updating Qamel failed: " + error.Message);
            }
        }

        static void Poll()
        {
            if (_request == null)
            {
                EditorApplication.update -= Poll;
                return;
            }
            if (!_request.IsCompleted) return;

            EditorApplication.update -= Poll;
            var request = _request;
            string target = _requestedTarget;
            _request = null;
            _requestedTarget = null;
            EditorWindow.focusedWindow?.Repaint();

            if (request.Status == StatusCode.Success)
            {
                var result = request.Result;
                string successMessage = "Qamel updated to " + (result != null ? result.version : "the requested version") + ".";
                SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateResult"), successMessage);
                SessionState.EraseString(QamelUpdateCheck.ProjectKey("PendingVersion"));
                QLog.Notice(successMessage);
                return;
            }

            string message = request.Error != null ? request.Error.message : "unknown error";
            SessionState.EraseString(QamelUpdateCheck.ProjectKey("PendingVersion"));
            SessionState.SetString(QamelUpdateCheck.ProjectKey("UpdateError"),
                "Updating Qamel failed: " + message + ". Try Update to latest or use Package Manager.");
            QLog.Warn("Updating Qamel failed (" + target + "): " + message +
                      "\nUpdate it through Package Manager instead.");
        }
    }
}
