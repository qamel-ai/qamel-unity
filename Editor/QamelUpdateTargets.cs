namespace QamelCapture.Editor
{
    /// <summary>
    /// Works out what to hand <c>UnityEditor.PackageManager.Client.Add</c> to move
    /// an install to a newer version.
    ///
    /// Git installs are moved to the release tag, even one that was installed
    /// unpinned. That is deliberate: a package resolved from a git URL is pinned
    /// by hash in the consumer's `packages-lock.json`, and Package Manager only
    /// re-resolves when the dependency *string* changes, so re-adding the same
    /// bare URL is not reliably an update. Asking for `#v&lt;version&gt;` always is.
    /// A branch install is the one case left alone, because a branch is a moving
    /// target by choice. Pure string logic, so it is directly testable.
    /// </summary>
    internal static class QamelUpdateTargets
    {
        /// <summary>
        /// Target for a git dependency. <paramref name="requestedRevision"/> is
        /// the `#fragment` the user installed with, if any.
        /// </summary>
        public static string ForGitInstall(string gitUrl, string requestedRevision, string latestVersion)
        {
            if (string.IsNullOrWhiteSpace(gitUrl)) return null;

            string url = StripRevision(gitUrl.Trim());
            string revision = (requestedRevision ?? "").Trim();

            // A branch is a deliberate moving target: re-adding the same branch
            // re-resolves it to the newest commit, which is the update.
            bool tracksBranch = revision.Length > 0 &&
                                !LooksLikeVersion(revision) &&
                                !LooksLikeCommitHash(revision);
            if (tracksBranch) return url + "#" + revision;

            if (string.IsNullOrWhiteSpace(latestVersion)) return null;
            return url + "#v" + latestVersion.Trim().TrimStart('v', 'V');
        }

        /// <summary>
        /// True when the update will pin an install that was not pinned before,
        /// which the confirmation dialog has to say out loud.
        /// </summary>
        public static bool PinsAPreviouslyUnpinnedInstall(string requestedRevision)
        {
            return string.IsNullOrWhiteSpace(requestedRevision);
        }

        /// <summary>Target for a registry install (OpenUPM or any scoped registry).</summary>
        public static string ForRegistryInstall(string packageName, string latestVersion)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return null;
            if (string.IsNullOrWhiteSpace(latestVersion)) return packageName.Trim();
            return packageName.Trim() + "@" + latestVersion.Trim().TrimStart('v', 'V');
        }

        /// <summary>
        /// The `#fragment` of a git URL, or empty. Unity reports the requested
        /// revision separately, but a packageId carries it inline.
        /// </summary>
        public static string RevisionOf(string gitUrl)
        {
            if (string.IsNullOrEmpty(gitUrl)) return "";
            int hash = gitUrl.IndexOf('#');
            return hash < 0 || hash + 1 >= gitUrl.Length ? "" : gitUrl.Substring(hash + 1).Trim();
        }

        /// <summary>The URL without its revision fragment; query strings are kept.</summary>
        public static string StripRevision(string gitUrl)
        {
            if (string.IsNullOrEmpty(gitUrl)) return gitUrl;
            int hash = gitUrl.IndexOf('#');
            return hash < 0 ? gitUrl : gitUrl.Substring(0, hash);
        }

        static bool LooksLikeVersion(string revision)
        {
            return QamelVersion.TryParse(revision, out _);
        }

        static bool LooksLikeCommitHash(string revision)
        {
            if (revision.Length < 7 || revision.Length > 40) return false;
            for (int i = 0; i < revision.Length; i++)
            {
                char c = revision[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
