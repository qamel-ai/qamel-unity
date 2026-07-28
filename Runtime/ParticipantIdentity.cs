using System;
using UnityEngine;

namespace QamelCapture
{
    internal struct IdentitySnapshot
    {
        public string InstallationId;
        public string ExternalPlayerId;
        public string ParticipantKind;
    }

    internal interface IInstallationIdStore
    {
        string Load();
        void Save(string value);
    }

    internal sealed class PlayerPrefsInstallationIdStore : IInstallationIdStore
    {
        const string Key = "qamel.installation_id.v1";

        public string Load()
        {
            return PlayerPrefs.GetString(Key, "");
        }

        public void Save(string value)
        {
            PlayerPrefs.SetString(Key, value);
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// Holds participant identity independently from device diagnostics. The
    /// installation UUID is the anonymous fallback; a studio player id takes
    /// precedence server-side without permanently merging a shared installation.
    /// </summary>
    internal sealed class ParticipantIdentity
    {
        public const int MaxExternalPlayerIdLength = 128;

        readonly object _gate = new object();
        string _externalPlayerId = "";
        QamelSettings.ParticipantKind _participantKind;

        public ParticipantIdentity(QamelSettings settings)
            : this(settings, new PlayerPrefsInstallationIdStore())
        {
        }

        internal ParticipantIdentity(QamelSettings settings, IInstallationIdStore store)
        {
            InstallationId = ResolveInstallationId(store);
            _participantKind = Application.isEditor
                ? QamelSettings.ParticipantKind.Developer
                : settings != null
                    ? settings.defaultParticipantKind
                    : QamelSettings.ParticipantKind.Unknown;
        }

        public string InstallationId { get; }

        public IdentitySnapshot Snapshot()
        {
            lock (_gate)
            {
                return new IdentitySnapshot
                {
                    InstallationId = InstallationId,
                    ExternalPlayerId = _externalPlayerId,
                    ParticipantKind = KindValue(_participantKind),
                };
            }
        }

        public bool TrySetPlayer(string playerId, out string error)
        {
            string normalized = (playerId ?? "").Trim();
            if (!IsValidExternalPlayerId(normalized))
            {
                error = "Player identity must be 1-" + MaxExternalPlayerIdLength +
                        " printable ASCII characters without spaces or '@'.";
                return false;
            }

            lock (_gate) _externalPlayerId = normalized;
            error = null;
            return true;
        }

        public void ClearPlayer()
        {
            lock (_gate) _externalPlayerId = "";
        }

        public void SetParticipantKind(QamelSettings.ParticipantKind kind)
        {
            lock (_gate) _participantKind = kind;
        }

        internal static bool IsValidExternalPlayerId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxExternalPlayerIdLength)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x21 || c > 0x7e || c == '@') return false;
            }
            return true;
        }

        internal static string KindValue(QamelSettings.ParticipantKind kind)
        {
            switch (kind)
            {
                case QamelSettings.ParticipantKind.Developer: return "developer";
                case QamelSettings.ParticipantKind.Playtester: return "playtester";
                default: return "unknown";
            }
        }

        static string ResolveInstallationId(IInstallationIdStore store)
        {
            string existing = "";
            try { existing = store.Load(); }
            catch { /* preferences may be unavailable; use an in-memory id */ }

            if (Guid.TryParseExact(existing, "N", out _))
                return existing.ToLowerInvariant();

            string generated = Guid.NewGuid().ToString("N");
            try { store.Save(generated); }
            catch { /* the id remains stable for this process */ }
            return generated;
        }
    }
}
