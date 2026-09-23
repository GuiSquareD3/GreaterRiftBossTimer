using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Turbo.Plugins.Default;

namespace Turbo.Plugins.GuiSquare
{
    /// <summary>
    /// Times how long the Greater Rift guardian takes to die, and keeps a running average
    /// over the whole session.
    ///
    /// Three lines are drawn:
    ///
    ///   1. CURRENT  - the guardian of the rift you are in. It ticks live during the
    ///                 fight, then freezes on the final value and stays on screen until
    ///                 the next Greater Rift starts.
    ///   2. AVERAGE  - the mean of every guardian killed since TurboHUD started, with the
    ///                 number of samples behind it.
    ///   3. SESSION  - the fastest and the slowest of those kills.
    ///
    /// The clock is driven by game ticks (60 per second), not by the wall clock, so the
    /// numbers stay comparable whatever the game speed is. Set UseGameTime to false to
    /// measure real elapsed time instead.
    ///
    /// The block sits at the minimap's top-left corner, pushed down by PanelLineCount
    /// text lines so it lands just under the tracker text that is already drawn there.
    /// </summary>

    public enum GrBossTimerStart
    {
        BecomesAttackable, // the clock starts when the guardian can first be hit
        FirstDamage,       // the clock starts when the guardian first loses health
        GuardianSpawn      // the clock starts the moment the guardian spawns
    }

    public enum GrBossTimerAnchor
    {
        BelowMinimapText,   // minimap top-left corner, PanelLineCount lines down
        Custom              // free placement through CustomX / CustomY (screen ratio 0..1)
    }

    public class GreaterRiftBossTimerPlugin : BasePlugin, IInGameTopPainter, IAfterCollectHandler, INewAreaHandler
    {
        // ---------------------------------------------------------------------
        // Placement
        // ---------------------------------------------------------------------

        public GrBossTimerAnchor Anchor { get; set; }

        /// <summary>
        /// How far below the minimap's top edge the block starts, counted in text lines.
        /// This is THE value to adjust if the block overlaps the text above it, or
        /// floats too low: count the lines already drawn over the minimap, and land right
        /// under the last one.
        ///
        /// The line height is measured live at the current window size, so the block
        /// keeps its place when the game window is resized.
        /// </summary>
        public int PanelLineCount { get; set; }

        /// <summary>
        /// Absolute floor, in pixels, for one line of the text drawn over the minimap.
        /// That text refuses to be smaller than this however small the window gets, and
        /// grows its font size past MaxFontSize to stay above it. Mirrors the same
        /// literal in the tracker text's own sizing code.
        /// </summary>
        public float MinLineHeight { get; set; }

        /// <summary>
        /// Size the block's font the way the tracker text above it sizes itself, so the
        /// two match at any window size. Set it to false to draw at whatever TextFont
        /// and RunningFont were built with, and take full control of them.
        /// </summary>
        public bool AutoFontSize { get; set; }

        /// <summary>
        /// Size the auto-sizing stops at once lines are at least MinLineHeight pixels
        /// tall. Mirrors the tracker text's own cap; below a certain window size that
        /// cap cannot be met and both grow past it together.
        /// </summary>
        public float MaxFontSize { get; set; }

        /// <summary>Free placement, as a screen ratio (0..1). Only used when Anchor == Custom.</summary>
        public float CustomX { get; set; }
        public float CustomY { get; set; }

        /// <summary>Fine tuning for either anchor, as a ratio of the screen height.</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        /// <summary>Extra spacing between lines, as a fraction of a line height.</summary>
        public float LineSpacing { get; set; }

        // ---------------------------------------------------------------------
        // What is written
        // ---------------------------------------------------------------------

        /// <summary>Label of the current-rift line.</summary>
        public string CurrentLabel { get; set; }

        /// <summary>Label of the session-average line.</summary>
        public string AverageLabel { get; set; }

        /// <summary>Text used on the current line while no guardian has been engaged yet.</summary>
        public string IdlePlaceholder { get; set; }

        /// <summary>Append the sample count to the average, as " x12".</summary>
        public bool ShowSampleCount { get; set; }

        /// <summary>Draw a third line with the fastest and slowest kill of the session.</summary>
        public bool ShowSessionLine { get; set; }

        /// <summary>Label of the session best-and-worst line.</summary>
        public string SessionLabel { get; set; }

        /// <summary>Append the guardian's name to the current line while the fight is on.</summary>
        public bool ShowGuardianName { get; set; }

        /// <summary>Hide the whole block until the first guardian of the session is engaged.</summary>
        public bool HideUntilFirstBoss { get; set; }

        /// <summary>Hide the whole block while the world map or the act map is open.</summary>
        public bool HideOnMapModes { get; set; }

        /// <summary>Hide the whole block while in town.</summary>
        public bool HideInTown { get; set; }

        // ---------------------------------------------------------------------
        // Measurement
        // ---------------------------------------------------------------------

        /// <summary>
        /// Where the clock starts. Default: the moment the guardian can first be hit.
        ///
        /// A rift guardian spends its first moment on screen playing a spawn animation,
        /// untargetable and immune, and nothing anyone does shortens it. Counting it
        /// would add a constant to every sample and drown the differences the average is
        /// there to show. Starting on the first point of damage has the opposite flaw:
        /// it hides the time the hero spends closing in or setting up.
        /// </summary>
        public GrBossTimerStart StartOn { get; set; }

        /// <summary>
        /// true  = measure in game ticks (60 per second), which is what the rift timer
        ///         itself counts, and the one measure a changed game speed cannot skew.
        /// false = measure real elapsed time on the wall clock.
        /// </summary>
        public bool UseGameTime { get; set; }

        /// <summary>
        /// Fraction of max health the guardian has to drop below to count as damaged.
        /// Slightly under 1 so that a rounding wobble on a full health bar does not
        /// start the clock on its own.
        ///
        /// Used by StartOn = FirstDamage, and as the backstop of BecomesAttackable: a
        /// guardian taking damage is attackable whatever its flags claim.
        /// </summary>
        public double FirstDamageThreshold { get; set; }

        /// <summary>
        /// How long a guardian stays unhittable after the progress bar fills, in game
        /// ticks. Not a guess: measured over 220 kills across 25 distinct guardians, and
        /// the gap between the bar filling and the flags clearing landed in 222..227
        /// ticks 215 of those times. It is a fixed game rule, not a per-guardian
        /// animation length.
        ///
        /// The flags remain the primary signal, because they self-adjust if the game
        /// ever changes this. This value only polices them: see the tolerance below.
        /// </summary>
        public int SpawnProtectionTicks { get; set; }

        /// <summary>
        /// How far the observed transition may sit from SpawnProtectionTicks before it
        /// is treated as no observation at all and replaced.
        ///
        /// Five of 220 kills fell outside, all for the same reason -- the transition was
        /// never visible -- and in both directions:
        ///
        ///   * the actor was first seen on the very tick it was created, before its
        ///     Untargetable flag was populated, so the gate opened immediately and the
        ///     clock ran 3.75s long;
        ///   * the actor was first seen after the protection had already lapsed, so the
        ///     clock started short by anything from 0.5s to 2.2s.
        ///
        /// 15 sits in a gap the data leaves wide open: over 220 kills the sound readings
        /// never strayed more than 3 ticks from SpawnProtectionTicks, and the nearest
        /// bad one was 31 out. Five times the observed jitter, half the closest fault.
        /// </summary>
        public int SpawnProtectionToleranceTicks { get; set; }

        /// <summary>
        /// Kills shorter than this many milliseconds are shown but left out of the
        /// average. 0 keeps every sample, which is the honest default: a one-shot
        /// guardian in a low rift really does die in no time.
        /// </summary>
        public double MinimumValidMilliseconds { get; set; }

        /// <summary>
        /// How long to wait, in real milliseconds, before ruling on a guardian that has
        /// vanished from the actor list. A kill is only recorded if the rift has actually
        /// moved on to its reward step; otherwise the run is dropped without a sample,
        /// which is what happens when the hero dies far away from the fight.
        /// </summary>
        public int VanishGraceMs { get; set; }

        /// <summary>Wipe the session statistics whenever a new game starts.</summary>
        public bool ResetStatsOnNewGame { get; set; }

        // ---------------------------------------------------------------------
        // Look
        // ---------------------------------------------------------------------

        /// <summary>Font of the block. Matches the tracker text drawn above it.</summary>
        public IFont TextFont { get; set; }

        /// <summary>Font used for the current line while the fight is still running.</summary>
        public IFont RunningFont { get; set; }

        /// <summary>
        /// Append one line per rift to a log file. Independent of the on-screen panel, so
        /// a long run can be recorded without the clutter, and read back afterwards --
        /// the on-screen history only keeps the last few.
        /// </summary>
        public bool LogToFile { get; set; }

        /// <summary>
        /// Log location, relative to the TurboHUD folder. Kept OUT of plugins/ on
        /// purpose: writing there trips the file watcher and forces a recompile.
        /// </summary>
        public string LogFileName { get; set; }

        /// <summary>Show the diagnostic panel. Turn it on once to validate in game.</summary>
        public bool DebugEnabled { get; set; }
        public IFont DebugFont { get; set; }
        public float DebugX { get; set; }
        public float DebugY { get; set; }

        // ---------------------------------------------------------------------
        // Exposed state (read-only in practice)
        // ---------------------------------------------------------------------

        /// <summary>Number of guardian kills counted into the average this session.</summary>
        public int KillCount { get; private set; }

        /// <summary>Sum of every counted kill, in milliseconds.</summary>
        public double TotalMilliseconds { get; private set; }

        /// <summary>Fastest and slowest counted kill of the session, in milliseconds.</summary>
        public double BestMilliseconds { get; private set; }
        public double WorstMilliseconds { get; private set; }

        /// <summary>
        /// How many of those kills the actor list never showed us, and the safety net had
        /// to recover from the rift reaching its reward step. Diagnostic only: a rising
        /// number means guardians are dying between two collections, which is expected at
        /// high game speed and does not cost a sample.
        /// </summary>
        public int RecoveredKillCount { get; private set; }

        /// <summary>Average kill time of the session, in milliseconds. 0 when there is no sample.</summary>
        public double AverageMilliseconds
        {
            get { return KillCount > 0 ? TotalMilliseconds / KillCount : 0d; }
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        /// <summary>
        /// Stamped into the log header, so a log can be tied to the code that wrote it.
        /// Bump it whenever the detection changes: a run was already misread once as a
        /// test of a build that was not actually loaded.
        /// </summary>
        private const string BuildTag = "2026-09-23-leave-confirm";

        private const double GameTicksPerSecond = 60.0d;

        // Guardian being tracked in the current rift.
        private uint _guardianAcd;
        private string _guardianName;
        private int _spawnTick;         // first sighting; diagnostics only, never an anchor
        private int _engageTick;         // int.MinValue while the fight has not started
        private DateTime _engageUtc;
        private int _fullHealthTick;     // last moment the guardian was seen untouched
        private DateTime _fullHealthUtc;
        private int _attackableTick;     // last moment the guardian could NOT be hit yet
        private DateTime _attackableUtc;
        private bool _attackableSeen;
        private int _lastSeenTick;
        private DateTime _lastSeenUtc;
        private double _maxHealth;
        private bool _seenAlive;         // the guardian was confirmed alive at least once
        private DateTime _vanishedUtc;   // DateTime.MinValue while the guardian is on the list

        // Result of the current rift.
        private double _currentMs;       // negative while there is nothing to show
        private bool _running;
        private bool _killed;

        // Rift bookkeeping.
        // Sticky: set on entering a Greater Rift, cleared only by town, a new game, or
        // leaving the game. Never re-derived from SpecialArea, which goes back to None
        // as soon as the rift completes.
        private bool _wasInGreaterRift;
        private int _riftFullTick;       // when the progress bar filled, ie. the guardian spawned
        private DateTime _riftFullUtc;
        private bool _riftWasFull;
        private int _inProgressAfterFull;
        private string _killPath;

        /// <summary>Collections of a running rift needed to call it a new one, after a full one.</summary>
        private const int NewRiftConfirmCollections = 3;

        /// <summary>Collections of "not in game" or "in town" needed before the rift is torn down.</summary>
        private const int LeaveConfirmCollections = 5;

        // Outcome of the last few rifts, newest last. Diagnostic only: this is what turns
        // "one rift counted nothing" from a guess into a reading.
        private const int HistoryLength = 8;
        private readonly List<string> _history = new List<string>();
        private int _riftsSeen;

        // Auto-sizing, mirroring how the tracker text above sizes itself.
        private const float FontSizeFloor = 1.0f;
        private const float FontSizeCeiling = 40.0f;
        private const float FontSizeStep = 0.1f;
        private IFont _autoTextFont;
        private IFont _autoRunningFont;
        private IFont _probeFont;
        private float _probeSize;
        private float _fontSize;
        private float _lastMinimapWidth;
        private int _lastBlockLength = -1;

        private float _dbgLineHeight;
        private string _dbgState = "-";
        private string _dbgGuardian = "-";
        private string _dbgAttackable = "-";
        private string _attackableGate = "-";   // what opened the gate, latched at the transition
        private string _spawnAttrsAtFirstSight = "-";
        private int _gateOpenedTick;
        private bool _logHeaderWritten;
        private bool _anchorCorrected;
        private int _leavingConfirm;
        private string _closeReason;

        /// <summary>How long after the gate opens a returning blocker still counts as a flicker.</summary>
        private const int BlockerEchoWindowTicks = 120;   // 2 seconds of game time

        public GreaterRiftBossTimerPlugin()
        {
            Enabled = true;
            Order = 30001; // right after the follower status icon

            Anchor = GrBossTimerAnchor.BelowMinimapText;
            PanelLineCount = 8;      // tracker lines already drawn over the minimap
            MinLineHeight = 13.5f;   // absolute floor of one of those lines, in pixels
            AutoFontSize = true;
            MaxFontSize = 8.0f;      // the tracker text's own cap
            CustomX = 0.02f;
            CustomY = 0.30f;
            OffsetX = 0f;
            OffsetY = 0f;
            LineSpacing = 0f;

            CurrentLabel = "Bosskill: ";
            AverageLabel = "Bossavg: ";
            IdlePlaceholder = "-";
            ShowSampleCount = true;
            ShowSessionLine = true;
            SessionLabel = "Session: ";
            ShowGuardianName = false;
            HideUntilFirstBoss = false;
            HideOnMapModes = true;
            HideInTown = false;

            StartOn = GrBossTimerStart.BecomesAttackable;
            UseGameTime = true;
            FirstDamageThreshold = 0.999d;
            SpawnProtectionTicks = 225;
            SpawnProtectionToleranceTicks = 15;
            MinimumValidMilliseconds = 0d;
            VanishGraceMs = 900;
            ResetStatsOnNewGame = false;

            // The log carries one line per rift, including the gate that released the
            // clock. That is the reading that tells a spawn phase this plugin recognises
            // from one it does not, and it survives a restart -- unlike the on-screen
            // history, which keeps only the last few.
            LogToFile = true;
            LogFileName = "GuiSquare/gr_boss_timer_log.txt";

            // Off: the log says the same thing without the clutter. Turn it on only to
            // watch the flags move live during a spawn.
            DebugEnabled = false;
            DebugX = 0.35f;
            DebugY = 0.20f;

            ResetRift();
            ResetStats();
        }

        public override void Load(IController hud)
        {
            base.Load(hud);

            // Fall-backs, used as-is only when AutoFontSize is off. With it on -- the
            // default -- the size is re-derived from the window, and these are replaced.
            TextFont = Hud.Render.CreateFont("Arial", MaxFontSize, 255, 255, 255, 255, true, false, false);
            RunningFont = Hud.Render.CreateFont("Arial", MaxFontSize, 255, 255, 225, 130, true, false, false);
            DebugFont = Hud.Render.CreateFont("consolas", 8.5f, 255, 255, 255, 160, false, false, 200, 0, 0, 0, true);
        }

        // =====================================================================
        // Detection (collection phase, no rendering here)
        // =====================================================================

        public void OnNewArea(bool newGame, ISnoArea area)
        {
            if (newGame)
            {
                _wasInGreaterRift = false;
                ResetRift();
                if (ResetStatsOnNewGame)
                    ResetStats();
            }
        }

        public void AfterCollect()
        {
            if (!Enabled)
                return;

            // Leaving the rift is confirmed over several collections, never on one.
            //
            // A single collection reading "not in game" or "in town" mid-fight -- a lag
            // spike, a frame where the game state is between two things -- used to tear
            // the rift down on the spot. That cost two entries in the log instead of one:
            // a phantom NO SAMPLE for the rift being abandoned, then the real kill logged
            // as a fresh rift with no anchor at all, so the cross-check had nothing to
            // police it with. Seen five times in a single session.
            //
            // A genuine exit holds for far longer than a few collections, so nothing real
            // is missed by waiting.
            var leaving = !Hud.Game.IsInGame ? "not in game"
                : Hud.Game.IsInTown ? "in town"
                : null;

            if (leaving != null)
            {
                _dbgState = leaving + " (" + _leavingConfirm.ToString(CultureInfo.InvariantCulture)
                    + "/" + LeaveConfirmCollections.ToString(CultureInfo.InvariantCulture) + ")";

                if (++_leavingConfirm < LeaveConfirmCollections)
                    return; // not convinced yet: leave the rift state alone

                if (_wasInGreaterRift)
                    _closeReason = leaving;

                _wasInGreaterRift = false;
                _guardianAcd = 0u;
                _vanishedUtc = DateTime.MinValue;
                _running = false;

                // Only a real exit from the game clears the rift outright. Town keeps the
                // last result on screen until the next rift starts.
                if (!Hud.Game.IsInGame)
                    ResetRift();

                return;
            }

            _leavingConfirm = 0;

            // Being inside a rift is STICKY: entered on a positive signal, left only on a
            // confirmed town, new game, or exit.
            //
            // It cannot be re-tested every collection against Hud.Game.SpecialArea, which
            // is what the first versions did and what cost whole rifts. SpecialArea drops
            // back to None the moment the rift completes -- confirmed in game, standing at
            // the gem upgrade with the rift at 100% and its reward step already active,
            // while SpecialArea read None. Re-testing it turned the guardian's death into
            // a race: whichever landed first between the collection that sees the corpse
            // and the flip decided whether the rift counted at all. That is exactly the
            // one-rift-in-four miss, and every check downstream was powerless, because
            // the early return fired before any of them.
            var now = Hud.Time.Now;
            var tick = Hud.Game.CurrentGameTick;
            var rewardStep = IsRiftRewardStep();
            var inProgress = !rewardStep && Hud.Game.RiftPercentage < 100d;

            if (!_wasInGreaterRift)
            {
                // Only entering needs a positive signal, and while the rift is running
                // SpecialArea gives one.
                if (Hud.Game.SpecialArea != SpecialArea.GreaterRift && !Hud.Game.Me.InGreaterRift)
                {
                    _dbgState = "not in a greater rift";
                    return;
                }

                // A new Greater Rift just started: blank the current counter.
                _wasInGreaterRift = true;
                ResetRift();
            }
            else if (_riftWasFull && inProgress)
            {
                // The rift is running again after having been finished, so this is
                // another one. Leaving the special area is the usual signal for that,
                // but it must not be the only one: if it ever fails to clear between two
                // rifts, the kill flag from the previous guardian stays set and silently
                // blanks the whole next rift, safety net included. Confirmed over a few
                // collections so a frame of missing quest data cannot pass for a rift.
                if (++_inProgressAfterFull >= NewRiftConfirmCollections)
                    ResetRift();
            }
            else
            {
                _inProgressAfterFull = 0;
            }

            // Anchor of last resort: the moment the progress bar filled, which is when
            // the guardian spawns, and in a Greater Rift it spawns where the bar filled,
            // right next to the hero. So there is no walk to charge to the fight.
            //
            // It slides forward while the rift is still running instead of being latched
            // the first time the bar is caught at 100. Catching that exact reading takes
            // a collection landing inside the window where it holds, and collections are
            // not guaranteed per frame -- the whole reason a guardian can be missed in
            // the first place. Stopping one collection short of the spawn cannot be
            // missed, and is close enough.
            if (!_riftWasFull)
            {
                if (inProgress)
                {
                    _riftFullTick = tick;
                    _riftFullUtc = now;
                }
                else if (_riftFullTick != 0)
                {
                    _riftWasFull = true;
                }

                // A rift found already finished is one we joined after the fact -- a hot
                // recompile while standing at the gem upgrade, typically. It never gets
                // an anchor, which is what keeps the safety net below from inventing a
                // kill of no duration out of it.
            }

            var guardian = FindGuardian();

            if (guardian != null)
                TrackGuardian(guardian, tick, now);
            else
                TrackMissingGuardian(now, rewardStep);

            // Safety net. The rift only reaches its reward step once the guardian is
            // down, so a rift that has moved on without a registered kill means the
            // actor list never showed us the fight: a guardian nuked between two
            // collections, or one the matcher failed to recognise. The sample is still
            // worth having, measured from whatever anchor we do have.
            if (!_killed && _riftFullTick != 0 && rewardStep)
            {
                RecoveredKillCount++;
                RegisterKill(tick, now, "safety net");
                _dbgState = "kill recovered (rift reached its reward step)";
            }
        }

        private void TrackGuardian(IMonster guardian, int tick, DateTime now)
        {
            _vanishedUtc = DateTime.MinValue;

            if (_killed)
            {
                // The corpse can linger on the actor list. Nothing left to measure.
                _dbgState = "killed, corpse still listed";
                return;
            }

            if (_guardianAcd != guardian.AcdId)
            {
                _guardianAcd = guardian.AcdId;
                _guardianName = guardian.SnoMonster != null ? guardian.SnoMonster.NameEnglish : guardian.SnoActor.Code;
                _spawnTick = tick;
                _engageTick = int.MinValue;
                _fullHealthTick = 0;
                _fullHealthUtc = DateTime.MinValue;
                // Seeded on this first sighting, so a guardian already attackable by the
                // time it reaches the actor list still has an anchor.
                _attackableTick = tick;
                _attackableUtc = now;
                _attackableSeen = false;
                _spawnAttrsAtFirstSight = SpawnPhaseAttributes(guardian);
                _maxHealth = 0d;
                _seenAlive = false;
                _currentMs = -1d;
            }

            if (_maxHealth <= 0d && guardian.MaxHealth > 0d)
                _maxHealth = guardian.MaxHealth;

            var damaged = _maxHealth > 0d
                && guardian.CurHealth > 0d
                && guardian.CurHealth < _maxHealth * FirstDamageThreshold;

            // When the guardian became hittable. It spends its first moment on screen
            // playing a spawn animation that no damage can interrupt, so this, not the
            // spawn and not the first hit, is when the fight really starts.
            //
            // Slides while it cannot be hit and stops on its own, for the same reason
            // the rift anchor slides: a transition caught on one exact collection can be
            // missed, a sliding value cannot.
            if (!_attackableSeen)
            {
                var blocker = AttackabilityBlocker(guardian);

                if (blocker == null || damaged)
                {
                    // Damage only lands on something that can be hit, so it settles the
                    // question whatever the flags say. That backstop matters: without
                    // it, a guardian whose flags never clear would slide its own anchor
                    // all the way to its death and report a fight of no duration.
                    _attackableSeen = true;
                    _gateOpenedTick = tick;

                    // What opened the gate, recorded here because it cannot be read back
                    // afterwards. "flags@0t" means the guardian never looked unhittable
                    // at all -- which is the fingerprint of a spawn phase this test does
                    // not recognise, and the clock starting too early.
                    _attackableGate = (blocker == null ? "flags" : "damage")
                        + "@" + (tick - _spawnTick).ToString(CultureInfo.InvariantCulture) + "t"
                        + " state=" + guardian.AnimationState
                        + " anim=" + guardian.Animation
                        + " attrs=" + _spawnAttrsAtFirstSight + "->" + SpawnPhaseAttributes(guardian);
                }
                else
                {
                    _attackableTick = tick;
                    _attackableUtc = now;
                    _attackableGate = "waiting on " + blocker;
                }
            }
            else if (_gateOpenedTick != 0 && tick - _gateOpenedTick <= BlockerEchoWindowTicks)
            {
                // Did a blocker come back right after the gate opened? Pure observation,
                // it changes nothing.
                //
                // It separates the two ways this can go wrong, which need opposite
                // fixes. A flag that never fires means the spawn phase is invisible to
                // us, and the answer is a better signal. A flag that flickers -- clear
                // for one collection in the middle of the entrance -- means the signal
                // is fine and the reading is not, and the answer is to require it to
                // hold for a few collections before believing it, exactly as the new
                // rift check already does.
                //
                // An intermittent symptom on the same guardian points at the second.
                var echo = AttackabilityBlocker(guardian);
                if (echo != null)
                {
                    _attackableGate += " !ECHO " + echo
                        + "@+" + (tick - _gateOpenedTick).ToString(CultureInfo.InvariantCulture) + "t";
                    _gateOpenedTick = 0; // recorded once is enough
                }
            }

            if (_engageTick == int.MinValue)
            {
                if (damaged)
                {
                    _engageTick = tick;
                    _engageUtc = now;
                }
                else if (_maxHealth > 0d && guardian.CurHealth > 0d)
                {
                    // Still untouched. Remembering when that was last true keeps a
                    // guardian that dies between two collections from being charged
                    // for the walk over to it: the fall-back start is the last moment
                    // its health bar was known full, not the moment it appeared.
                    _fullHealthTick = tick;
                    _fullHealthUtc = now;
                }
            }

            _running = !_killed && HasStarted();

            var dead = !guardian.IsAlive
                || guardian.CurHealth <= 0d
                || guardian.AnimationState == AcdAnimationState.Dead;

            if (!dead)
            {
                _seenAlive = true;
                _lastSeenTick = tick;
                _lastSeenUtc = now;
                _dbgState = _running
                    ? "fight in progress"
                    : "guardian up, waiting for it to become attackable";
            }
            else if (_seenAlive)
            {
                RegisterKill(tick, now, "actor");
                _dbgState = "kill registered (actor reported dead)";
            }
            else
            {
                // Dead on the very first sighting, so nothing here proves the actor was
                // ever really alive -- one caught half loaded reads zero health the same
                // way. Left alone on purpose: the reward-step safety net in AfterCollect
                // picks the kill up a moment later, and anchors it on the rift rather
                // than on this one dubious frame.
                _dbgState = "guardian reads dead on first sighting, left to the safety net";
            }

            _dbgGuardian = _guardianName
                + " hp=" + guardian.CurHealth.ToString("0", CultureInfo.InvariantCulture)
                + "/" + _maxHealth.ToString("0", CultureInfo.InvariantCulture)
                + " anim=" + guardian.AnimationState;

            _dbgAttackable = "seen=" + _attackableSeen
                + " gate=" + _attackableGate
                + " -- now blocked by " + (AttackabilityBlocker(guardian) ?? "nothing")
                + ", damaged=" + damaged
                + ", attrs now " + SpawnPhaseAttributes(guardian);
        }

        private void TrackMissingGuardian(DateTime now, bool rewardStep)
        {
            _dbgGuardian = "no guardian on the actor list";
            _dbgAttackable = "-";

            if (_guardianAcd == 0u || _killed || !_seenAlive)
            {
                _dbgState = _killed ? "killed, waiting for the next rift" : "waiting for the guardian";
                return;
            }

            // The guardian was being tracked and is gone from the list. That is usually a
            // kill, but it is also what happens when the hero respawns far from the
            // fight, so wait a moment and ask the rift whether it has moved on.
            if (_vanishedUtc == DateTime.MinValue)
            {
                _vanishedUtc = now;
                _dbgState = "guardian vanished, waiting for confirmation";
                return;
            }

            if ((now - _vanishedUtc).TotalMilliseconds < VanishGraceMs)
            {
                _dbgState = "guardian vanished, waiting for confirmation";
                return;
            }

            if (rewardStep)
            {
                RegisterKill(_lastSeenTick, _lastSeenUtc, "vanish");
                _dbgState = "kill registered (guardian gone, rift moved on)";
            }
            else
            {
                // No sample: something interrupted the fight rather than ending it.
                ResetRun();
                _dbgState = "guardian lost without a rift completion, run dropped";
            }
        }

        /// <summary>
        /// The rift guardian, or null. Boss rarity is the main signal; the actor code is
        /// the fallback for the handful of guardians that do not report it. Summoned adds
        /// and illusions are filtered out so a clone never passes for the real one.
        /// </summary>
        private IMonster FindGuardian()
        {
            IMonster fallback = null;

            foreach (var monster in Hud.Game.Monsters)
            {
                if (monster.Illusion)
                    continue;
                if (monster.SummonerAcdDynamicId != uint.MinValue)
                    continue; // an add, not the guardian
                if (monster.SnoActor.Sno == ActorSnoEnum._x1_lr_boss_terrordemon_a_breathminion)
                    continue; // Orlash's breath minion carries the boss code too
                var isGuardian = monster.Rarity == ActorRarity.Boss
                    || (monster.SnoMonster != null && monster.SnoMonster.Priority == MonsterPriority.boss)
                    || monster.SnoActor.Code.Contains("_Boss_");

                if (!isGuardian)
                    continue;

                // Once locked on, stay on the same actor for the rest of the fight.
                if (_guardianAcd != 0u && monster.AcdId == _guardianAcd)
                    return monster;

                if (fallback == null)
                    fallback = monster;
            }

            return fallback;
        }

        /// <summary>
        /// True once the rift has reached its reward step, which only happens after the
        /// guardian is down. Used to tell a real kill from a guardian that merely dropped
        /// off the actor list.
        /// </summary>
        private bool IsRiftRewardStep()
        {
            var riftQuest = Hud.Sno.SnoQuests.NephalemRift_337492;
            if (riftQuest == null)
                return Hud.Game.RiftPercentage >= 100d;

            foreach (var quest in Hud.Game.Quests)
            {
                if (quest.SnoQuest != riftQuest)
                    continue;
                return quest.QuestStepId == RiftRewardQuestStep;
            }

            return false;
        }

        /// <summary>Quest step of NephalemRift_337492 that means "the rift is over, claim the reward".</summary>
        private const uint RiftRewardQuestStep = 34u;

        private void RegisterKill(int endTick, DateTime endUtc, string path)
        {
            if (_killed)
                return;

            _killed = true;
            _killPath = path;
            _running = false;

            int startTick;
            DateTime startUtc;
            if (!TryGetStartPoint(endTick, endUtc, out startTick, out startUtc))
            {
                _killPath = path + ", no anchor";
                return;
            }

            var ms = UseGameTime
                ? TicksToMilliseconds(startTick, endTick)
                : (endUtc - startUtc).TotalMilliseconds;

            if (ms < 0d || double.IsNaN(ms))
                ms = 0d;

            _currentMs = ms;

            if (ms < MinimumValidMilliseconds)
                return; // shown, but too short to be believed: kept out of the average

            KillCount++;
            TotalMilliseconds += ms;
            if (KillCount == 1 || ms < BestMilliseconds) BestMilliseconds = ms;
            if (KillCount == 1 || ms > WorstMilliseconds) WorstMilliseconds = ms;
        }

        /// <summary>
        /// Can the guardian be hit right now? A rift guardian spends its first moment on
        /// screen playing a spawn animation it cannot be interrupted during, and the
        /// clock has no business running then.
        ///
        /// Every flag the API offers that means "not hittable yet" is tested, not just
        /// the obvious two: a guardian seen starting its clock early was doing so because
        /// its own spawn phase raised none of the ones we happened to check. Which flag
        /// each guardian raises is not documented anywhere, so the safe reading is the
        /// union. Only the very first transition matters -- once the fight has started
        /// the answer is latched -- so a monster that later stealths or burrows mid-fight
        /// cannot restart anything.
        ///
        /// Deliberately NOT IMonster.Attackable, which folds in IsOnScreen and would
        /// therefore answer "no" every time the camera loses the guardian mid-fight.
        /// </summary>
        private bool IsAttackable(IMonster guardian)
        {
            return AttackabilityBlocker(guardian) == null;
        }

        /// <summary>
        /// The flag currently keeping the guardian unhittable, or null if none is. Same
        /// test as IsAttackable, but it names the reason, which is what the diagnostic
        /// panel needs: by the time anyone looks at the screen, the transition is long
        /// past and the live flags say nothing about what happened.
        /// </summary>
        private string AttackabilityBlocker(IMonster guardian)
        {
            if (guardian.Untargetable) return "untargetable";
            if (guardian.Invulnerable) return "invulnerable";
            if (guardian.Hidden) return "hidden";
            if (guardian.Invisible) return "invisible";
            if (guardian.Stealthed) return "stealthed";
            if (guardian.Burrowed) return "burrowed";
            if (guardian.AnimationState == AcdAnimationState.Spawn) return "anim=Spawn";

            return null;
        }

        /// <summary>
        /// Raw game attributes that might mark a spawn phase, listed only when actually
        /// set. Diagnostic for now, NOT part of the gate: which of these a rift guardian
        /// raises during its entrance has never been observed, and guessing is what cost
        /// this plugin two wasted iterations already.
        ///
        /// They are worth watching because IMonster's own flags are derived views, and
        /// the raw list carries things they do not -- AI_Used_Scripted_Spawn_Anim says
        /// outright that the actor is playing a scripted entrance, and Gethit_Immune
        /// says damage will not register, which is the real question.
        ///
        /// Whichever ones turn out to fire reliably belong in AttackabilityBlocker.
        /// </summary>
        private string SpawnPhaseAttributes(IMonster guardian)
        {
            var set = new List<string>();

            AddIfSet(set, guardian, Hud.Sno.Attributes.AI_Used_Scripted_Spawn_Anim, "scriptedSpawnAnim");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Gethit_Immune, "gethitImmune");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Immunity, "immunity");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Uninterruptible, "uninterruptible");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Untargetable, "attr:untargetable");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Invulnerable, "attr:invulnerable");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Hidden, "attr:hidden");
            AddIfSet(set, guardian, Hud.Sno.Attributes.Disabled, "attr:disabled");

            return set.Count == 0 ? "none" : string.Join(",", set.ToArray());
        }

        private static void AddIfSet(List<string> into, IMonster guardian, IAttribute attribute, string label)
        {
            if (attribute == null)
                return;

            // -1 is the "not present on this actor" default, 0 is present but off.
            var value = guardian.GetAttributeValue(attribute, 0, -1d);
            if (value != 0d && value != -1d)
                into.Add(label);
        }

        /// <summary>Has the clock started, under the currently selected StartOn?</summary>
        private bool HasStarted()
        {
            switch (StartOn)
            {
                case GrBossTimerStart.FirstDamage:
                    return _engageTick != int.MinValue;

                case GrBossTimerStart.GuardianSpawn:
                    return _guardianAcd != 0u || _riftFullTick != 0;

                default:
                    return _attackableSeen;
            }
        }

        /// <summary>
        /// Where the clock is counting from. The preferred anchor depends on StartOn, but
        /// every mode falls back the same way: the moment the guardian became attackable,
        /// then the moment the progress bar filled -- the latter being the only anchor
        /// left for a guardian the actor list never showed at all.
        ///
        /// false when there is no anchor whatsoever, which means nothing was ever
        /// observed and there is nothing to measure. Returning a bogus zero instead
        /// would quietly poison the average with a kill of no duration.
        /// </summary>
        private bool TryGetStartPoint(int nowTick, DateTime nowUtc, out int tick, out DateTime utc)
        {
            switch (StartOn)
            {
                case GrBossTimerStart.FirstDamage:
                    if (_engageTick != int.MinValue)
                    {
                        tick = _engageTick;
                        utc = _engageUtc;
                        return true;
                    }
                    if (_fullHealthUtc != DateTime.MinValue)
                    {
                        tick = _fullHealthTick;
                        utc = _fullHealthUtc;
                        return true;
                    }
                    break;

                case GrBossTimerStart.GuardianSpawn:
                    // The bar filling IS the spawn, and it is known even when the actor
                    // list never showed the guardian at all.
                    if (_riftFullTick != 0)
                    {
                        tick = _riftFullTick;
                        utc = _riftFullUtc;
                        return true;
                    }
                    break;
            }

            if (_attackableUtc != DateTime.MinValue)
            {
                tick = _attackableTick;
                utc = _attackableUtc;

                // Cross-check against the rift anchor, and override the observation when
                // it is plainly not one. See SpawnProtectionTicks for why this is sound.
                if (_riftFullTick != 0)
                {
                    var expected = _riftFullTick + SpawnProtectionTicks;
                    if (Math.Abs(tick - expected) > SpawnProtectionToleranceTicks)
                    {
                        utc = InterpolateUtc(expected, nowTick, nowUtc);
                        tick = expected;
                        _anchorCorrected = true;
                    }
                }

                return true;
            }

            if (_riftFullTick != 0)
            {
                tick = _riftFullTick;
                utc = _riftFullUtc;
                return true;
            }

            tick = 0;
            utc = DateTime.MinValue;
            return false;
        }

        /// <summary>
        /// A wall-clock stamp for a tick we never observed, interpolated at the rate this
        /// rift is actually running at. A game speed hack breaks any fixed tick-to-second
        /// conversion, so the rate has to come from the rift itself.
        /// </summary>
        private DateTime InterpolateUtc(int targetTick, int nowTick, DateTime nowUtc)
        {
            var span = nowTick - _riftFullTick;
            if (span <= 0 || _riftFullUtc == DateTime.MinValue)
                return nowUtc;

            var fraction = (targetTick - _riftFullTick) / (double)span;
            return _riftFullUtc.AddTicks((long)((nowUtc - _riftFullUtc).Ticks * fraction));
        }

        private static double TicksToMilliseconds(int fromTick, int toTick)
        {
            return (toTick - fromTick) * 1000.0d / GameTicksPerSecond;
        }

        private void ResetRun()
        {
            _guardianAcd = 0u;
            _guardianName = null;
            _spawnTick = 0;
            _engageTick = int.MinValue;
            _engageUtc = DateTime.MinValue;
            _fullHealthTick = 0;
            _fullHealthUtc = DateTime.MinValue;
            _attackableTick = 0;
            _attackableUtc = DateTime.MinValue;
            _attackableSeen = false;
            _attackableGate = "-";
            _spawnAttrsAtFirstSight = "-";
            _gateOpenedTick = 0;
            _anchorCorrected = false;
            _lastSeenTick = 0;
            _lastSeenUtc = DateTime.MinValue;
            _maxHealth = 0d;
            _seenAlive = false;
            _vanishedUtc = DateTime.MinValue;
            _currentMs = -1d;
            _running = false;
            _killed = false;
        }

        /// <summary>
        /// Everything ResetRun clears, plus the rift-level anchor. Used on the boundaries
        /// of a rift; the vanish path uses ResetRun on its own so that dropping a run
        /// does not throw away the moment the progress bar filled.
        /// </summary>
        private void ResetRift()
        {
            CloseRift();

            ResetRun();
            _riftFullTick = 0;
            _riftFullUtc = DateTime.MinValue;
            _riftWasFull = false;
            _inProgressAfterFull = 0;
            _killPath = null;
            _closeReason = null;
        }

        /// <summary>
        /// Book the rift that is ending, so that a rift which counted nothing says so
        /// instead of leaving no trace at all. Every anchor is written down, which is
        /// what makes the next anomaly readable rather than guessable.
        /// </summary>
        private void CloseRift()
        {
            if (_riftFullTick == 0 && !_killed)
                return; // nothing ran: first collection, menu, or a rift joined finished

            _riftsSeen++;

            var entry = "#" + _riftsSeen.ToString(CultureInfo.InvariantCulture) + " ";

            if (_killed && _currentMs >= 0d)
            {
                entry += "ok " + FormatDuration(_currentMs) + " via " + (_killPath ?? "?")
                    + " [gate " + _attackableGate + "]"
                    + (_anchorCorrected ? " [ANCHOR CORRECTED to riftFull+" + SpawnProtectionTicks.ToString(CultureInfo.InvariantCulture) + "t]" : "");
            }
            else if (!_riftWasFull)
            {
                // The bar never filled, so no guardian ever spawned. An abandoned or
                // failed rift, not a miss.
                entry += "no guardian phase";
            }
            else
            {
                entry += "NO SAMPLE"
                    + " closedBy=" + (_closeReason ?? "new rift")
                    + " anchor=" + (_riftFullTick != 0 ? "yes" : "no")
                    + " acd=" + _guardianAcd.ToString(CultureInfo.InvariantCulture)
                    + " alive=" + _seenAlive
                    + " engaged=" + (_engageTick != int.MinValue);
            }

            _history.Add(entry);
            while (_history.Count > HistoryLength)
                _history.RemoveAt(0);

            AppendToLog(entry);
        }

        // =====================================================================
        // Log file
        // =====================================================================

        /// <summary>
        /// Where the log lands. Relative to the TurboHUD folder, and deliberately NOT
        /// under plugins/: writing there would trip the file watcher and make the HUD
        /// recompile every rift.
        /// </summary>
        private string LogPath()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(root)) root = Environment.CurrentDirectory;
            return Path.Combine(root, LogFileName.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// One line per rift, appended. The on-screen history only keeps the last few,
        /// which is no use over a long run: this is what can be read back afterwards.
        /// Failures are swallowed on purpose -- a plugin that stops timing rifts because
        /// a disk write failed would be worse than one that quietly loses its log.
        /// </summary>
        private void AppendToLog(string entry)
        {
            if (!LogToFile)
                return;

            try
            {
                var path = LogPath();
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                var text = new StringBuilder();

                if (!_logHeaderWritten)
                {
                    _logHeaderWritten = true;
                    text.AppendLine();
                    text.AppendLine("=== session started " + Hud.Time.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  build=" + BuildTag
                        + " startOn=" + StartOn
                        + " clock=" + (UseGameTime ? "gameticks" : "wallclock") + " ===");
                }

                text.AppendLine(Hud.Time.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  " + entry
                    + "  guardian=" + (_guardianName ?? "-")
                    + "  anchors riftFull=" + _riftFullTick.ToString(CultureInfo.InvariantCulture)
                    + " spawn=" + _spawnTick.ToString(CultureInfo.InvariantCulture)
                    + " attackable=" + _attackableTick.ToString(CultureInfo.InvariantCulture)
                    + " engage=" + (_engageTick == int.MinValue ? "-" : _engageTick.ToString(CultureInfo.InvariantCulture)));

                File.AppendAllText(path, text.ToString(), Encoding.ASCII);
            }
            catch
            {
                // Never let logging break the timing.
            }
        }

        /// <summary>Wipe the session statistics. Safe to call from a customizer or a hotkey.</summary>
        public void ResetStats()
        {
            KillCount = 0;
            RecoveredKillCount = 0;
            _history.Clear();
            _riftsSeen = 0;
            TotalMilliseconds = 0d;
            BestMilliseconds = 0d;
            WorstMilliseconds = 0d;
        }

        // =====================================================================
        // Rendering
        // =====================================================================

        public void PaintTopInGame(ClipState clipState)
        {
            if (clipState != ClipState.BeforeClip)
                return;
            if (!Enabled || Hud.Render.UiHidden || !Hud.Game.IsInGame)
                return;

            if (DebugEnabled)
                PaintDebug();

            if (HideOnMapModes && (Hud.Game.MapMode == MapMode.WaypointMap || Hud.Game.MapMode == MapMode.ActMap))
                return;
            if (HideInTown && Hud.Game.IsInTown)
                return;
            if (HideUntilFirstBoss && KillCount == 0 && _currentMs < 0d && !_running)
                return;

            var line1 = CurrentLabel + BuildCurrentText();
            var line2 = AverageLabel + BuildAverageText();
            var line3 = ShowSessionLine ? SessionLabel + BuildSessionText() : null;

            // The whole block in one string, exactly as the tracker text does it: the
            // metrics of a multi-line layout report the widest line, which is what has
            // to fit the minimap.
            var block = line3 == null ? line1 + "\n" + line2 : line1 + "\n" + line2 + "\n" + line3;

            var minimapWidth = GetMinimapWidth();
            if (AutoFontSize && minimapWidth > 0f)
                EnsureAutoFonts(minimapWidth, block);

            var textFont = EffectiveTextFont();
            var runningFont = EffectiveRunningFont();

            var lineHeight = MeasureLineHeight(textFont) * (1f + LineSpacing);

            float x, y;
            if (!GetOrigin(lineHeight, out x, out y))
                return;

            var currentFont = _running && !_killed ? runningFont : textFont;
            currentFont.DrawText(line1, x, y);
            textFont.DrawText(line2, x, y + lineHeight);

            if (line3 != null)
                textFont.DrawText(line3, x, y + (lineHeight * 2f));
        }

        private string BuildCurrentText()
        {
            double ms;

            int startTick;
            DateTime startUtc;

            if (_running && !_killed && TryGetStartPoint(Hud.Game.CurrentGameTick, Hud.Time.Now, out startTick, out startUtc))
            {
                ms = UseGameTime
                    ? TicksToMilliseconds(startTick, Hud.Game.CurrentGameTick)
                    : (Hud.Time.Now - startUtc).TotalMilliseconds;
                if (ms < 0d) ms = 0d;
            }
            else if (_currentMs >= 0d)
            {
                ms = _currentMs;
            }
            else
            {
                return IdlePlaceholder;
            }

            var text = FormatDuration(ms);

            if (ShowGuardianName && !string.IsNullOrEmpty(_guardianName))
                text += " " + _guardianName;

            return text;
        }

        /// <summary>
        /// Fastest and slowest of the session, each named rather than left to position.
        /// The kill count and the average are left out on purpose: the line above already
        /// carries both, and repeating them would cost width for nothing.
        /// </summary>
        private string BuildSessionText()
        {
            if (KillCount == 0)
                return IdlePlaceholder;

            return "best " + FormatDuration(BestMilliseconds)
                + " // worst " + FormatDuration(WorstMilliseconds);
        }

        private string BuildAverageText()
        {
            if (KillCount == 0)
                return IdlePlaceholder;

            var text = FormatDuration(AverageMilliseconds);

            if (ShowSampleCount)
                text += " x" + KillCount.ToString(CultureInfo.InvariantCulture);

            return text;
        }

        /// <summary>
        /// Seconds with one decimal below a minute, m:ss.d above it. Short kills are the
        /// common case and the tenth of a second is where the interesting variation is.
        /// </summary>
        private static string FormatDuration(double milliseconds)
        {
            if (milliseconds < 0d || double.IsNaN(milliseconds))
                milliseconds = 0d;

            var totalSeconds = milliseconds / 1000d;

            if (totalSeconds < 60d)
                return totalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

            var minutes = (int)(totalSeconds / 60d);
            var seconds = totalSeconds - (minutes * 60d);

            return minutes.ToString(CultureInfo.InvariantCulture)
                + ":" + seconds.ToString("00.0", CultureInfo.InvariantCulture);
        }

        // =====================================================================
        // Layout
        // =====================================================================

        private bool GetOrigin(float lineHeight, out float x, out float y)
        {
            var w = (float)Hud.Window.Size.Width;
            var h = (float)Hud.Window.Size.Height;

            x = w * CustomX;
            y = h * CustomY;

            if (Anchor == GrBossTimerAnchor.BelowMinimapText)
            {
                var minimap = Hud.Render.MinimapUiElement;
                if (minimap == null || !minimap.Visible || minimap.Rectangle.Width <= 0f)
                    return false;

                var rect = minimap.Rectangle;
                x = rect.Left;
                y = rect.Top + (PanelLineCount * lineHeight);
            }

            x += h * OffsetX;
            y += h * OffsetY;

            return true;
        }

        private float GetMinimapWidth()
        {
            var minimap = Hud.Render.MinimapUiElement;
            if (minimap == null || !minimap.Visible)
                return 0f;

            return minimap.Rectangle.Width;
        }

        private IFont EffectiveTextFont()
        {
            return AutoFontSize && _autoTextFont != null ? _autoTextFont : TextFont;
        }

        private IFont EffectiveRunningFont()
        {
            return AutoFontSize && _autoRunningFont != null ? _autoRunningFont : RunningFont;
        }

        /// <summary>
        /// Size the block the way the tracker text above it sizes itself, so the two
        /// match at any window size.
        ///
        /// That text is NOT drawn at a fixed size. It grows until it fills the minimap
        /// width, and stops early only once a line reaches MinLineHeight pixels AND the
        /// size reaches MaxFontSize. Both halves of that cap matter. TurboHUD font sizes
        /// scale with the window, so at a small resolution a size of MaxFontSize gives
        /// lines well under MinLineHeight, the cap is not satisfied, and the text keeps
        /// growing past MaxFontSize until it is. Drawing at a fixed MaxFontSize therefore
        /// looks right at 1080p and visibly too small at 800x600 -- which is exactly what
        /// this block used to do.
        /// </summary>
        private void EnsureAutoFonts(float minimapWidth, string block)
        {
            var widthChanged = Math.Abs(minimapWidth - _lastMinimapWidth) > 0.01f;

            // The rendered text changes every frame while the clock ticks, so the search
            // is keyed on its length rather than its content. A same-length line differs
            // by a pixel or two at most, far below the 0.1 step of the search.
            if (!widthChanged && block.Length == _lastBlockLength && _autoTextFont != null)
                return;

            _lastMinimapWidth = minimapWidth;
            _lastBlockLength = block.Length;

            // A resize re-derives from the bottom, as the tracker text does by resetting
            // its own size. Growing from the previous size would leave the font too large
            // on a shrink whenever the width is not what caps it. A mere text-length
            // change can start where it left off.
            var size = widthChanged || _fontSize <= 0f ? FontSizeFloor : _fontSize;

            while (size < FontSizeCeiling)
            {
                var font = ProbeFont(size);
                if (font.GetTextLayout(block).Metrics.Width >= minimapWidth)
                    break;
                if (font.GetTextLayout("X").Metrics.Height >= MinLineHeight && size >= MaxFontSize)
                    break;

                size += FontSizeStep;
            }

            while (size > FontSizeFloor)
            {
                if (ProbeFont(size).GetTextLayout(block).Metrics.Width <= minimapWidth)
                    break;

                size -= FontSizeStep;
            }

            if (Math.Abs(size - _fontSize) < 0.001f && _autoTextFont != null)
                return;

            _fontSize = size;
            _autoTextFont = Hud.Render.CreateFont("Arial", size, 255, 255, 255, 255, true, false, false);
            _autoRunningFont = Hud.Render.CreateFont("Arial", size, 255, 255, 225, 130, true, false, false);
        }

        private IFont ProbeFont(float size)
        {
            if (_probeFont == null || Math.Abs(size - _probeSize) > 0.001f)
            {
                _probeFont = Hud.Render.CreateFont("Arial", size, 255, 255, 255, 255, true, false, false);
                _probeSize = size;
            }

            return _probeFont;
        }

        /// <summary>
        /// Height of one line, in pixels, at the current window size. Measured from the
        /// font actually being drawn with, so the placement below the tracker text stays
        /// true once that font follows a resize.
        /// </summary>
        private float MeasureLineHeight(IFont font)
        {
            var h = MinLineHeight;

            if (font != null)
            {
                var layout = font.GetTextLayout("X");
                if (layout != null && layout.Metrics.Height > h)
                    h = layout.Metrics.Height;
            }

            _dbgLineHeight = h;
            return h;
        }

        // =====================================================================
        // Diagnostics
        // =====================================================================

        private void PaintDebug()
        {
            var w = (float)Hud.Window.Size.Width;
            var h = (float)Hud.Window.Size.Height;
            var x = w * DebugX;
            var y = h * DebugY;
            var step = DebugFont.GetTextLayout("X").Metrics.Height * 1.15f;

            var lines = new List<string>
            {
                "-- Greater Rift boss timer --",
                "state       : " + _dbgState,
                "guardian    : " + _dbgGuardian,
                "attackable  : " + _dbgAttackable,
                "in rift     : " + _wasInGreaterRift
                                 + " (sticky) -- specialArea=" + Hud.Game.SpecialArea
                                 + " inGr=" + Hud.Game.Me.InGreaterRift
                                 + " grRank=" + Hud.Game.Me.InGreaterRiftRank.ToString(CultureInfo.InvariantCulture)
                                 + " town=" + Hud.Game.IsInTown,
                "rift %      : " + Hud.Game.RiftPercentage.ToString("0.0", CultureInfo.InvariantCulture),
                "reward step : " + IsRiftRewardStep(),
                "clock       : " + (UseGameTime ? "game ticks" : "wall clock")
                                 + ", starts on " + StartOn,
                "acd tracked : " + _guardianAcd.ToString(CultureInfo.InvariantCulture)
                                 + " running=" + _running + " killed=" + _killed
                                 + " seenAlive=" + _seenAlive,
                "anchors     : riftFull=" + _riftFullTick.ToString(CultureInfo.InvariantCulture)
                                 + " spawn=" + _spawnTick.ToString(CultureInfo.InvariantCulture)
                                 + " fullHp=" + _fullHealthTick.ToString(CultureInfo.InvariantCulture)
                                 + " attackable=" + _attackableTick.ToString(CultureInfo.InvariantCulture)
                                 + " engage=" + (_engageTick == int.MinValue ? "-" : _engageTick.ToString(CultureInfo.InvariantCulture))
                                 + " now=" + Hud.Game.CurrentGameTick.ToString(CultureInfo.InvariantCulture),
                "current     : " + (_currentMs >= 0d ? FormatDuration(_currentMs) : "-"),
                "recovered   : " + RecoveredKillCount.ToString(CultureInfo.InvariantCulture)
                                 + " of those kills came from the safety net",
                "rift flags  : full=" + _riftWasFull
                                 + " rewardStep=" + IsRiftRewardStep()
                                 + " newRift=" + _inProgressAfterFull.ToString(CultureInfo.InvariantCulture)
                                 + "/" + NewRiftConfirmCollections.ToString(CultureInfo.InvariantCulture),
                "session     : " + KillCount.ToString(CultureInfo.InvariantCulture) + " kills, avg "
                                 + (KillCount > 0 ? FormatDuration(AverageMilliseconds) : "-")
                                 + ", best " + (KillCount > 0 ? FormatDuration(BestMilliseconds) : "-")
                                 + ", worst " + (KillCount > 0 ? FormatDuration(WorstMilliseconds) : "-"),
                "font        : " + (AutoFontSize ? "auto " : "fixed ")
                                 + _fontSize.ToString("0.0", CultureInfo.InvariantCulture)
                                 + " (cap " + MaxFontSize.ToString("0.0", CultureInfo.InvariantCulture)
                                 + ", floor " + MinLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                                 + "px), minimap " + _lastMinimapWidth.ToString("0", CultureInfo.InvariantCulture) + "px",
                "line height : " + _dbgLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                                 + " px, " + PanelLineCount.ToString(CultureInfo.InvariantCulture) + " lines down",
            };

            // One line per rift that has ended, newest last. A rift that counted nothing
            // shows up here as NO SAMPLE with the anchors it had, which says which check
            // gave up rather than leaving the miss invisible.
            lines.Add("last rifts  : " + (_history.Count == 0 ? "none yet" : ""));
            foreach (var entry in _history)
                lines.Add("              " + entry);

            foreach (var line in lines)
            {
                DebugFont.DrawText(line, x, y);
                y += step;
            }
        }
    }
}
