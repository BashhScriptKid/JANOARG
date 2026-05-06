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
///     <see cref="PlayerInputManager.DesktopInput"/> is true — direct replacement tick,
///     no <c>MonoBehaviour.Update</c>.</para>
///
///     <para><b>Architecture mirrors <see cref="PlayerInputManager"/> low-level flow:</b>
///     <list type="bullet">
///       <item>Each held key is a <see cref="KeyClass"/> (analogous to <c>TouchClass</c>),
///             persisting from keydown to keyup with its own state.</item>
///       <item>On keydown, <c>Initial = true</c> for one tick — the key scans the HitQueue
///             for the nearest note within hitbox and stores it as <c>QueuedHit</c>, exactly
///             like a new touch finger does. No chord budget; N simultaneous keypresses each
///             independently claim their note by proximity, which is how chords emerge.</item>
///       <item>Queued-hit resolver fires all <c>Initial</c> keys' <c>QueuedHit</c>s at the
///             end of the frame, matching the touch pipeline's resolver.</item>
///       <item>Held keys (not Initial) handle catch auto-clear via hitbox intersection +
///             per-key cooldown.</item>
///     </list></para>
/// </summary>
public class PCInputManager : MonoBehaviour
{
    public static PCInputManager sInstance;

    // ─── Inspector ───────────────────────────────────────────────────────────────

    [Header("References")]
    public PlayerScreen       Player;
    public PlayerInputManager InputManager;

    // ─── Keyboard state ──────────────────────────────────────────────────────────

    private readonly HashSet<Key>              _ConsumedKeys = new();

    /// <summary>
    ///     Live registry of all currently held keys, each wrapped in a <see cref="KeyClass"/>.
    ///     Mirrors <c>TouchClasses</c> in <see cref="PlayerInputManager"/>.
    /// </summary>
    public readonly Dictionary<Key, KeyClass> KeyClasses = new();

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
    // Unity lifecycle
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
    // Raw input — enumerate only changed controls, no per-event allocation
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
                var keyClass = new KeyClass
                {
                    KeyCode   = key,
                    PressTime = Player.CurrentTime,
                    Initial   = true,
                };
                KeyClasses[key] = keyClass;
            }
            else if (keyControl.wasReleasedThisFrame)
            {
                KeyClasses.Remove(key);
            }
        }
    }

    /// <summary>Called by upstream layers (system, UI) to block a key from reaching input.</summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    // ─────────────────────────────────────────────────────────────────────────────
    // Main tick — called by PlayerInputManager.UpdateInput() when DesktopInput = true
    // ─────────────────────────────────────────────────────────────────────────────

    public void UpdateInput()
    {
        _ConsumedKeys.Clear();

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        // ── HitQueue processor ────────────────────────────────────────────────

        for (int a = 0; a < InputManager.HitQueue.Count; a++)
        {
            HitPlayer hitobject = InputManager.HitQueue[a];

            if (!hitobject)
            {
                InputManager.HitQueue.RemoveAt(a--);
                continue;
            }

            double hitobjectTimingDelta = judgementOffsetTime - hitobject.Time;

            bool isDiscrete = hitobject.Current.Type == HitObject.HitType.Catch || hitobject.Current.Flickable;
            float window    = isDiscrete ? Player.PassWindow : Player.GoodWindow;

            if (hitobject.Current.HoldLength > 0 && !hitobject.PendingHoldQueue)
                hitobject.PendingHoldQueue = true;

            var alreadyHit = false;

            if (hitobjectTimingDelta >= -window && !hitobject.IsProcessed)
            {
                HitobjectProcessor(hitobject, hitobjectTimingDelta, ref alreadyHit);

                // Mark all keys as in-range of this discrete hitobject — no cursor gate,
                // key presence alone is the input signal.
                if (isDiscrete)
                {
                    foreach (KeyClass key in KeyClasses.Values)
                    {
                        key.DiscreteHitobjectIsInRange = true;
                        key.NearestDiscreteHitobject   = hitobject;
                    }
                }

                // Pass to DiscreteHitQueue
                if (hitobject.InDiscreteHitQueue ||
                    (alreadyHit && hitobject.Current.Type == HitObject.HitType.Catch))
                {
                    InputManager.DiscreteHitQueue.Add(hitobject);
                    hitobject.InDiscreteHitQueue = false;
                    InputManager.HitQueue.Remove(hitobject);
                    a--;
                }

                if (!alreadyHit && hitobjectTimingDelta > window)
                {
                    Player.Hit(hitobject, float.PositiveInfinity, false);
                    hitobject.IsProcessed = true;

                    foreach (KeyClass key in KeyClasses.Values)
                        if (key.QueuedHit == hitobject)
                        {
                            key.QueuedHit                  = null;
                            key.DiscreteHitobjectIsInRange = false;
                        }

                    EnqueueHoldNote(hitobject, missed: true);
                }
            }

            if (hitobjectTimingDelta < -Math.Max(Player.PassWindow, Player.GoodWindow)) break;
        }

        // ── Hold queue processor ──────────────────────────────────────────────

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
        // Catch notes: cleared at their time unconditionally (like the touch pipeline).
        // Additionally gated by per-key cooldown + hitbox intersection for held keys.

        for (int i = 0; i < InputManager.DiscreteHitQueue.Count; i++)
        {
            HitPlayer hitObject = InputManager.DiscreteHitQueue[i];

            double time = judgementOffsetTime - hitObject.Time;

            if (hitObject.Current.Flickable)
            {
                hitObject.InDiscreteHitQueue = false;
                InputManager.DiscreteHitQueue.Remove(hitObject);
                continue;
            }

            if (judgementOffsetTime >= hitObject.Time && hitObject.Current.Type == HitObject.HitType.Catch)
            {
                if (!hitObject.IsProcessed)
                    Player.Hit(hitObject, time);

                hitObject.InDiscreteHitQueue = false;
                hitObject.IsProcessed        = true;

                foreach (KeyClass key in KeyClasses.Values)
                    if (key.QueuedHit == hitObject)
                    {
                        key.QueuedHit                  = null;
                        key.DiscreteHitobjectIsInRange = false;
                    }

                EnqueueHoldNote(hitObject: hitObject);

                if (!hitObject) continue;
                InputManager.DiscreteHitQueue.Remove(hitObject);
            }
        }

        // ── Queued-hit resolver ───────────────────────────────────────────────
        // Fires QueuedHit for every Initial key — mirrors TouchClass queued-hit resolver.

        foreach (KeyClass key in KeyClasses.Values)
        {
            if (key.QueuedHit &&
                !key.QueuedHit.IsProcessed &&
                key.QueuedHit.Current.Type == HitObject.HitType.Normal &&
                !key.QueuedHit.Current.Flickable)
            {
                Player.Hit(
                    key.QueuedHit,
                    key.PressTime + Player.Settings.JudgmentOffset - key.QueuedHit.Time
                );
                key.QueuedHit.IsProcessed = true;
                EnqueueHoldNote(key.QueuedHit);
                key.QueuedHit         = null;
                key.QueuedHitDistance = 0;
            }

            key.Initial = false; // Initial only lasts one tick.
        }

        // ── Cue tick ──────────────────────────────────────────────────────────
        PCMouseOwnershipCueManager.sMain?.UpdateCue(Time.deltaTime);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HitobjectProcessor — mirrors PlayerInputManager.HitobjectProcessor
    // ─────────────────────────────────────────────────────────────────────────────

    private void HitobjectProcessor(HitPlayer hitobject, double hitobjectTimingDelta, ref bool alreadyHit)
    {
        // Flick notes — mouse handles these; keyboard is not involved.
        if (hitobject.Current.Flickable)
            return;

        switch (hitobject.Current.Type)
        {
            case HitObject.HitType.Normal:
                // Any Initial key claims the next note in timing window — purely key-based,
                // no cursor proximity. Discrete tap-protection is preserved (same logic as
                // touch pipeline) since a catch note arriving just before a tap note should
                // still gate correctly.
                foreach (KeyClass key in KeyClasses.Values)
                {
                    if (!key.Initial) continue;

                    var discreteTapProtectionPassed = false;

                    if ((discreteTapProtectionPassed =
                            !(key.DiscreteHitobjectIsInRange &&
                              key.NearestDiscreteHitobject != null &&
                              key.NearestDiscreteHitobject.Current.Type == HitObject.HitType.Catch &&
                              key.NearestDiscreteHitobject.Time < hitobject.Time &&
                              hitobject.Time >= -Player.GoodWindow &&
                              hitobject.Time - key.NearestDiscreteHitobject.Time <= Player.GoodWindow * 2
                            ) ||
                            (key.DiscreteHitobjectIsInRange &&
                             key.NearestDiscreteHitobject != null &&
                             (Math.Abs(hitobjectTimingDelta) <= Player.PerfectWindow ||
                              Mathf.Approximately(hitobject.Time, key.NearestDiscreteHitobject.Time) ||
                              Mathf.Approximately(
                                  Vector3.Distance(hitobject.HitCoord.Position,
                                                   key.NearestDiscreteHitobject.HitCoord.Position),
                                  hitobject.HitCoord.Radius / 2)
                             ))
                        ) &&
                        !key.QueuedHit // Each key claims at most one note per press.
                       )
                    {
                        key.QueuedHit = hitobject;
                        alreadyHit    = true;
                    }
                }
                return;

            case HitObject.HitType.Catch:
                // Catch: any held key within the cooldown gate can claim it.
                // No Initial check — resting keys clear catch notes too.
                foreach (KeyClass key in KeyClasses.Values)
                {
                    if (hitobject.InDiscreteHitQueue) break; // Already queued.

                    // Gate: per-key cooldown + cursor proximity.
                    if (!CanKeyClearCatch(key, hitobject)) continue;

                    hitobject.InDiscreteHitQueue   = true;
                    key.DiscreteHitobjectIsInRange = true;
                    key.CatchCooldownExpiry        = Player.CurrentTime + 60f / GetCurrentBPM() / 16f;
                    alreadyHit                     = true;
                }
                return;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HoldQueue_Processor — verbatim mirror of PlayerInputManager's
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

        // Hold: any key within the hitbox counts, mirroring touch AssignedTouch check.
        holdNoteEntry.IsPlayerHolding = KeyClasses.Count > 0 &&
            Vector2.Distance(CursorPos(), holdNoteEntry.HitObject.HitCoord.Position)
            <= holdNoteEntry.HitObject.HitCoord.Radius;

        holdNoteEntry.holdPassDrainValue = Mathf.Clamp01(
            holdNoteEntry.holdPassDrainValue + Time.deltaTime / Player.PassWindow
            * (holdNoteEntry.IsPlayerHolding ? 1f : -1f)
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

    private void EnqueueHoldNote(HitPlayer hitObject, bool missed = false)
    {
        if (!hitObject.PendingHoldQueue) return;

        InputManager.HoldQueue.Add(new HoldNoteClass
        {
            HitObject          = hitObject,
            holdPassDrainValue = missed ? 0 : 1,
            IsPlayerHolding    = KeyClasses.Count > 0,
        });

        if (hitObject.Current.HoldLength > 0 || hitObject.Current.Flickable)
        {
            LaneStep laneStep = hitObject.Lane?.Current?.LaneSteps?.Count > 0
                ? hitObject.Lane.Current.LaneSteps[0] : null;
            EnqueueMouseCandidate(hitObject, laneStep);
        }
    }

    /// <summary>
    ///     Returns the current cursor screen position. All hitbox checks use this
    ///     so the cursor is the single spatial reference point, equivalent to a
    ///     touch's <c>screenPosition</c>.
    /// </summary>
    private Vector2 CursorPos() =>
        Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

    /// <summary>
    ///     Per-key cooldown gate for catch notes.
    ///     A key on cooldown is only permitted to clear a catch note if the cursor
    ///     intersects the note's hitbox — meaning the notes are spatially close enough
    ///     that a single input covers them naturally.
    /// </summary>
    private bool CanKeyClearCatch(KeyClass key, HitPlayer note)
    {
        bool cooldownExpired  = Player.CurrentTime >= key.CatchCooldownExpiry;
        bool cursorIntersects = Vector2.Distance(CursorPos(), note.HitCoord.Position)
                                <= note.HitCoord.Radius;
        return cooldownExpired || cursorIntersects;
    }

    private float GetCurrentBPM() =>
        PlayerScreen.sTargetSong?.Timing.GetStop((float)Player.CurrentTime, out int _).BPM ?? 120f;

    // ─────────────────────────────────────────────────────────────────────────────
    // Mouse ownership queue
    // ─────────────────────────────────────────────────────────────────────────────

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
        Mouse.current?.WarpCursorPosition(entry.Note.HitCoord.Position);
        PCMouseOwnershipCueManager.sMain?.OnOwnerChanged(entry.Note, GetLaneScreenRotation(entry.Note), GetApproachDuration(entry.Note));
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

        Vector2 cursor = CursorPos();
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
///     Stateful wrapper for a single held keyboard key, mirroring <c>TouchClass</c>
///     in <see cref="PlayerInputManager"/>. Persists in <c>KeyClasses</c> from keydown
///     to keyup, carrying per-key QueuedHit and catch cooldown state.
/// </summary>
public class KeyClass
{
    /// <summary>The physical key this wrapper represents.</summary>
    public Key KeyCode;

    /// <summary>True only on the first tick after keydown — used to identify new presses.</summary>
    public bool Initial;

    /// <summary>Game time (seconds) when this key was pressed.</summary>
    public double PressTime;

    /// <summary>
    ///     The note this key has claimed to hit, resolved at end of frame.
    ///     Mirrors <c>TouchClass.QueuedHit</c>.
    /// </summary>
    public HitPlayer QueuedHit;

    /// <summary>Distance from cursor to the queued hit at claim time. Used for nearest-note selection.</summary>
    public float QueuedHitDistance;

    /// <summary>Whether the cursor is currently within a discrete hitobject's radius.</summary>
    public bool DiscreteHitobjectIsInRange;

    /// <summary>Distance to the nearest discrete hitobject. Used for tap protection.</summary>
    public float DiscreteHitobjectDistance;

    /// <summary>The nearest discrete hitobject to this key's cursor. Used for tap protection.</summary>
    public HitPlayer NearestDiscreteHitobject;

    /// <summary>
    ///     Game time (seconds) at which this key's catch cooldown expires.
    ///     <c>double.NegativeInfinity</c> = no cooldown active.
    ///     Timeout = 60 / BPM / 16 (one 1/16th note).
    /// </summary>
    public double CatchCooldownExpiry = double.NegativeInfinity;
}
