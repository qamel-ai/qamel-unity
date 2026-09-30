using System.Runtime.CompilerServices;

// The editor tooling shares the runtime's internals (logging, ingest routes,
// version comparison) rather than widening the public API for its own use.
[assembly: InternalsVisibleTo("Qamel.Capture.Editor")]
[assembly: InternalsVisibleTo("Qamel.Capture.EditorTests")]
[assembly: InternalsVisibleTo("Qamel.Capture.RuntimeTests")]
[assembly: InternalsVisibleTo("Qamel.Capture.Benchmark")]
[assembly: InternalsVisibleTo("Qamel.TestAuthoring.Spike.Tests")]
[assembly: InternalsVisibleTo("Qamel.TestAuthoring.ConnectedRunHarness")]
[assembly: InternalsVisibleTo("Qamel.Milestone5.FpsMicrogame.Tests")]
