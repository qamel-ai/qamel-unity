using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QamelCapture
{
    /// <summary>
    /// Builds bundles in the format defined by the Qamel capture wire format, entirely in
    /// memory (Qamel never writes gameplay data to the player's disk). Safe to run
    /// on a background thread.
    /// </summary>
    internal static class ReportBundler
    {
        public const string SchemaVersion = "1";

        public static string FrameEntryName(CapturedFrame frame)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "frames/f_{0:D6}_{1:D8}.jpg", frame.Index, (long)Math.Round(frame.T * 1000));
        }

        /// <summary>Builds the zip (manifest, logs.jsonl, frames/) and returns its bytes.</summary>
        public static byte[] BuildBundle(string manifestJson, List<string> eventLines, List<CapturedFrame> frames)
        {
            using (var memory = new MemoryStream(EstimateSize(eventLines, frames)))
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteTextEntry(zip, "manifest.json", manifestJson, CompressionLevel.Optimal);

                    var logs = new StringBuilder(eventLines.Count * 96);
                    foreach (var line in eventLines) logs.Append(line).Append('\n');
                    WriteTextEntry(zip, "logs.jsonl", logs.ToString(), CompressionLevel.Optimal);

                    foreach (var frame in frames)
                    {
                        // JPEGs do not deflate; store them uncompressed for speed.
                        var entry = zip.CreateEntry(FrameEntryName(frame), CompressionLevel.NoCompression);
                        using (var stream = entry.Open())
                        {
                            stream.Write(frame.Jpg, 0, frame.Jpg.Length);
                        }
                    }
                }
                return memory.ToArray();
            }
        }

        static int EstimateSize(List<string> eventLines, List<CapturedFrame> frames)
        {
            long size = 4096 + eventLines.Count * 96L;
            foreach (var frame in frames) size += frame.Jpg.Length + 128;
            return (int)Math.Min(size, int.MaxValue / 2);
        }

        static void WriteTextEntry(ZipArchive zip, string name, string content, CompressionLevel level)
        {
            var entry = zip.CreateEntry(name, level);
            using (var stream = entry.Open())
            {
                var bytes = Encoding.UTF8.GetBytes(content ?? "");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
