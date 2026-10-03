# ScreenEase workflow coverage

Run from the repository root after building the Debug test projects:

```powershell
npx e2e run tests/e2e/screenease.e2e.ts --output .e2e/tools/screenease
```

The suite uses the e2e Windows tools target to execute real Avalonia surface commands and runtime commands. Every group checks TRX counters: a missing case, skipped case, failed case or absent report fails the suite. No source-text assertions participate in these groups.

| e2e group | Executed regressions | User flows |
| --- | ---: | --- |
| Surface workflows | 12 | Day/night editing; switch and retain drafts; undo selected draft; save; duplicate with unique IDs; failed save retains draft; manual apply; toggle; reminder start/pause/resume/reset; reminder/schedule input validation; returned schedule effect; transition limits; overlay opacity/color limits and normalized response; legacy import; reminder presets; tools navigation; refresh; diagnostics; shortcut settings |
| Runtime timer/effects | 7 | Persist timer across module recreation; pause/resume/reset persistence; logical effect recreation; disable; manual effect preserves saved profile; schedule day/night values; immediate scheduled apply |
| Persistence/migration | 7 | New-install modes; original settings migration; concurrent writes; first-run import; generated-ID migration; corrupt-file backup recovery; full legacy INI import |
| Driver/hotkeys/overlay | 8 | Gamma mathematics; eight hotkey settings; cleanup after applied/partially applied effects; hardware reset warnings; hotkey actions; overlay normalize/apply/toggle/cancelled cleanup; partial settings preserve values and update affected hotkeys |

The surface regressions live in `tools/screenease/tests/PersonalUxWorkflowTests.cs` and `PersonalUxDraftTests.cs`. Runtime regressions come from the existing `RuntimeAcceptanceTests.ScreenEaseParity.Tests.cs` suite. Displays and overlays use recording drivers; state stores use temporary directories. These tests leave the user's physical brightness, color temperature and screen power intact.

## Bugs reproduced and fixed

1. When the reminder was already enabled, pressing Start after editing its durations skipped configuration persistence. The new regression requires Save to complete before Start and checks the edited durations. Start now always saves the enabled configuration first.
2. A failed save changed the UI's reminder-enabled flag before persistence succeeded. The regression requires the disabled state to survive and forbids starting the timer after failure. The flag now changes after successful persistence.

The pre-fix e2e run produced 3 passing runtime groups and a failed surface group containing those 2 failed regressions out of 12. After the fix and a fresh Debug build, e2e 0.16.0 completed all 4 groups successfully (34 executed regressions, no skips). The final report is `.e2e/tools/screenease/report.json`.

## Remaining physical/UI acceptance

The upstream e2e engines target browser/mobile UI and do not provide an Avalonia Windows desktop driver. This suite verifies native surface commands and module behavior. Pointer/keyboard routing, rendering, physical DDC/gamma behavior, multi-monitor topology changes, global shortcut registration, native overlay visibility, real timer notifications and service restart/reconnect still require desktop/device acceptance. Physical screen changes must be performed in an explicitly authorized device session. A passing workflow suite does not certify those hardware/UI paths.
