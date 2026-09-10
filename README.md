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

The clock runs on **game ticks** (60 per second), the same measure the rift timer itself counts.
That is deliberate: it is the one clock a changed game speed cannot skew, so the numbers stay
comparable from run to run. Switch `UseGameTime` to `false` for real elapsed time instead.

## Where it sits

The block anchors to the top-left corner of the minimap element and drops by a number of text
lines you can tune, so it lands just under whatever tracker text is already drawn there.

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
| Session: best 8.4s    |  <-
|   // worst 41.2s      |
+-----------------------+
   minimap area
```

The drop is expressed as a **number of text lines, measured live** rather than as a fraction of the
minimap, so the block keeps its place when you resize the game window. That detail matters: the
text drawn over the minimap is sized to fit the minimap width but stops shrinking at 13.5 pixels,
an absolute floor, so it takes a larger share of the minimap on a small window than on a large one.
A fixed fraction drifts into it as soon as the window changes size.

### The font follows the window too

The block **re-derives its own font size the way the tracker text derives its own**, so the two
match at any resolution.

That text is not drawn at a fixed size. It grows until it fills the minimap width, and stops early
only once a line reaches `MinLineHeight` pixels **and** the size reaches `MaxFontSize`. Both halves
of that cap matter: TurboHUD font sizes scale with the window, so at 800x600 a size of `MaxFontSize`
gives lines well under the floor, the cap is never met, and the text keeps growing past
`MaxFontSize` until it is. Drawing at a fixed `MaxFontSize` therefore looks right at 1080p and
visibly too small at 800x600.

The search is re-run from scratch on a resize and incrementally when the text changes length, so it
costs nothing per frame. `AutoFontSize = false` turns it off.

`Anchor = Custom` puts the block anywhere on screen, by ratio.

**Anything already docked below that text has to move down by three lines** to make room, through
whatever setting places it.

## Install

1. Copy the `GuiSquare` folder into your TurboHUD `plugins` directory. The plugin is
   self-contained — no other files, no textures.

2. **If your HUD pack ships a plugin manager that disables everything it does not know about**, add
   one line to that manager's enable list:

   ```csharp
   "Turbo.Plugins.GuiSquare.GreaterRiftBossTimerPlugin"
   ```

3. Restart TurboHUD. If the block overlaps the text above it or floats too low, set
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
| `DebugEnabled` | `true` | On-screen diagnostic panel. On by default while the plugin is still being validated |

Set `AutoFontSize = false` to hand the fonts back to yourself: `TextFont`, and `RunningFont` for
the current line while the fight is still on.

## How the detection works

The guardian is picked out of the monster list by `ActorRarity.Boss`, with the actor code
containing `_Boss_` as a fallback. Illusions, summoned adds and Orlash's breath minion — which
carries the boss code without being the boss — are filtered out. Once a guardian is found, the
plugin locks onto its `AcdId` and stays on that actor for the rest of the fight, so a clone can
never take its place.

The kill is read from three signals, each a fallback for the one above:

| Signal | Role |
| --- | --- |
| The actor reports dead | `IsAlive` false, health at `0`, or the animation state at `Dead`. Timestamped on the spot. |
| The actor vanished | After `VanishGraceMs`, a kill is recorded **only** if the rift has actually moved on to its reward step (`NephalemRift_337492`, quest step 34), and it is timestamped at the last tick the guardian was seen, not at the end of the grace. |
| The rift moved on | Last resort. The rift only reaches its reward step once the guardian is down, so reaching it with no kill on the books means the actor list never showed the fight at all. The sample is kept anyway. |

The rift-completion condition on the second signal is the whole point of it: a hero dying and
respawning far from the fight also makes the guardian drop off the actor list. Without that check
it would invent a kill; with it, the run is dropped and no sample is recorded.

### When the clock starts

On the moment the guardian **becomes attackable**, not on its spawn and not on the first hit.

A rift guardian spends its first moment on screen playing a spawn animation, untargetable and
immune, and nothing anyone does shortens it. Counting it would add the same constant to every
sample and drown the differences the average exists to show. Starting on the first point of damage
has the opposite flaw: it hides the time the hero spends closing in or setting up. `StartOn`
switches to either of those if you want them.

Attackability is read from `Untargetable`, `Invulnerable` and the `Spawn` animation state —
deliberately not `IMonster.Attackable`, which also folds in `IsOnScreen` and would therefore answer
"no" every time the camera loses the guardian mid-fight. **Damage taken overrides all three**: a
guardian losing health is attackable whatever its flags claim, which keeps one whose flags never
clear from reporting a fight of no duration.

### Anchors, and why they slide

Collections are not guaranteed once per frame, which is what makes the third kill signal necessary
and also shapes every timestamp here. The clock prefers the moment the guardian became attackable,
then the moment the progress bar filled.

Both of those **slide forward** and stop on their own, rather than being latched on the transition
— the attackable anchor advances while the guardian still cannot be hit, the rift anchor while the
bar is still filling. Latching means catching one exact reading on one exact collection, which is
the very thing that cannot be relied on here; a value that slides cannot be missed. In a Greater
Rift the guardian spawns where the bar filled, right next to the hero, so neither anchor charges
the fight for a walk.

### Being in a rift is sticky

Entering a Greater Rift is detected from `SpecialArea` (or `InGreaterRift`). Leaving it is detected
from **being back in town**, never from `SpecialArea` again.

That is not a detail. `Hud.Game.SpecialArea` drops back to `None` the moment the rift completes —
verified in game, standing at the gem upgrade with the rift at 100% and its reward step already
active, while `SpecialArea` read `None`. Re-deriving "am I in a rift" from it on every collection
turns the guardian's death into a race between the collection that sees the corpse and the flip,
and loses roughly one rift in four, with no pattern.

The rest is bookkeeping. Entering a Greater Rift blanks the current counter; town leaves the last
result on screen; a lingering corpse on the actor list is ignored because the kill has already been
registered.

### Diagnostic panel

Setting `DebugEnabled = true` prints the raw state on screen — which condition the tracker is
sitting on, the guardian it locked onto with its health, the special area, rift percentage and
reward step, the clock in use, the numbers behind every line, and one entry per rift that has
ended -- which is what turns "one rift counted nothing" from a guess into a reading.

## Requirements

TurboHUD with the `Turbo.Plugins` API used here (`IInGameTopPainter`, `IAfterCollectHandler`,
`INewAreaHandler`). No external dependencies.

## License

[MIT](LICENSE) — use it, change it, ship it in your own pack, just keep the notice.
