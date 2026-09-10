# Greater Rift Boss Timer

A TurboHUD plugin for Diablo III. Three lines on your HUD: how long the guardian of the rift you
are in took to die, the average over every guardian killed this session, and the best and worst of
those.

---

## What it does

- **Line 1 — current rift.** Ticks live from the moment the guardian becomes attackable,
  freezes on the final value when it dies, and stays on screen until the next Greater Rift starts.
- **Line 2 — session average.** The mean of every counted kill, with the sample count behind it.
- **Line 3 — session range.** The fastest and slowest of those kills.

```
Bosskill: 12.4s
Bossavg: 15.1s x23
Session: best 8.4s // worst 41.2s
```

The block sits under whatever tracker text is already drawn over your minimap, and keeps its place
when you resize the game window.

## Install

1. Copy the `GuiSquare` folder into your TurboHUD `plugins` directory. The plugin is
   self-contained — no other files, no textures.

2. Restart TurboHUD. If the block overlaps the text on your minimap or floats too low, set
   `PanelLineCount` to the number of lines actually drawn there, plus one.

## Configuration

Rename `GuiSquare/GreaterRiftBossTimerCustomizer.txt` to `.cs` to change any setting without
editing the plugin itself.

| Option | Default | What it controls |
| --- | --- | --- |
| `Anchor` | `BelowMinimapText` | `BelowMinimapText` or `Custom` |
| `PanelLineCount` | `8` | Drop below the minimap top edge, in text lines. Resize-proof |
| `MinLineHeight` | `13.5` | Absolute floor of one of those lines, in pixels |
| `AutoFontSize` | `true` | Re-derive the font size from the window, the way the tracker text does |
| `MaxFontSize` | `8.0` | Size the auto-sizing stops at, once lines clear the floor |
| `OffsetX` / `OffsetY` | `0` / `0` | Fine tuning, as a ratio of screen height |
| `LineSpacing` | `0` | Extra air between lines, as a fraction of a line height |
| `CurrentLabel` / `AverageLabel` / `SessionLabel` | `Bosskill: ` / `Bossavg: ` / `Session: ` | Line labels |
| `ShowSampleCount` | `true` | ` x12` behind the average |
| `ShowSessionLine` | `true` | The third line, `best 8.4s // worst 41.2s` |
| `ShowGuardianName` | `false` | Guardian's name on the current line |
| `HideUntilFirstBoss` | `false` | Draw nothing until the first guardian is engaged |
| `HideOnMapModes` / `HideInTown` | `true` / `false` | When to stay out of the way |
| `StartOn` | `BecomesAttackable` | `BecomesAttackable`, `FirstDamage`, or `GuardianSpawn` |
| `UseGameTime` | `true` | Game ticks, or the wall clock |
| `FirstDamageThreshold` | `0.999` | Health fraction that counts as damaged |
| `MinimumValidMilliseconds` | `0` | Kills below this are shown but left out of the average |
| `VanishGraceMs` | `900` | Wait before ruling on a guardian that left the actor list |
| `ResetStatsOnNewGame` | `false` | Session total, or per game |
| `DebugEnabled` | `false` | On-screen diagnostic panel, one line per rift (see below) |

Set `AutoFontSize = false` to hand the fonts back to yourself: `TextFont`, and `RunningFont` for
the current line while the fight is still on.

## Diagnostic panel

Setting `DebugEnabled = true` prints the raw state on screen — which condition the tracker is
sitting on, the guardian it locked onto with its health and its attackability flags, the special
area, rift percentage and reward step, the clock and anchors in use, the numbers behind every
line, and one entry per rift that has ended.

## Requirements

TurboHUD with the `Turbo.Plugins` API used here (`IInGameTopPainter`, `IAfterCollectHandler`,
`INewAreaHandler`). No external dependencies.

## License

[MIT](LICENSE) — use it, change it, ship it in your own pack, just keep the notice.
