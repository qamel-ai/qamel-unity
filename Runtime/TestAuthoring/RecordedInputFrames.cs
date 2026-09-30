using System;
using System.Collections.Generic;
using System.IO;

namespace QamelCapture.TestAuthoring
{
    // Versioned artifact envelope. The native Input System trace remains intact;
    // simulation timing belongs alongside it, not in device event timestamps.
    internal static class RecordedInputFrames
    {
        const int Magic = 0x46494151; // QAIF
        const int Version = 1;
        const int MaximumFrames = 524288;

        internal struct Timing
        {
            public float DeltaTime;
            public float TimeScale;
            public float FixedDeltaTime;

            public void Validate()
            {
                if (!PositiveFinite(DeltaTime) || !PositiveFinite(TimeScale) ||
                    !PositiveFinite(FixedDeltaTime) || !PositiveFinite(DeltaTime / TimeScale))
                    throw new InvalidDataException("Recorded simulation timing must be positive and finite.");
            }

            static bool PositiveFinite(float value) => value > 0 &&
                !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static byte[] Write(byte[] nativeTrace, IReadOnlyList<Timing> frames)
        {
            if (frames.Count == 0 || frames.Count > MaximumFrames)
                throw new InvalidDataException("The recording has no complete gameplay frames or is too long.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(frames.Count);
                foreach (var frame in frames)
                {
                    frame.Validate();
                    writer.Write(frame.DeltaTime);
                    writer.Write(frame.TimeScale);
                    writer.Write(frame.FixedDeltaTime);
                }
                writer.Write(nativeTrace.Length);
                writer.Write(nativeTrace);
                return stream.ToArray();
            }
        }

        public static Timing[] Read(byte[] artifact, out byte[] nativeTrace)
        {
            using (var stream = new MemoryStream(artifact, false))
            using (var reader = new BinaryReader(stream))
            {
                if (artifact.Length < 4 || reader.ReadInt32() != Magic)
                {
                    nativeTrace = artifact;
                    return null; // Previously saved native traces remain readable.
                }
                if (reader.ReadInt32() != Version)
                    throw new InvalidDataException("Unsupported recorded input timing version.");
                var count = reader.ReadInt32();
                if (count <= 0 || count > MaximumFrames || (long)count * 12 > stream.Length - stream.Position - 4)
                    throw new InvalidDataException("Invalid recorded gameplay frame count.");
                var frames = new Timing[count];
                for (var index = 0; index < count; index++)
                {
                    frames[index] = new Timing
                    {
                        DeltaTime = reader.ReadSingle(),
                        TimeScale = reader.ReadSingle(),
                        FixedDeltaTime = reader.ReadSingle()
                    };
                    frames[index].Validate();
                }
                var length = reader.ReadInt32();
                if (length <= 0 || length != stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid native input trace length.");
                nativeTrace = reader.ReadBytes(length);
                return frames;
            }
        }
    }
}
