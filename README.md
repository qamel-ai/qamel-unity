# Qamel for Unity

Qamel turns playtest sessions into structured bug reports. It records the last
minutes of gameplay in the background; when something goes wrong, one keypress
sends a report to Qamel.

Each report contains:

- **Gameplay footage** of the moments leading up to the report
- **Logs**: console messages and exceptions with stack traces
- **Context**: scene, frame rate, memory, play time
- **Input**: the key and mouse actions that led there

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
   *Create Qamel settings*, and paste the API key Qamel gave you.

3. **Play.** Press **F8** whenever something looks wrong, optionally describe it,
   and hit *Send report* (or **Shift+Enter**). It uploads in the background, and
   the console confirms it was sent.

## Report from code

Optional, for richer reports:

```csharp
using QamelCapture;

Qamel.Log("boss fight started");                 // breadcrumb
Qamel.Event("level_loaded", "level_3");          // named event + value
Qamel.TriggerReport("fell through the floor");   // file a report

// Optional: use your own opaque account id for cross-device grouping.
Qamel.SetPlayerIdentity("player_42");
Qamel.SetParticipantKind(QamelSettings.ParticipantKind.Playtester);
// Call on logout to return to anonymous per-installation grouping.
Qamel.ClearPlayerIdentity();
```

These are safe no-ops when Qamel is disabled, so you can leave them in.

**Using your own report UI.** Turn off *Use Built In Overlay* in the settings and
call `Qamel.TriggerReport(text)` from your own form or menu. Capture, bundling
and upload are unchanged; only the form is yours.

## While the report form is open

By default the game freezes: Qamel sets `Time.timeScale` to 0 and pauses audio
while the form is showing, then restores whatever the values were. Turn off
*Pause While Reporting* for anything networked, where pausing one client does
not pause the session. Capture is unaffected either way: footage timing uses
unscaled time and the session clock is a stopwatch.

Qamel **cannot swallow the keypress**: neither input backend lets a plugin
consume input before your game reads it, so the hotkey that opens the form, and
Escape when it closes it, still reach your own handlers. If your game opens a
pause menu on Escape, guard it:

```csharp
using QamelCapture;

void Update()
{
    if (Qamel.IsReportFormOpen) return;   // the tester is typing a report
    if (Input.GetKeyDown(KeyCode.Escape)) TogglePauseMenu();
}
```

To pause with your own pause manager instead, turn off *Pause While Reporting*
and hook the form's lifecycle (unsubscribe in `OnDestroy`):

```csharp
void OnEnable()
{
    Qamel.ReportFormOpened += PauseGame;
    Qamel.ReportFormClosed += ResumeGame;
}

void OnDisable()
{
    Qamel.ReportFormOpened -= PauseGame;
    Qamel.ReportFormClosed -= ResumeGame;
}
```

## Settings

`Edit > Project Settings > Qamel`

| Setting | Default | |
| ------- | ------- | ----- |
| Capture Enabled | on | Master switch |
| Api Key | empty | Required; the key Qamel gave you |
| Endpoint | `https://ingest.qamel.ai` | Change only if Qamel gave you another host |
| Upload Reports | on | Turn off to stop sending without disabling capture |
| Build Id | empty | Optional CI/release build identifier used for filtering |
| Default Participant Kind | Unknown | Audience of packaged builds; editor sessions are Developer |
| Buffer Seconds | 120 | How much gameplay each report covers |
| Capture Fps / Frame Width / Jpeg Quality | 6 / 1280 / 60 | Footage quality; lower them to use less bandwidth |
| Frame Flip | Auto | Switch if footage arrives upside down |
| Capture Input / Mouse Position | on | Keyboard and mouse actions (never typed text) |
| Report Hotkey | F8 | |
| Use Built In Overlay | on | Off = use your own UI, see above |
| Pause While Reporting | on | Freezes the game while the form is open; off for multiplayer |
| Auto Report On Exception | on | Report unhandled exceptions automatically |
| Continuous Streaming | off | Stream footage while playing instead of only on reports |
| Check For Updates | on | Daily editor-only version check, see below |
| Verbose Logging | off | Qamel's own console output |
| Send Plugin Diagnostics | on | Report Qamel's internal errors to Qamel; never gameplay data |

Captured gameplay data is held in memory and uploaded to Qamel. Qamel persists
only one random installation UUID in Unity PlayerPrefs so anonymous sessions
can be grouped; recordings, reports and upload queues are never written to the
player's device.

## Updating Qamel

Unity resolves a git package once and records that commit in your
`Packages/packages-lock.json`, so Package Manager never offers you an update.
Qamel therefore checks for one itself: `Edit > Project Settings > Qamel` shows
your installed version, and when a newer one exists you get a banner with
*Update to X*, *Release notes* and *Skip this version*, plus one line in the
console. *Update to X* asks Package Manager for that release's tag. Package
Manager only re-resolves a git package when the requested revision changes, so
naming the tag is what makes it an update. If you installed without a tag, the
first update pins you to one and later releases update the same way. A branch
install keeps tracking its branch. Unity reimports afterwards, so save your
scene first.

The check runs in the editor only, at most once a day, and can be turned off
with *Check For Updates*. It is a plain `GET` for a version number: no API key,
no identifiers, no project data. Use *Check for updates* on that page to ask
immediately.

To update by hand (which is what you need coming from a version that predates
this banner), add the git URL with the tag you want in Package Manager
(*+ > Install package from git URL*):

```
https://github.com/qamel-ai/qamel-unity.git#v<version>
```

Installing over an existing git install is enough; you do not have to remove it
first. Deleting the `com.qamel.unity` entry from `Packages/packages-lock.json`
also works if you track a branch and just want the newest commit.

## Verify your setup

1. Enter play mode. With *Verbose Logging* on, the console shows
   `[Qamel] Session … started`.
2. Press **F8** and send a report. The console confirms the upload, with no
   `[Qamel]` warnings.
3. Do the same in a build: that is what your playtesters will run.

## Troubleshooting

**Nothing arrives.** Check the console for a `[Qamel]` warning. A missing API
key disables capture at startup; a rejected key disables uploads for the
session.

**Footage is upside down.** Set *Frame Flip* to the opposite value.

**No footage, but logs and input arrive.** The platform or graphics API does not
support async GPU readback, so Qamel skips frames rather than stalling your
game.

Keyboard and mouse are recorded; gamepad, touch and audio are not yet.

## License

MIT, see [LICENSE.md](LICENSE.md). Using it needs a Qamel API key, which is
where reports are analysed and grouped.

---

Questions or a key request: [qamel.ai](https://qamel.ai)
