using System.Runtime.CompilerServices;

// The editor tooling shares the runtime's internals (logging, ingest routes,
// version comparison) rather than widening the public API for its own use.
[assembly: InternalsVisibleTo("Qamel.Capture.Editor")]
[assembly: InternalsVisibleTo("Qamel.Capture.EditorTests")]
[assembly: InternalsVisibleTo("Qamel.Capture.RuntimeTests")]
[assembly: InternalsVisibleTo("Qamel.Capture.Benchmark")]
