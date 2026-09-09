# Greater Rift Boss Timer

A TurboHUD plugin for Diablo III. Two lines on your HUD: how long the guardian of the rift you are
in took to die, and the average over every guardian killed this session.

---

## What it does

- **Line 1 — current rift.** Ticks live from the guardian's first point of damage, freezes on the
  final value the moment it dies, and stays on screen until the next Greater Rift starts.
- **Line 2 — session average.** The mean of every counted kill, with the sample count behind it.

```
Bosskill: 12.4s
Bossavg: 15.1s x23
```

The clock runs on **game ticks** (60 per second), the same measure the rift timer itself counts.
That is deliberate: it is the one clock a changed game speed cannot skew, so the numbers stay
comparable from run to run. Switch `UseGameTime` to `false` for real elapsed time instead.

## Where it sits

The two lines anchor to the top-left corner of the minimap element and drop by a number of text
lines you can tune, so they land just under whatever tracker text is already drawn there.

```
+-----------------------+
| Status:   Live Session|
| Duration: 00:17:22    |
| Rifts:    12R 4.10R/H |  tracker text already on the minimap
| Greater:  12R 4.10R/H |
| Nephalem: 0R 0.00R/H  |
|                       |
| Bosskill: 12.4s       |  <- this plugin
| Bossavg: 15.1s x23    |  <-
+-----------------------+
   minimap area
```

The drop is expressed as a **number of text lines, measured live** rather than as a fraction of the
minimap, so the block keeps its place when you resize the game window. That detail matters: the
text drawn over the minimap is sized to fit the minimap width but stops shrinking at 13.5 pixels,
an absolute floor, so it takes a larger share of the minimap on a small window than on a large one.
A fixed fraction drifts into it as soon as the window changes size.

`Anchor = Custom` puts the block anywhere on screen, by ratio.

**Anything already docked below that text has to move down by two lines.** If you also run the
Follower Alive Status plugin, its `RbhPanelLineCount` goes from `8` to `10`.

## Install

1. Copy the `GuiSquare` folder into your TurboHUD `plugins` directory. The plugin is
   self-contained — no other files, no textures.

2. **If your HUD pack ships a plugin manager that disables everything it does not know about**, add
   one line to its enable list. On the RosbotHelper pack that is `Enable_Plugins` in
   `plugins/RosbotHelper/Config/Manager_Config.cs`:

   ```csharp
   "Turbo.Plugins.GuiSquare.GreaterRiftBossTimerPlugin"
   ```

3. Restart TurboHUD. If the two lines overlap the text above them or float too low, set
   `PanelLineCount` to the number of lines actually drawn over your minimap, plus one.

## Configuration

Rename `GuiSquare/GreaterRiftBossTimerCustomizer.txt` to `.cs` to change any setting without
editing the plugin itself. Managed packs must whitelist the customizer too:
`"Turbo.Plugins.GuiSquare.GreaterRiftBossTimerCustomizer"`.

| Option | Default | What it controls |
| --- | --- | --- |
| `Anchor` | `BelowMinimapText` | `BelowMinimapText` or `Custom` |
| `PanelLineCount` | `8` | Drop below the minimap top edge, in text lines. Resize-proof |
| `MinLineHeight` | `13.5` | Absolute floor of one of those lines, in pixels |
| `OffsetX` / `OffsetY` | `0` / `0` | Fine tuning, as a ratio of screen height |
| `LineSpacing` | `0` | Extra air between the two lines, as a fraction of a line height |
| `CurrentLabel` / `AverageLabel` | `Bosskill: ` / `Bossavg: ` | Line labels |
| `ShowSampleCount` | `true` | ` x12` behind the average |
| `ShowBestAndWorst` | `false` | ` (best 8.4s / worst 41.2s)` behind the average |
| `ShowGuardianName` | `false` | Guardian's name on the current line |
| `HideUntilFirstBoss` | `false` | Draw nothing until the first guardian is engaged |
| `HideOnMapModes` / `HideInTown` | `true` / `false` | When to stay out of the way |
| `StartOn` | `FirstDamage` | `FirstDamage` (the kill itself) or `GuardianSpawn` (kill plus the walk over) |
| `UseGameTime` | `true` | Game ticks, or the wall clock |
| `FirstDamageThreshold` | `0.999` | Health fraction that starts the clock |
| `MinimumValidMilliseconds` | `0` | Kills below this are shown but left out of the average |
| `VanishGraceMs` | `900` | Wait before ruling on a guardian that left the actor list |
| `ResetStatsOnNewGame` | `false` | Session total, or per game |
| `DebugEnabled` | `false` | On-screen diagnostic panel |

Both fonts are plain TurboHUD fonts and can be replaced: `TextFont`, and `RunningFont` for the
current line while the fight is still on.

## How the detection works

The guardian is picked out of the monster list by `ActorRarity.Boss`, with the actor code
containing `_Boss_` as a fallback. Illusions, summoned adds and Orlash's breath minion — which
carries the boss code without being the boss — are filtered out. Once a guardian is found, the
plugin locks onto its `AcdId` and stays on that actor for the rest of the fight, so a clone can
never take its place.

The kill is read from two independent signals:

| Signal | Role |
| --- | --- |
| The actor reports dead | Primary. `IsAlive` false, health at `0`, or the animation state at `Dead`. Timestamped on the spot. |
| The actor vanished | Fallback. After `VanishGraceMs`, a kill is recorded **only** if the rift has actually moved on to its reward step (`NephalemRift_337492`, quest step 34), and it is timestamped at the last tick the guardian was seen, not at the end of the grace. |

That second condition is the whole point of the fallback: a hero dying and respawning far from the
fight also makes the guardian drop off the actor list. Without the rift-completion check, that
would invent a kill. With it, the run is dropped and no sample is recorded.

The rest is bookkeeping. Entering a Greater Rift blanks the current counter; leaving it stops
tracking but leaves the last result on screen; a lingering corpse on the actor list is ignored
because the kill has already been registered.

### Diagnostic panel

Setting `DebugEnabled = true` prints the raw state on screen — which condition the tracker is
sitting on, the guardian it locked onto with its health, the special area, rift percentage and
reward step, the clock in use, and the numbers behind both lines.

## Requirements

TurboHUD with the `Turbo.Plugins` API used here (`IInGameTopPainter`, `IAfterCollectHandler`,
`INewAreaHandler`). No external dependencies.

## License

[MIT](LICENSE) — use it, change it, ship it in your own pack, just keep the notice.
