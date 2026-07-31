using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QamelCapture
{
    /// <summary>
    /// Uploads bundles to the Qamel ingest endpoints (see <see cref="IngestRoutes"/>
    /// and the Qamel capture wire format)
    /// from an in-memory queue, in two steps: POST the manifest JSON to the
    /// ingest API (which stores it and returns a pre-signed URL), then PUT the
    /// zip directly to object storage. The API never sees bundle bytes, so it
    /// can run on serverless hosts with small request-body limits. Nothing is
    /// ever written to the player's disk: Qamel data lives only in memory on
    /// the device and on the Qamel servers. Bundles that cannot be delivered
    /// after the retry budget are dropped with a warning.
    /// </summary>
    internal sealed class Uploader
    {
        const int MaxAttemptsPerBundle = 3;
        const int RequestTimeoutSeconds = 120;
        // Cap queued memory so a dead server cannot grow RAM: newest data wins.
        const int MaxQueuedBundles = 8;
        static readonly float[] RetryDelaysSeconds = { 5f, 20f, 60f };

        struct PendingUpload
        {
            public string ManifestJson;
            public byte[] ZipBytes;
            public string FileName;
            public bool IsChunk;
        }

        readonly QamelSettings _settings;
        readonly MonoBehaviour _host;
        readonly Queue<PendingUpload> _queue = new Queue<PendingUpload>();
        bool _draining;
        bool _authFailed;
        bool _retired;
        bool _warnedNotConfigured;

        public Uploader(QamelSettings settings, MonoBehaviour host)
        {
            _settings = settings;
            _host = host;
        }

        public bool CanUpload =>
            _settings.uploadReports &&
            !string.IsNullOrWhiteSpace(_settings.apiKey) &&
            !string.IsNullOrWhiteSpace(_settings.endpoint) &&
            !_authFailed &&
            !_retired;

        /// <summary>Queues a bundle for upload. Main thread only.</summary>
        public void Enqueue(string manifestJson, byte[] zipBytes, string fileName, bool isChunk)
        {
            if (!CanUpload)
            {
                WarnNotConfiguredOnce();
                return;
            }

            if (_queue.Count >= MaxQueuedBundles)
            {
                _queue.Dequeue();
                QLog.Warn("Upload queue is full; dropping the oldest pending bundle.");
            }
            _queue.Enqueue(new PendingUpload
            {
                ManifestJson = manifestJson,
                ZipBytes = zipBytes,
                FileName = fileName,
                IsChunk = isChunk,
            });

            if (!_draining) _host.StartCoroutine(DrainLoop());
        }

        void WarnNotConfiguredOnce()
        {
            if (_warnedNotConfigured) return;
            _warnedNotConfigured = true;
            if (_authFailed)
                QLog.Warn("Uploads are disabled for this session after an authentication failure; captured data is discarded.");
            else if (_retired)
                QLog.Warn("Uploads are disabled for this session because the server retired this plugin version; captured data is discarded.");
            else
                QLog.Warn("No API key / endpoint configured (Project Settings > Qamel); captured data is discarded. " +
                          "Qamel keeps data only in memory and on the Qamel servers.");
        }

        IEnumerator DrainLoop()
        {
            _draining = true;

            while (_queue.Count > 0)
            {
                var pending = _queue.Dequeue();
                bool uploaded = false;

                for (int attempt = 0;
                     attempt < MaxAttemptsPerBundle && !uploaded && !_authFailed && !_retired;
                     attempt++)
                {
                    if (attempt > 0)
                        yield return new WaitForSecondsRealtime(
                            RetryDelaysSeconds[Mathf.Min(attempt - 1, RetryDelaysSeconds.Length - 1)]);

                    // Step 1: register the manifest; the response carries a
                    // pre-signed URL for the bundle zip.
                    string bundleUploadUrl = null;
                    string url = IngestRoutes.Url(
                        _settings.endpoint,
                        pending.IsChunk ? IngestRoutes.ChunkPath : IngestRoutes.ReportPath);
                    using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                    {
                        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(pending.ManifestJson))
                        {
                            contentType = "application/json",
                        };
                        request.downloadHandler = new DownloadHandlerBuffer();
                        request.SetRequestHeader(IngestHeaders.Authorization, IngestHeaders.Bearer(_settings.apiKey));
                        request.SetRequestHeader(IngestHeaders.Plugin, IngestHeaders.PluginValue());
                        request.timeout = RequestTimeoutSeconds;

                        yield return request.SendWebRequest();

                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            string body = request.downloadHandler.text;
                            // The server can move this session to another host
                            // without a plugin update; applies from the next
                            // request on, including retries of this bundle.
                            IngestRoutes.TryAcceptHandoff(QamelJson.ExtractString(body, "ingestBase"));

                            bundleUploadUrl = QamelJson.ExtractString(body, "bundleUploadUrl");
                            if (string.IsNullOrEmpty(bundleUploadUrl))
                                QLog.Info("Manifest for " + pending.FileName + " accepted but no upload URL was returned.");
                        }
                        else if (request.responseCode == 401 || request.responseCode == 403)
                        {
                            _authFailed = true;
                            QLog.Warn("Upload rejected (" + request.responseCode +
                                      "). Check the API key in Project Settings > Qamel. Uploads are disabled for this session.");
                        }
                        else if (request.responseCode == 410)
                        {
                            // Endpoint retired: retrying can never succeed, and
                            // only a package update fixes it.
                            _retired = true;
                            string reason = QamelJson.ExtractString(request.downloadHandler.text, "error");
                            QLog.Warn("This Qamel Capture version is no longer accepted by the server" +
                                      (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")") +
                                      ". Update the com.qamel.unity package. Uploads are disabled for this session.");
                        }
                        else
                        {
                            QLog.Info("Upload attempt " + (attempt + 1) + " for " + pending.FileName +
                                      " failed at manifest registration (" + request.responseCode + " " + request.error + ").");
                        }
                    }

                    if (_authFailed || _retired || string.IsNullOrEmpty(bundleUploadUrl)) continue;

                    // Step 2: PUT the zip straight to object storage.
                    using (var put = UnityWebRequest.Put(bundleUploadUrl, pending.ZipBytes))
                    {
                        put.SetRequestHeader("Content-Type", "application/zip");
                        // The signed URL was created with upsert, so retried
                        // uploads may overwrite their own partial object.
                        put.SetRequestHeader("x-upsert", "true");
                        put.timeout = RequestTimeoutSeconds;

                        yield return put.SendWebRequest();

                        if (put.result == UnityWebRequest.Result.Success)
                        {
                            uploaded = true;
                            QLog.Info("Uploaded " + pending.FileName + " (" + (pending.ZipBytes.Length / 1024) + " KB).");
                        }
                        else
                        {
                            QLog.Info("Upload attempt " + (attempt + 1) + " for " + pending.FileName +
                                      " failed at bundle upload (" + put.responseCode + " " + put.error + ").");
                        }
                    }
                }

                if (!uploaded && !pending.IsChunk)
                    QLog.Warn("Report " + pending.FileName + " could not be delivered and was dropped.");

                if (_authFailed || _retired)
                {
                    _queue.Clear();
                    break;
                }
            }

            _draining = false;
        }
    }
}
