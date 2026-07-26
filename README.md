# Qamel for Unity

Qamel turns playtest sessions into structured bug reports. It records the last
minutes of gameplay in the background; when something goes wrong, one keypress
sends a report to your Qamel dashboard.

Each report contains:

- **Gameplay footage** of the moments leading up to the report
- **Logs** — console messages and exceptions with stack traces
- **Context** — scene, frame rate, memory, play time
- **Input** — the key and mouse actions that led there

Unhandled exceptions file a report on their own, so crashes arrive with their
context. No scene setup and no code required.

Requires Unity **2021.3 LTS or newer** on Windows, macOS or Linux. Works with
both the legacy Input Manager and the Input System package.

## Install

1. **Add the package.** In Unity: `Window > Package Manager > + > Add package
   from git URL...` and paste:

   ```
   https://github.com/qamel-ai/qamel-unity.git
   ```

   Unity records the exact commit in your `Packages/packages-lock.json`, so your
   version stays fixed until you update it.

2. **Add your API key.** Open `Edit > Project Settings > Qamel`, click
   *Create Qamel settings*, and paste the API key from your Qamel dashboard.

3. **Play.** Press **F8** whenever something looks wrong, describe it in a
   sentence, and hit *Send report*. It appears on your dashboard within seconds.

## Report from code

Optional, for richer reports:

```csharp
using QamelCapture;

Qamel.Log("boss fight started");                 // breadcrumb
Qamel.Event("level_loaded", "level_3");          // named event + value
Qamel.TriggerReport("fell through the floor");   // file a report
```

These are safe no-ops when Qamel is disabled, so you can leave them in.

## Settings

`Edit > Project Settings > Qamel`

| Setting | Default | |
| ------- | ------- | ----- |
| Capture Enabled | on | Master switch |
| Api Key | empty | Required; from your Qamel dashboard |
| Endpoint | `https://ingest.qamel.ai` | Change only if Qamel gave you another host |
| Upload Reports | on | Turn off to stop sending without disabling capture |
| Buffer Seconds | 120 | How much gameplay each report covers |
| Capture Fps / Frame Width / Jpeg Quality | 6 / 1280 / 60 | Footage quality; lower them to use less bandwidth |
| Frame Flip | Auto | Switch if footage arrives upside down |
| Capture Input / Mouse Position | on | Keyboard and mouse actions (never typed text) |
| Report Hotkey | F8 | |
| Auto Report On Exception | on | Report unhandled exceptions automatically |
| Continuous Streaming | off | Stream footage while playing instead of only on reports |
| Verbose Logging | off | Qamel's own console output |
| Send Plugin Diagnostics | on | Report Qamel's internal errors to Qamel; never gameplay data |

Captured data is held in memory and uploaded to Qamel. Nothing is written to
the player's device, so there is nothing to clean up afterwards.

## Verify your setup

1. Enter play mode. With *Verbose Logging* on, the console shows
   `[Qamel] Session … started`.
2. Press **F8**, send a report, and confirm it appears on your dashboard.
3. Do the same in a build — that is what your playtesters will run.

## Troubleshooting

**Nothing arrives.** Check the console for a `[Qamel]` warning. A missing API
key disables capture at startup; a rejected key disables uploads for the
session.

**Footage is upside down.** Set *Frame Flip* to the opposite value.

**No footage, but logs and input arrive.** The platform or graphics API does not
support async GPU readback, so Qamel skips frames rather than stalling your
game.

Keyboard and mouse are recorded; gamepad, touch and audio are not yet.

---

Questions or a key request: [qamel.ai](https://qamel.ai)
