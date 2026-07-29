# Performance Benchmark

Measures Qamel's frame-time cost in your own project, on your own hardware.
Compares three modes on a synthetic GPU+CPU-loaded scene:

1. **baseline**: Qamel off
2. **buffer**: rolling in-memory buffer, footage sent only when a report is
   filed (the default)
3. **streaming**: continuous capture upload

across a matrix of capture resolutions (`frameWidths`) and rates
(`captureFpsValues`).

## Run it

1. Import the sample via Package Manager > Qamel > Samples.
2. Create an empty scene, add an empty GameObject, attach `QamelBenchmark`.
3. Press play. Defaults run 25 scenarios x 25 s, roughly 10 minutes; results
   appear in the console as they finish.
4. A summary table is logged at the end and a CSV is written to
   `persistentDataPath` (the path is printed).

For numbers you can rely on:

- **Use a standalone build**, not the editor: editor overhead adds noise and
  compresses the differences.
- Run the matrix three times and compare medians.
- Test at the resolutions you care about. Capture reads the screen, so window
  size affects the downscale cost.
- Keep the machine idle and off battery-saver or thermal-limited states.

## Tuning the synthetic load

The scene is generated: `cubeCount` rotating lit cubes plus
`cpuLoadMsPerFrame` of busy CPU work. Raise `cubeCount` until the baseline
frame rate roughly matches your game. Overhead percentages only mean something
when the baseline is loaded like a real game rather than running at 2000 fps on
an empty scene.

## Streaming and network

Streaming scenarios build chunk bundles and discard them by default, which
measures the full on-device cost (capture, GPU readback, JPEG encode, zip)
without needing a server. Set `endpoint` and `apiKey` on the component to
include real uploads; upload cost is mostly async I/O and shows up in bandwidth
rather than frame time.

## Reading the results

- `mean ms` / `p95 ms` / `p99 ms`: frame time. p99 catches hitches the mean
  hides, such as encode or GC spikes.
- `overhead`: change in mean frame time against the baseline scenario.
- `GC0`: gen-0 collections in the measure window. Expect roughly the baseline
  count; the capture path is designed to be allocation-steady.
- `capture/s`: KB/s of encoded data produced. In buffer mode that is what sits
  in RAM; in streaming mode it is what gets uploaded, so use it to pick
  settings that fit your testers' upload capacity.
