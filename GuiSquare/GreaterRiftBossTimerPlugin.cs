using System;
using System.Collections.Generic;
using System.Globalization;
using Turbo.Plugins.Default;

namespace Turbo.Plugins.GuiSquare
{
    /// <summary>
    /// Times how long the Greater Rift guardian takes to die, and keeps a running average
    /// over the whole session.
    ///
    /// Two counters are drawn, one per line:
    ///
    ///   1. CURRENT  - the guardian of the rift you are in. It ticks live during the
    ///                 fight, then freezes on the final value and stays on screen until
    ///                 the next Greater Rift starts.
    ///   2. AVERAGE  - the mean of every guardian killed since TurboHUD started, with the
    ///                 number of samples behind it.
    ///
    /// The clock is driven by game ticks (60 per second), not by the wall clock, so the
    /// numbers stay comparable whatever the game speed is. Set UseGameTime to false to
    /// measure real elapsed time instead.
    ///
    /// The two lines sit at the minimap's top-left corner, pushed down by PanelLineCount
    /// text lines so they land just under the tracker text that is already drawn there.
    /// </summary>

    public enum GrBossTimerStart
    {
        FirstDamage,    // the clock starts when the guardian first loses health
        GuardianSpawn   // the clock starts the moment the guardian is first seen
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
        /// This is THE value to adjust if the two lines overlap the text above them or
        /// float too low: count the lines already drawn over the minimap, and land right
        /// under the last one.
        ///
        /// The line height is measured live at the current window size, so the block
        /// keeps its place when the game window is resized.
        /// </summary>
        public int PanelLineCount { get; set; }

        /// <summary>
        /// Absolute floor for one line of the text drawn over the minimap, in pixels.
        /// That text stops shrinking at this height however small the window gets, which
        /// is why a plain fraction of the minimap height drifts and a measured line does
        /// not.
        /// </summary>
        public float MinLineHeight { get; set; }

        /// <summary>Free placement, as a screen ratio (0..1). Only used when Anchor == Custom.</summary>
        public float CustomX { get; set; }
        public float CustomY { get; set; }

        /// <summary>Fine tuning for either anchor, as a ratio of the screen height.</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        /// <summary>Extra spacing between the two lines, as a fraction of a line height.</summary>
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

        /// <summary>Append the best and worst kill of the session, as " (best 8.4s / worst 41.2s)".</summary>
        public bool ShowBestAndWorst { get; set; }

        /// <summary>Append the guardian's name to the current line while the fight is on.</summary>
        public bool ShowGuardianName { get; set; }

        /// <summary>Hide both lines entirely until the first guardian of the session is engaged.</summary>
        public bool HideUntilFirstBoss { get; set; }

        /// <summary>Hide both lines while the world map or the act map is open.</summary>
        public bool HideOnMapModes { get; set; }

        /// <summary>Hide both lines while in town.</summary>
        public bool HideInTown { get; set; }

        // ---------------------------------------------------------------------
        // Measurement
        // ---------------------------------------------------------------------

        /// <summary>Where the clock starts. Default: on the guardian's first point of damage.</summary>
        public GrBossTimerStart StartOn { get; set; }

        /// <summary>
        /// true  = measure in game ticks (60 per second), which is what the rift timer
        ///         itself counts, and the one measure a changed game speed cannot skew.
        /// false = measure real elapsed time on the wall clock.
        /// </summary>
        public bool UseGameTime { get; set; }

        /// <summary>
        /// Fraction of max health the guardian has to drop below before the fight counts
        /// as started. Slightly under 1 so that a rounding wobble on a full health bar
        /// does not start the clock on its own.
        /// </summary>
        public double FirstDamageThreshold { get; set; }

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

        /// <summary>Font of both lines. Matches the tracker text drawn above it.</summary>
        public IFont TextFont { get; set; }

        /// <summary>Font used for the current line while the fight is still running.</summary>
        public IFont RunningFont { get; set; }

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

        /// <summary>Average kill time of the session, in milliseconds. 0 when there is no sample.</summary>
        public double AverageMilliseconds
        {
            get { return KillCount > 0 ? TotalMilliseconds / KillCount : 0d; }
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        private const double GameTicksPerSecond = 60.0d;

        // Guardian being tracked in the current rift.
        private uint _guardianAcd;
        private string _guardianName;
        private int _spawnTick;
        private DateTime _spawnUtc;
        private int _engageTick;         // int.MinValue while the fight has not started
        private DateTime _engageUtc;
        private int _fullHealthTick;     // last moment the guardian was seen untouched
        private DateTime _fullHealthUtc;
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
        private bool _wasInGreaterRift;

        private IFont _lineProbeFont;
        private float _dbgLineHeight;
        private string _dbgState = "-";
        private string _dbgGuardian = "-";

        public GreaterRiftBossTimerPlugin()
        {
            Enabled = true;
            Order = 30001; // right after the follower status icon

            Anchor = GrBossTimerAnchor.BelowMinimapText;
            PanelLineCount = 8;      // tracker lines already drawn over the minimap
            MinLineHeight = 13.5f;   // absolute floor of one of those lines, in pixels
            CustomX = 0.02f;
            CustomY = 0.30f;
            OffsetX = 0f;
            OffsetY = 0f;
            LineSpacing = 0f;

            CurrentLabel = "Bosskill: ";
            AverageLabel = "Bossavg: ";
            IdlePlaceholder = "-";
            ShowSampleCount = true;
            ShowBestAndWorst = false;
            ShowGuardianName = false;
            HideUntilFirstBoss = false;
            HideOnMapModes = true;
            HideInTown = false;

            StartOn = GrBossTimerStart.FirstDamage;
            UseGameTime = true;
            FirstDamageThreshold = 0.999d;
            MinimumValidMilliseconds = 0d;
            VanishGraceMs = 900;
            ResetStatsOnNewGame = false;

            DebugEnabled = false;
            DebugX = 0.35f;
            DebugY = 0.20f;

            ResetRun();
            ResetStats();
        }

        public override void Load(IController hud)
        {
            base.Load(hud);

            // Same family, size, weight and colour as the tracker text drawn over the
            // minimap, so the two lines read as a continuation of it.
            TextFont = Hud.Render.CreateFont("Arial", 8.0f, 255, 255, 255, 255, true, false, false);
            RunningFont = Hud.Render.CreateFont("Arial", 8.0f, 255, 255, 225, 130, true, false, false);
            DebugFont = Hud.Render.CreateFont("consolas", 8.5f, 255, 255, 255, 160, false, false, 200, 0, 0, 0, true);

            // Never drawn with. It only measures how tall one line of that tracker text
            // is right now, which is what tells us how far down it reaches.
            _lineProbeFont = Hud.Render.CreateFont("Arial", 8.0f, 255, 255, 255, 255, true, false, false);
        }

        // =====================================================================
        // Detection (collection phase, no rendering here)
        // =====================================================================

        public void OnNewArea(bool newGame, ISnoArea area)
        {
            if (newGame)
            {
                _wasInGreaterRift = false;
                ResetRun();
                if (ResetStatsOnNewGame)
                    ResetStats();
            }
        }

        public void AfterCollect()
        {
            if (!Enabled)
                return;

            if (!Hud.Game.IsInGame)
            {
                _wasInGreaterRift = false;
                ResetRun();
                _dbgState = "not in game";
                return;
            }

            if (Hud.Game.SpecialArea != SpecialArea.GreaterRift)
            {
                // Out of the rift. The last result stays on screen, but nothing is
                // tracked any more, and coming back in counts as a fresh rift.
                _wasInGreaterRift = false;
                _guardianAcd = 0u;
                _vanishedUtc = DateTime.MinValue;
                _running = false;
                _dbgState = "outside a greater rift";
                return;
            }

            if (!_wasInGreaterRift)
            {
                // A new Greater Rift just started: blank the current counter.
                _wasInGreaterRift = true;
                ResetRun();
            }

            var now = Hud.Time.Now;
            var tick = Hud.Game.CurrentGameTick;
            var guardian = FindGuardian();

            if (guardian != null)
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
                    _spawnUtc = now;
                    _engageTick = int.MinValue;
                    _fullHealthTick = tick;
                    _fullHealthUtc = now;
                    _maxHealth = 0d;
                    _seenAlive = false;
                    _currentMs = -1d;

                    if (StartOn == GrBossTimerStart.GuardianSpawn)
                    {
                        _engageTick = tick;
                        _engageUtc = now;
                        _running = true;
                    }
                }

                if (_maxHealth <= 0d && guardian.MaxHealth > 0d)
                    _maxHealth = guardian.MaxHealth;

                if (_engageTick == int.MinValue && _maxHealth > 0d && guardian.CurHealth > 0d)
                {
                    if (guardian.CurHealth < _maxHealth * FirstDamageThreshold)
                    {
                        _engageTick = tick;
                        _engageUtc = now;
                        _running = true;
                    }
                    else
                    {
                        // Still untouched. Remembering when that was last true keeps a
                        // guardian that dies between two collections from being charged
                        // for the walk over to it: the fall-back start is the last moment
                        // its health bar was known full, not the moment it appeared.
                        _fullHealthTick = tick;
                        _fullHealthUtc = now;
                    }
                }

                var dead = !guardian.IsAlive
                    || guardian.CurHealth <= 0d
                    || guardian.AnimationState == AcdAnimationState.Dead;

                if (!dead)
                {
                    // Nothing is ever recorded before the guardian has been confirmed
                    // alive once. An actor caught half loaded reads zero health, and
                    // without this it would book a phantom kill of no duration at all.
                    _seenAlive = true;
                    _lastSeenTick = tick;
                    _lastSeenUtc = now;
                    _dbgState = _running ? "fight in progress" : "guardian up, no damage yet";
                }
                else if (_seenAlive)
                {
                    RegisterKill(tick, now);
                    _dbgState = "kill registered (actor reported dead)";
                }
                else
                {
                    _dbgState = "guardian reads dead but was never seen alive, ignored";
                }

                _dbgGuardian = _guardianName
                    + " hp=" + guardian.CurHealth.ToString("0", CultureInfo.InvariantCulture)
                    + "/" + _maxHealth.ToString("0", CultureInfo.InvariantCulture)
                    + " anim=" + guardian.AnimationState;

                return;
            }

            _dbgGuardian = "no guardian on the actor list";

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

            if (IsRiftRewardStep())
            {
                RegisterKill(_lastSeenTick, _lastSeenUtc);
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
                if (monster.SnoActor.Type != ActorType.Monster)
                    continue;
                if (monster.Rarity != ActorRarity.Boss && !monster.SnoActor.Code.Contains("_Boss_"))
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

        private void RegisterKill(int endTick, DateTime endUtc)
        {
            if (_killed)
                return;

            _killed = true;
            _running = false;

            int startTick;
            DateTime startUtc;
            GetStartPoint(out startTick, out startUtc);

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
        /// Where the clock is counting from. Normally the first point of damage. Failing
        /// that -- a guardian killed between two collections, so its health bar was never
        /// caught in between -- the last moment it was seen at full health, which is the
        /// closest honest answer and never charges the fight for the walk over.
        /// </summary>
        private void GetStartPoint(out int tick, out DateTime utc)
        {
            if (_engageTick != int.MinValue)
            {
                tick = _engageTick;
                utc = _engageUtc;
            }
            else if (_fullHealthUtc != DateTime.MinValue)
            {
                tick = _fullHealthTick;
                utc = _fullHealthUtc;
            }
            else
            {
                tick = _spawnTick;
                utc = _spawnUtc;
            }
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
            _spawnUtc = DateTime.MinValue;
            _engageTick = int.MinValue;
            _engageUtc = DateTime.MinValue;
            _fullHealthTick = 0;
            _fullHealthUtc = DateTime.MinValue;
            _lastSeenTick = 0;
            _lastSeenUtc = DateTime.MinValue;
            _maxHealth = 0d;
            _seenAlive = false;
            _vanishedUtc = DateTime.MinValue;
            _currentMs = -1d;
            _running = false;
            _killed = false;
        }

        /// <summary>Wipe the session statistics. Safe to call from a customizer or a hotkey.</summary>
        public void ResetStats()
        {
            KillCount = 0;
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

            float x, y;
            if (!GetOrigin(out x, out y))
                return;

            var lineHeight = MeasureLineHeight() * (1f + LineSpacing);

            var currentFont = _running && !_killed ? RunningFont : TextFont;
            currentFont.DrawText(CurrentLabel + BuildCurrentText(), x, y);
            TextFont.DrawText(AverageLabel + BuildAverageText(), x, y + lineHeight);
        }

        private string BuildCurrentText()
        {
            double ms;

            if (_running && !_killed)
            {
                int startTick;
                DateTime startUtc;
                GetStartPoint(out startTick, out startUtc);
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

        private string BuildAverageText()
        {
            if (KillCount == 0)
                return IdlePlaceholder;

            var text = FormatDuration(AverageMilliseconds);

            if (ShowSampleCount)
                text += " x" + KillCount.ToString(CultureInfo.InvariantCulture);

            if (ShowBestAndWorst)
            {
                text += " (best " + FormatDuration(BestMilliseconds)
                     + " / worst " + FormatDuration(WorstMilliseconds) + ")";
            }

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

        private bool GetOrigin(out float x, out float y)
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
                y = rect.Top + (PanelLineCount * MeasureLineHeight());
            }

            x += h * OffsetX;
            y += h * OffsetY;

            return true;
        }

        /// <summary>
        /// Height of one line of the tracker text drawn over the minimap, in pixels, at
        /// the current window size. Font sizes scale with the window, so this shrinks and
        /// grows on a resize -- except below the absolute floor of that text, which is
        /// exactly why a fixed fraction of the minimap drifts and this does not.
        /// </summary>
        private float MeasureLineHeight()
        {
            var h = MinLineHeight;

            if (_lineProbeFont != null)
            {
                var layout = _lineProbeFont.GetTextLayout("X");
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
                "special area: " + Hud.Game.SpecialArea,
                "rift %      : " + Hud.Game.RiftPercentage.ToString("0.0", CultureInfo.InvariantCulture),
                "reward step : " + IsRiftRewardStep(),
                "clock       : " + (UseGameTime ? "game ticks" : "wall clock")
                                 + ", starts on " + StartOn,
                "acd tracked : " + _guardianAcd.ToString(CultureInfo.InvariantCulture)
                                 + " running=" + _running + " killed=" + _killed,
                "current     : " + (_currentMs >= 0d ? FormatDuration(_currentMs) : "-"),
                "session     : " + KillCount.ToString(CultureInfo.InvariantCulture) + " kills, avg "
                                 + (KillCount > 0 ? FormatDuration(AverageMilliseconds) : "-")
                                 + ", best " + (KillCount > 0 ? FormatDuration(BestMilliseconds) : "-")
                                 + ", worst " + (KillCount > 0 ? FormatDuration(WorstMilliseconds) : "-"),
                "line height : " + _dbgLineHeight.ToString("0.0", CultureInfo.InvariantCulture)
                                 + " px, " + PanelLineCount.ToString(CultureInfo.InvariantCulture) + " lines down",
            };

            foreach (var line in lines)
            {
                DebugFont.DrawText(line, x, y);
                y += step;
            }
        }
    }
}
