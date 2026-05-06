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

    private readonly List<Key>      _ChordBuffer      = new();
    private double                  _ChordWindowStart = double.NegativeInfinity;
    private bool                    _ChordWindowOpen;
    private readonly HashSet<Key>   _ConsumedKeys     = new();

    /// <summary>
    ///     Live registry of all currently held keys, each wrapped in a <see cref="KeyClass"/>
    ///     that carries its own state — queued hit, catch cooldown expiry.
    ///     Mirrors the <c>TouchClasses</c> pattern in <see cref="PlayerInputManager"/>.
    /// </summary>
    private readonly Dictionary<Key, KeyClass> _HeldKeys = new();

    /// <summary>
    ///     Chord events buffered this tick: KeyClass wrappers for keys that were pressed
    ///     in the current grouping window. Flushed into the hit queue when the window closes.
    /// </summary>
    private readonly List<KeyClass> _PendingChord = new();

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
                var keyClass = new KeyClass { KeyCode = key, PressTime = Player.CurrentTime };
                _HeldKeys[key] = keyClass;
                BufferKeyDown(keyClass);
            }
            else if (keyControl.wasReleasedThisFrame)
            {
                _HeldKeys.Remove(key);
            }
        }
    }

    /// <summary>Called by upstream layers (system, UI) to prevent a key reaching the chord pool.</summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    private void BufferKeyDown(KeyClass keyClass)
    {
        if (!_ChordWindowOpen)
        {
            _ChordWindowStart = Player.CurrentTime;
            _ChordWindowOpen  = true;
            _ChordBuffer.Clear();
        }
        _ChordBuffer.Add(keyClass.KeyCode);
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
        _PendingChord.Clear();
        if (_ChordWindowOpen && Player.CurrentTime - _ChordWindowStart >= ChordGroupingWindowSec)
        {
            // Collect KeyClass wrappers for each key in the chord buffer.
            // Keys that were released before the window closed are gone from _HeldKeys
            // but we still want to count them — so we snapshot from _ChordBuffer.
            foreach (Key key in _ChordBuffer)
                if (_HeldKeys.TryGetValue(key, out KeyClass kc))
                    _PendingChord.Add(kc);
                else
                    _PendingChord.Add(new KeyClass { KeyCode = key, PressTime = _ChordWindowStart });

            _ChordBuffer.Clear();
            _ChordWindowOpen = false;
        }

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        // ── HitQueue processor ────────────────────────────────────────────────
        // Mirrors PlayerInputManager's HitQueue loop. N chord keys hit N notes;
        // held keys additionally clear catch notes as they arrive.

        int chordBudget = _PendingChord.Count;
        int chordIdx    = 0; // Tracks which KeyClass from _PendingChord consumed this slot.

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
                    // Chord keypress — each key in the chord independently claims one note.
                    if (chordBudget > 0)
                    {
                        KeyClass assignedKey = chordIdx < _PendingChord.Count
                            ? _PendingChord[chordIdx] : null;
                        HitNote(hit, delta, assignedKey);
                        chordBudget--;
                        chordIdx++;
                        consumed = true;
                    }
                    // Held key clears catch notes (resting finger equivalent), gated by
                    // per-key cooldown unless the cursor intersects the note's hitbox.
                    else if (_HeldKeys.Count > 0 && hit.Current.Type == HitObject.HitType.Catch)
                    {
                        KeyClass bestKey = FindBestHeldKeyForCatch(hit);
                        if (bestKey != null)
                        {
                            HitNote(hit, delta, bestKey);
                            consumed = true;
                        }
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
                KeyClass heldKeyForCatch = null;
                if (_HeldKeys.Count > 0 && Math.Abs(delta) <= Player.PassWindow)
                    heldKeyForCatch = FindBestHeldKeyForCatch(hit);
                bool heldClear = heldKeyForCatch != null;

                if ((inWindow || heldClear) && !hit.IsProcessed)
                {
                    Player.Hit(hit, delta);
                    hit.InDiscreteHitQueue = false;
                    hit.IsProcessed        = true;
                    if (heldClear) heldKeyForCatch.RecordCatchCooldown(Player.CurrentTime, GetCurrentBPM());
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

    /// <param name="key">
    ///     The <see cref="KeyClass"/> responsible for this hit. When the note is a catch
    ///     cleared by a held key (not a fresh chord press), a cooldown is recorded on it.
    ///     May be null for autoplay or missed-note forced hits.
    /// </param>
    private void HitNote(HitPlayer note, double delta, KeyClass key = null)
    {
        Player.Hit(note, delta);
        note.IsProcessed = true;
        // Record catch cooldown only for held-key clears (key already held, not a new press).
        // Chord presses don't need the cooldown — they're distinct inputs by definition.
        if (key != null && note.Current.Type == HitObject.HitType.Catch
                        && key.PressTime < _ChordWindowStart)
            key.RecordCatchCooldown(Player.CurrentTime, GetCurrentBPM());
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
    ///     Finds the best currently-held key to clear <paramref name="note"/> as a passive
    ///     hold-clear, or null if no key passes the cooldown/intersection gate.
    ///
    ///     Each held key is checked independently — a key on cooldown is only allowed if
    ///     the cursor intersects the note's hitbox (notes close enough to share one input).
    ///     Among eligible keys, prefer the one whose cooldown expired earliest (most "rested").
    /// </summary>
    private KeyClass FindBestHeldKeyForCatch(HitPlayer note)
    {
        Vector2 cursorPos = Mouse.current != null
            ? Mouse.current.position.ReadValue()
            : note.HitCoord.Position;

        bool cursorIntersects = Vector2.Distance(cursorPos, note.HitCoord.Position)
                                <= note.HitCoord.Radius;

        KeyClass best = null;
        double   bestExpiry = double.MaxValue;

        foreach (KeyClass kc in _HeldKeys.Values)
        {
            // Gate: cooldown expired, or cursor intersects hitbox.
            if (!kc.IsCatchCooldownExpired(Player.CurrentTime) && !cursorIntersects)
                continue;

            // Prefer whichever key has the earliest (most rested) expiry.
            double expiry = kc.CatchCooldownExpiry;
            if (best == null || expiry < bestExpiry)
            {
                best       = kc;
                bestExpiry = expiry;
            }
        }

        return best;
    }

    private float GetCurrentBPM() =>
        PlayerScreen.sTargetSong?.Timing.GetStop((float)Player.CurrentTime, out int _).BPM ?? 120f;

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

/// <summary>
///     Stateful wrapper for a single held keyboard key, mirroring the <c>TouchClass</c>
///     pattern in <see cref="PlayerInputManager"/>. Persists in <c>_HeldKeys</c> from
///     keydown to keyup, carrying per-key catch cooldown state.
/// </summary>
public class KeyClass
{
    /// <summary>The physical key this wrapper represents.</summary>
    public Key KeyCode;

    /// <summary>Game time (seconds) when this key was pressed.</summary>
    public double PressTime;

    /// <summary>
    ///     Game time (seconds) at which the catch cooldown for this key expires.
    ///     <see cref="double.NegativeInfinity"/> means no cooldown is active.
    /// </summary>
    public double CatchCooldownExpiry = double.NegativeInfinity;

    /// <summary>Returns true if the catch cooldown has expired at <paramref name="currentTime"/>.</summary>
    public bool IsCatchCooldownExpired(double currentTime) => currentTime >= CatchCooldownExpiry;

    /// <summary>
    ///     Stamps a catch cooldown on this key.
    ///     Timeout = 60 / BPM / 16 seconds (one 1/16th note at current tempo).
    /// </summary>
    public void RecordCatchCooldown(double currentTime, float bpm)
    {
        float timeout        = 60f / bpm / 16f;
        CatchCooldownExpiry  = currentTime + timeout;
    }
}
