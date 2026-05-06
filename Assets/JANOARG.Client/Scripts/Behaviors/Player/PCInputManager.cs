using System;
using System.Collections.Generic;
using JANOARG.Client.Behaviors.Player;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

/// <summary>
///     Keyboard + mouse hybrid input manager for JANOARG on PC.
///
///     <para>Called by <see cref="PlayerInputManager.UpdateInput"/> when
///     <see cref="PlayerInputManager.DesktopInput"/> is true — it acts as a direct
///     replacement tick, not a parallel system. No <c>MonoBehaviour.Update</c> of its own.</para>
///
///     <para><b>Architecture</b> mirrors <see cref="PlayerInputManager"/> directly:
///     <list type="bullet">
///       <item>HitQueue processor loop — N held/pressed keys consume N notes (Normal + Catch).</item>
///       <item>Hold queue processor — mirrors <see cref="PlayerInputManager.HoldQueue_Processor"/>
///             verbatim; IsPlayerHolding is driven by whether any key is currently held.</item>
///       <item>Discrete queue processor — catch notes auto-clear while any key is held.</item>
///     </list></para>
///
///     <para><b>Keyboard:</b> Milthm-style any-key pool. Keys arriving within
///     <see cref="ChordGroupingWindowSec"/> are merged into a chord of size N. A separate
///     held-key count lets catch notes clear continuously while any key stays down.</para>
///
///     <para><b>Mouse ownership cue:</b> ticked via
///     <see cref="PCMouseOwnershipCueManager.sMain"/> at the end of each
///     <see cref="UpdateInput"/> — no canvas reference on this class.</para>
/// </summary>
public class PCInputManager : MonoBehaviour
{
    public static PCInputManager sInstance;

    // ─── Inspector ───────────────────────────────────────────────────────────────

    [Header("References")]
    public PlayerScreen       Player;
    public PlayerInputManager InputManager;

    [Header("Tuning")]
    [Tooltip("Chord-grouping window in seconds. Keys pressing within this window are merged " +
             "into a single chord. Not coupled to any timing window — tune independently.")]
    public float ChordGroupingWindowSec = 0.020f;

    // ─── Keyboard state ──────────────────────────────────────────────────────────

    private readonly List<Key>    _ChordBuffer      = new();
    private double                _ChordWindowStart = double.NegativeInfinity;
    private bool                  _ChordWindowOpen;
    private readonly HashSet<Key> _ConsumedKeys     = new();

    /// <summary>
    ///     Number of keyboard keys currently held down. > 0 means the player is actively
    ///     holding, which lets catch notes clear automatically on arrival — mirrors a resting
    ///     finger on the touchscreen.
    /// </summary>
    private int _HeldKeyCount;

    /// <summary>
    ///     Chord consumed this tick: how many keydown events arrived in the current grouping
    ///     window. Resets each tick after being applied to the hit queue.
    /// </summary>
    private int _PendingChordCount;

    /// <summary>
    ///     The most recently pressed key this tick. Used to attribute held-catch clears to
    ///     a specific key for per-key cooldown tracking.
    /// </summary>
    private Key _LastPressedKey;

    /// <summary>
    ///     Per-key cooldown expiry times (game time in seconds). After a key clears a catch
    ///     note via hold, that key cannot clear another catch note until either:
    ///     (a) the cooldown expires (BPM/16 seconds), or
    ///     (b) the next catch note's hitbox intersects the cursor — indicating the notes are
    ///         close enough that a single input would naturally cover both (no distinct input needed).
    /// </summary>
    private readonly Dictionary<Key, double> _CatchCooldownExpiry = new();

    // ─── Mouse ownership state ───────────────────────────────────────────────────

    private readonly List<PCOwnershipEntry> _OwnershipQueue      = new();
    private double                          _LastHoldReleaseTime = double.NegativeInfinity;

    // ─── Easing rank table ───────────────────────────────────────────────────────

    private static readonly EaseFunction[] sr_EasingRank =
    {
        EaseFunction.Elastic,
        EaseFunction.Exponential,
        EaseFunction.Circle,
        EaseFunction.Back,
        EaseFunction.Quintic,
        EaseFunction.Quartic,
        EaseFunction.Cubic,
        EaseFunction.Quadratic,
        EaseFunction.Sine,
        EaseFunction.Linear,
    };

    // ─── Raw input wiring ────────────────────────────────────────────────────────

    private Action<InputEventPtr, InputDevice> _OnInputEvent;

    // ─────────────────────────────────────────────────────────────────────────────
    // Unity lifecycle — minimal; no Update()
    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        sInstance     = this;
        _OnInputEvent = HandleRawInputEvent;
        InputSystem.onEvent += _OnInputEvent;
    }

    private void OnDestroy()
    {
        if (_OnInputEvent != null) InputSystem.onEvent -= _OnInputEvent;
        sInstance = null;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Raw input — enumerate only changed controls to avoid per-event allocation
    // ─────────────────────────────────────────────────────────────────────────────

    private void HandleRawInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        if (device is not Keyboard) return;
        if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;

        foreach (InputControl control in eventPtr.EnumerateChangedControls(device))
        {
            if (control is not KeyControl keyControl) continue;

            Key key = keyControl.keyCode;
            if (_ConsumedKeys.Contains(key)) continue;

            if (keyControl.wasPressedThisFrame)
            {
                _HeldKeyCount++;
                BufferKeyDown(key);
            }
            else if (keyControl.wasReleasedThisFrame && _HeldKeyCount > 0)
            {
                _HeldKeyCount--;
            }
        }
    }

    /// <summary>Called by upstream layers (system, UI) to prevent a key reaching the chord pool.</summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    private void BufferKeyDown(Key key)
    {
        _LastPressedKey = key;
        if (!_ChordWindowOpen)
        {
            _ChordWindowStart = Player.CurrentTime;
            _ChordWindowOpen  = true;
            _ChordBuffer.Clear();
        }
        _ChordBuffer.Add(key);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Main tick — called by PlayerInputManager.UpdateInput() when DesktopInput = true
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Main input tick. Called directly by <see cref="PlayerInputManager.UpdateInput"/>
    ///     in place of the touch pipeline when <see cref="PlayerInputManager.DesktopInput"/>
    ///     is true. Mirrors the structure of that method.
    /// </summary>
    public void UpdateInput()
    {
        _ConsumedKeys.Clear();

        // Flush chord window if it has expired this tick.
        _PendingChordCount = 0;
        if (_ChordWindowOpen && Player.CurrentTime - _ChordWindowStart >= ChordGroupingWindowSec)
        {
            _PendingChordCount = _ChordBuffer.Count;
            _ChordBuffer.Clear();
            _ChordWindowOpen = false;
        }

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        // ── HitQueue processor ────────────────────────────────────────────────
        // Mirrors PlayerInputManager's HitQueue loop. N chord keys hit N notes;
        // held keys additionally clear catch notes as they arrive.

        int chordBudget = _PendingChordCount;

        for (int a = 0; a < InputManager.HitQueue.Count; a++)
        {
            HitPlayer hit = InputManager.HitQueue[a];

            if (!hit)
            {
                InputManager.HitQueue.RemoveAt(a--);
                continue;
            }

            double delta = judgementOffsetTime - hit.Time;

            bool isDiscrete = hit.Current.Type == HitObject.HitType.Catch || hit.Current.Flickable;
            float window    = isDiscrete ? Player.PassWindow : Player.GoodWindow;

            if (hit.Current.HoldLength > 0 && !hit.PendingHoldQueue)
                hit.PendingHoldQueue = true;

            if (delta >= -window && !hit.IsProcessed)
            {
                bool consumed = false;

                if (!hit.Current.Flickable)
                {
                    // Chord keypress consumes any note type (Normal or Catch).
                    if (chordBudget > 0)
                    {
                        HitNote(hit, delta);
                        chordBudget--;
                        consumed = true;
                    }
                    // Held key clears catch notes (resting finger equivalent), gated by
                    // per-key cooldown unless the cursor intersects the note's hitbox.
                    else if (_HeldKeyCount > 0 && hit.Current.Type == HitObject.HitType.Catch
                             && CanHeldKeyClearCatch(hit))
                    {
                        HitNote(hit, delta, heldKey: _LastPressedKey);
                        consumed = true;
                    }
                }

                // Pass non-flickable catch notes to DiscreteHitQueue for timed auto-clear,
                // consistent with how the touch pipeline handles them.
                if (!consumed && isDiscrete && !hit.Current.Flickable && !hit.InDiscreteHitQueue)
                {
                    hit.InDiscreteHitQueue = true;
                    InputManager.DiscreteHitQueue.Add(hit);
                    InputManager.HitQueue.RemoveAt(a--);
                    continue;
                }

                if (!consumed && delta > window)
                {
                    Player.Hit(hit, float.PositiveInfinity, false);
                    hit.IsProcessed = true;
                    EnqueueHoldNote(hit, missed: true);
                }
            }

            if (delta < -Math.Max(Player.PassWindow, Player.GoodWindow)) break;
        }

        // ── Hold queue processor ──────────────────────────────────────────────
        // Verbatim mirror of PlayerInputManager's hold processor.
        // IsPlayerHolding is true when any key is currently held (_HeldKeyCount > 0).

        if (InputManager.HoldQueue.Count > 0)
        {
            float beat = PlayerScreen.sTargetSong.Timing.ToBeat((float)judgementOffsetTime);

            var currentCamera =
                (CameraController)PlayerScreen.sTargetChart.Data.Camera.GetStoryboardableObject(beat);

            Player.Pseudocamera.transform.position    = currentCamera.CameraPivot;
            Player.Pseudocamera.transform.eulerAngles = currentCamera.CameraRotation;
            Player.Pseudocamera.transform.Translate(Vector3.back * currentCamera.PivotDistance);

            for (int a = 0; a < InputManager.HoldQueue.Count; a++)
                HoldQueue_Processor(InputManager.HoldQueue[a], ref a, beat, judgementOffsetTime);
        }

        // ── Discrete queue processor ──────────────────────────────────────────
        // Catch notes auto-clear at their exact time, or when a key is held.

        for (int i = 0; i < InputManager.DiscreteHitQueue.Count; i++)
        {
            HitPlayer hit = InputManager.DiscreteHitQueue[i];

            if (hit.Current.Flickable)
            {
                hit.InDiscreteHitQueue = false;
                InputManager.DiscreteHitQueue.RemoveAt(i--);
                continue;
            }

            if (hit.Current.Type == HitObject.HitType.Catch)
            {
                double delta = judgementOffsetTime - hit.Time;

                bool inWindow  = judgementOffsetTime >= hit.Time;
                bool heldClear = _HeldKeyCount > 0 && Math.Abs(delta) <= Player.PassWindow
                                 && CanHeldKeyClearCatch(hit);

                if ((inWindow || heldClear) && !hit.IsProcessed)
                {
                    Player.Hit(hit, delta);
                    hit.InDiscreteHitQueue = false;
                    hit.IsProcessed        = true;
                    if (heldClear) RecordCatchCooldown(_LastPressedKey);
                    EnqueueHoldNote(hit);

                    if (!hit) continue;
                    InputManager.DiscreteHitQueue.RemoveAt(i--);
                }
            }
        }

        // ── Cue tick ──────────────────────────────────────────────────────────
        PCMouseOwnershipCueManager.sMain?.UpdateCue(Time.deltaTime);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Hold queue processor — mirrors PlayerInputManager.HoldQueue_Processor verbatim
    // ─────────────────────────────────────────────────────────────────────────────

    private void HoldQueue_Processor(HoldNoteClass holdNoteEntry, ref int queuePtr, float beat, double judgementOffsetTime)
    {
        if (!holdNoteEntry.HitObject)
        {
            NotifyHoldReleased(holdNoteEntry.HitObject);
            InputManager.HoldQueue.RemoveAt(queuePtr--);
            return;
        }

        var laneHoldNote = (Lane)holdNoteEntry.HitObject.Lane.Original.GetStoryboardableObject(beat);

        LanePosition step = laneHoldNote.GetLanePosition(beat, beat, PlayerScreen.sTargetSong.Timing);

        Vector3 startHoldPosition = laneHoldNote.Position + Quaternion.Euler(laneHoldNote.Rotation) * step.StartPosition;
        Vector3 endHoldPosition   = laneHoldNote.Position + Quaternion.Euler(laneHoldNote.Rotation) * step.EndPosition;

        LaneGroupPlayer currentHoldGroupPlayer = holdNoteEntry.HitObject.Lane.Group;

        while (currentHoldGroupPlayer)
        {
            var currentLaneGroup = (LaneGroup)currentHoldGroupPlayer.Original.GetStoryboardableObject(beat);
            startHoldPosition = currentLaneGroup.Position + Quaternion.Euler(currentLaneGroup.Rotation) * startHoldPosition;
            endHoldPosition   = currentLaneGroup.Position + Quaternion.Euler(currentLaneGroup.Rotation) * endHoldPosition;
            currentHoldGroupPlayer = currentHoldGroupPlayer.Parent;
        }

        var hitObject = (HitObject)holdNoteEntry.HitObject.Original.GetStoryboardableObject(beat);

        Vector3 holdNoteLerpStart = Vector3.LerpUnclamped(startHoldPosition, endHoldPosition, hitObject.Position);
        Vector3 holdNoteLerpEnd   = Vector3.LerpUnclamped(startHoldPosition, endHoldPosition, hitObject.Position + hitObject.Length);

        Vector2 holdNoteHitboxStart = Player.Pseudocamera.WorldToScreenPoint(holdNoteLerpStart);
        Vector2 holdNoteHitboxEnd   = Player.Pseudocamera.WorldToScreenPoint(holdNoteLerpEnd);

        holdNoteEntry.HitObject.HitCoord = new HitScreenCoord
        {
            Position = (holdNoteHitboxStart + holdNoteHitboxEnd) / 2,
            Radius   = Mathf.Max(
                Vector2.Distance(holdNoteHitboxStart, holdNoteHitboxEnd) / 2 + Player.ScaledExtraRadius,
                Player.ScaledMinimumRadius
            )
        };

        // IsPlayerHolding: any key held down counts, not a specific touch.
        holdNoteEntry.IsPlayerHolding = _HeldKeyCount > 0;

        holdNoteEntry.holdPassDrainValue = Mathf.Clamp01(
            holdNoteEntry.holdPassDrainValue + Time.deltaTime / Player.PassWindow * (holdNoteEntry.IsPlayerHolding ? 1f : -1f)
        );

        if (!holdNoteEntry.IsScoring && holdNoteEntry.holdPassDrainValue >= 1)
            holdNoteEntry.IsScoring = true;
        else if (holdNoteEntry.IsScoring && holdNoteEntry.holdPassDrainValue == 0)
            holdNoteEntry.IsScoring = false;

        while (holdNoteEntry.HitObject.HoldTicks.Count > 0 &&
               holdNoteEntry.HitObject.HoldTicks[0] <= judgementOffsetTime + float.Epsilon)
        {
            Player.AddScore(holdNoteEntry.IsScoring ? 1 : 0, null);

            Player.HitObjectHistory.Add(new HitObjectHistoryItem(
                holdNoteEntry.HitObject.HoldTicks[0],
                HitObjectHistoryType.Catch,
                holdNoteEntry.IsScoring ? 0 : float.PositiveInfinity
            ));

            holdNoteEntry.HitObject.HoldTicks.RemoveAt(0);

            if (holdNoteEntry.IsScoring)
            {
                Color interfaceColor = new Color(
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.r,
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.g,
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.b,
                    0.32f
                );
                var effect = PlayerScreen.sMain.JudgeScreenManager.BorrowEffect(holdNoteEntry.HitObject, null, interfaceColor);
                var rt     = (RectTransform)effect.transform;
                rt.position = Player.Pseudocamera.WorldToScreenPoint(holdNoteEntry.HitObject.transform.position);
            }
        }

        if (holdNoteEntry.HitObject.HoldTicks.Count <= 0)
        {
            NotifyHoldReleased(holdNoteEntry.HitObject);
            Player.RemoveHitPlayer(holdNoteEntry.HitObject);
            InputManager.HoldQueue.RemoveAt(queuePtr--);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <param name="heldKey">
    ///     If set, this hit was triggered by a held key rather than a new chord keydown.
    ///     A catch cooldown is recorded against this key after the hit.
    /// </param>
    private void HitNote(HitPlayer note, double delta, Key heldKey = Key.None)
    {
        Player.Hit(note, delta);
        note.IsProcessed = true;
        if (heldKey != Key.None && note.Current.Type == HitObject.HitType.Catch)
            RecordCatchCooldown(heldKey);
        EnqueueHoldNote(note);
    }

    private void EnqueueHoldNote(HitPlayer hitObject, bool missed = false)
    {
        if (!hitObject.PendingHoldQueue) return;

        InputManager.HoldQueue.Add(new HoldNoteClass
        {
            HitObject          = hitObject,
            holdPassDrainValue = missed ? 0 : 1,
            IsPlayerHolding    = _HeldKeyCount > 0,
        });

        // Register with mouse ownership queue if it needs the cursor.
        if (hitObject.Current.HoldLength > 0 || hitObject.Current.Flickable)
        {
            LaneStep laneStep = hitObject.Lane?.Current?.LaneSteps?.Count > 0
                ? hitObject.Lane.Current.LaneSteps[0] : null;
            EnqueueMouseCandidate(hitObject, laneStep);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mouse ownership queue
    // ─────────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────────
    // Catch throttle helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns true if a held key is permitted to clear <paramref name="note"/>.
    ///     False when the key is on cooldown AND the cursor does not intersect the note's
    ///     screen-space hitbox — i.e. it's far enough away to warrant a distinct input.
    /// </summary>
    private bool CanHeldKeyClearCatch(HitPlayer note)
    {
        Key key = _LastPressedKey;

        // No active cooldown for this key — always allow.
        if (!_CatchCooldownExpiry.TryGetValue(key, out double expiry)) return true;
        if (Player.CurrentTime >= expiry) return true;

        // Cooldown active — allow only if cursor intersects the note's hitbox.
        // The hitbox is a circle: Position (screen midpoint), Radius covering the full span.
        // This mirrors the straight-line two-point hitbox construction in HoldQueue_Processor.
        Vector2 cursorPos = Mouse.current != null
            ? Mouse.current.position.ReadValue()
            : note.HitCoord.Position; // No mouse — always intersects (safe fallback).

        return Vector2.Distance(cursorPos, note.HitCoord.Position) <= note.HitCoord.Radius;
    }

    /// <summary>
    ///     Records a catch cooldown for <paramref name="key"/>.
    ///     Timeout = 60 / BPM / 16 seconds (one 1/16th note at current tempo).
    /// </summary>
    private void RecordCatchCooldown(Key key)
    {
        if (key == Key.None) return;
        float bpm     = PlayerScreen.sTargetSong?.Timing.GetStop((float)Player.CurrentTime, out int _).BPM ?? 120f;
        float timeout = 60f / bpm / 16f;
        _CatchCooldownExpiry[key] = Player.CurrentTime + timeout;
    }

    private void EnqueueMouseCandidate(HitPlayer note, LaneStep laneStep)
    {
        if (note == null) return;

        var entry = new PCOwnershipEntry
        {
            Note        = note,
            LaneStep    = laneStep,
            EnqueueTime = Player.CurrentTime,
        };

        if (_OwnershipQueue.Count == 0)
        {
            _OwnershipQueue.Add(entry);
            GrantOwnership(entry);
            return;
        }

        if (!IsCursorOccupiedOrWarm())
        {
            _OwnershipQueue.Insert(0, entry);
            GrantOwnership(entry);
        }
        else
        {
            int idx = FindPriorityInsertIndex(entry);
            _OwnershipQueue.Insert(idx, entry);
            if (idx == 0) GrantOwnership(entry);
        }
    }

    private void NotifyHoldReleased(HitPlayer note)
    {
        if (note == null) return;
        int idx = _OwnershipQueue.FindIndex(e => e.Note == note);
        if (idx < 0) return;

        bool wasOwner = idx == 0;
        _OwnershipQueue.RemoveAt(idx);

        if (wasOwner)
        {
            _LastHoldReleaseTime = Player.CurrentTime;

            if (_OwnershipQueue.Count > 0)
                GrantOwnership(_OwnershipQueue[0]);
            else
                PCMouseOwnershipCueManager.sMain?.OnOwnerChanged(null, 0, 0);
        }
    }

    private void GrantOwnership(PCOwnershipEntry entry)
    {
        if (Mouse.current != null)
            Mouse.current.WarpCursorPosition(entry.Note.HitCoord.Position);

        float laneRot = GetLaneScreenRotation(entry.Note);
        float duration = GetApproachDuration(entry.Note);
        PCMouseOwnershipCueManager.sMain?.OnOwnerChanged(entry.Note, laneRot, duration);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Priority stack
    // ─────────────────────────────────────────────────────────────────────────────

    private bool IsCursorOccupiedOrWarm()
    {
        if (_OwnershipQueue.Count > 0) return true;
        float bpm    = PlayerScreen.sTargetSong?.Timing.GetStop((float)Player.CurrentTime, out int _).BPM ?? 120f;
        float window = bpm / 4f / 1000f;
        return (Player.CurrentTime - _LastHoldReleaseTime) <= window;
    }

    private int FindPriorityInsertIndex(PCOwnershipEntry newEntry)
    {
        for (int i = 0; i < _OwnershipQueue.Count; i++)
            if (CompareOwnershipPriority(newEntry, _OwnershipQueue[i]) > 0) return i;
        return _OwnershipQueue.Count;
    }

    private int CompareOwnershipPriority(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        int rA = GetEasingRank(a), rB = GetEasingRank(b);
        if (rA != rB) return rB - rA;

        float dA = SampleFloatDelta(a), dB = SampleFloatDelta(b);
        if (dA >= 0.2f || dB >= 0.2f) { int c = dA.CompareTo(dB); if (c != 0) return c; }

        int fc = CompareFlickSpecificity(a, b);
        if (fc != 0) return fc;

        Vector2 cursor = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        float   distA  = Vector2.Distance(cursor, a.Note?.HitCoord.Position ?? Vector2.zero);
        float   distB  = Vector2.Distance(cursor, b.Note?.HitCoord.Position ?? Vector2.zero);
        int     dc     = distB.CompareTo(distA);
        if (dc != 0) return dc;

        return a.EnqueueTime > b.EnqueueTime ? 1 : -1;
    }

    private int GetEasingRank(PCOwnershipEntry entry)
    {
        if (SampleFloatDelta(entry) < 0.001f) return -1;
        if (entry.LaneStep?.StartEaseX is BasicEaseDirective basic)
            for (int i = 0; i < sr_EasingRank.Length; i++)
                if (sr_EasingRank[i] == basic.Function) return i;
        return sr_EasingRank.Length;
    }

    private float SampleFloatDelta(PCOwnershipEntry entry)
    {
        if (entry.Note?.Original == null || entry.LaneStep == null) return 0f;
        const int SampleCount = 8; const float BeatWindow = 2f;
        float beat0      = PlayerScreen.sTargetSong?.Timing.ToBeat((float)Player.CurrentTime) ?? 0f;
        float laneLength = Vector2.Distance(entry.LaneStep.StartPointPosition, entry.LaneStep.EndPointPosition);
        float sum = 0f, prev = float.NaN;
        for (int i = 0; i <= SampleCount; i++)
        {
            float b = beat0 + (i / (float)SampleCount) * BeatWindow;
            float p; try { p = ((HitObject)entry.Note.Original.GetStoryboardableObject(b))?.Position ?? 0f; } catch { p = entry.Note.Current?.Position ?? 0f; }
            if (!float.IsNaN(prev)) sum += Mathf.Abs(p - prev) * laneLength;
            prev = p;
        }
        return sum;
    }

    private static int CompareFlickSpecificity(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        bool aD = a.Note != null && float.IsFinite(a.Note.Current?.FlickDirection ?? float.NaN);
        bool bD = b.Note != null && float.IsFinite(b.Note.Current?.FlickDirection ?? float.NaN);
        if (aD && !bD) return 1; if (!aD && bD) return -1;
        if (aD) { float d = Mathf.Abs(Mathf.DeltaAngle(a.Note.Current.FlickDirection, b.Note.Current.FlickDirection)); return d > 0 ? 1 : 0; }
        return 0;
    }

    private float GetLaneScreenRotation(HitPlayer note)
    {
        if (note?.Lane?.Current == null || Player?.Pseudocamera == null) return 0f;
        var steps = note.Lane.Current.LaneSteps;
        if (steps == null || steps.Count == 0) return 0f;
        // Use the note's world-space transform for direction — StartPointPosition is local.
        Vector2 s = Player.Pseudocamera.WorldToScreenPoint(note.transform.position);
        Vector2 e = Player.Pseudocamera.WorldToScreenPoint(note.transform.position + note.transform.right);
        Vector2 d = e - s;
        return d.sqrMagnitude > 0.001f ? Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg : 0f;
    }

    private float GetApproachDuration(HitPlayer note)
    {
        if (note?.Lane?.Current?.LaneSteps == null || note.Lane.Current.LaneSteps.Count == 0) return 0.5f;
        float rate = note.Lane.Current.LaneSteps[0].Speed * Player.Speed;
        return rate > 0f ? 1f / rate : 0.5f;
    }
}

/// <summary>Entry in the mouse ownership queue for one active hold or flick note.</summary>
public class PCOwnershipEntry
{
    public HitPlayer Note;
    public LaneStep  LaneStep;
    public double    EnqueueTime;
}
