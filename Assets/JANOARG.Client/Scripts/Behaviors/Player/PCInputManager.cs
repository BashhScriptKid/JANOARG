using System;
using System.Collections.Generic;
using System.Linq;
using JANOARG.Client.Behaviors.Player;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

/// <summary>
///     Keyboard + mouse hybrid input manager for JANOARG on PC.
///
///     <para><b>Keyboard — Milthm-style any-key chord pool.</b><br/>
///     No lane-to-key binding. Event propagation is layered:
///     <list type="number">
///       <item>System layer (pause, quit, screenshot …) — claims its keys first via <see cref="ConsumeKey"/>.</item>
///       <item>Game UI layer (menus, overlays) — claims what it needs.</item>
///       <item>This manager — receives whatever falls through.</item>
///     </list>
///     Keydowns buffered in a short chord-grouping window (<see cref="ChordGroupingWindowSec"/>).
///     When the window closes, <see cref="OnChord"/> fires with count N.
///     N simultaneous keydowns = N note hits, applied uniformly to Normal, Catch, and any mix.</para>
///
///     <para><b>Mouse — unified ownership queue.</b><br/>
///     All active hold and flick notes share a single <see cref="_OwnershipQueue"/>.
///     Base ordering: last-come-first-serve. The front owns the cursor; on every push/pop
///     the cursor teleports to the new owner's <see cref="HitPlayer.HitCoord"/> position
///     (already maintained each frame by <see cref="PlayerInputManager.HoldQueue_Processor"/>)
///     and <see cref="OnMouseOwnerChanged"/> fires.</para>
///
///     <para>When the cursor is <i>occupied</i> (a hold active) or <i>warm</i> (within the
///     BPM-scaled rolling window after a release), a new entry is evaluated against the
///     priority stack:
///     <list type="number">
///       <item>Easing type rank (Snap > Elastic > Expo > Circle > Back > Quintic > Quartic >
///             Cubic > Quadratic > Sine > Linear). Snap detected by near-zero float-delta variance.</item>
///       <item>Float delta (sum of <c>NotePosition × laneLength</c> samples over 2 beats; notes
///             below 0.2 fall through to distance).</item>
///       <item>Flick direction specificity (directional beats omni).</item>
///       <item>Shortest distance from current cursor screen position.</item>
///       <item>Last-come-first-serve fallback.</item>
///     </list></para>
///
///     <para><b>Wiring into <see cref="PlayerInputManager"/>:</b><br/>
///     - <see cref="PlayerInputManager.UpdateInput"/> calls <see cref="InjectChordInput"/> once
///       per chord event, which directly feeds the <see cref="PlayerInputManager.HitQueue"/>.<br/>
///     - <see cref="PlayerInputManager.EnqueueHoldNote"/> notifies this manager via
///       <see cref="EnqueueMouseCandidate"/> when a hold note enters the hold queue.<br/>
///     - <see cref="PlayerInputManager.HoldQueue_Processor"/> calls
///       <see cref="NotifyHoldReleased"/> when a hold note completes or is removed.<br/>
///     - Flick commits in <see cref="PlayerInputManager.HitobjectProcessor"/> call
///       <see cref="PushFlickToFront"/> for the brief cursor claim during flick resolution.</para>
/// </summary>
public class PCInputManager : MonoBehaviour
{
    // ─── Singleton ───────────────────────────────────────────────────────────────

    /// <summary>Singleton instance, consistent with the Chartmaker.cs pattern.</summary>
    public static PCInputManager sInstance;

    // ─── Inspector references ────────────────────────────────────────────────────

    [Header("References")]
    public PlayerScreen         Player;
    public PlayerInputManager   InputManager;

    /// <summary>Parent <see cref="RectTransform"/> for the <see cref="PCMouseOwnershipCue"/> overlay.</summary>
    public RectTransform        OwnershipCueContainer;

    /// <summary>Prefab for the ownership cursor indicator.</summary>
    public PCMouseOwnershipCue  OwnershipCuePrefab;

    // ─── Tuning ──────────────────────────────────────────────────────────────────

    [Header("Tuning")]
    [Tooltip(
        "Width of the chord-grouping window in seconds. Keydowns arriving within this window " +
        "are merged into a single chord event. Intentionally not coupled to any timing window — " +
        "left for gameplay tuning.")]
    public float ChordGroupingWindowSec = 0.020f;

    // ─── Events ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Fires once per chord-grouping window when ≥1 key was pressed.
    ///     <c>count</c> is the number of simultaneous keydowns in the chord.
    ///     Subscribers (typically <see cref="PlayerInputManager"/>) use this to drive hit resolution.
    /// </summary>
    public event Action<int> OnChord;

    /// <summary>
    ///     Fires when cursor ownership transfers to a new note or becomes free.
    ///     <c>newOwner</c> is <c>null</c> when the queue empties.
    /// </summary>
    public event Action<HitPlayer> OnMouseOwnerChanged;

    /// <summary>
    ///     Fires when a mouse flick gesture commits.
    ///     <c>directionDeg</c> is degrees CW from up; <c>float.NaN</c> = omnidirectional.
    /// </summary>
    public event Action<float> OnFlick;

    /// <summary>Fires when a hold note claims mouse ownership (entered the hold queue).</summary>
    public event Action<HitPlayer> OnHoldStart;

    /// <summary>Fires when a hold note's entry is removed from the ownership queue.</summary>
    public event Action<HitPlayer> OnHoldEnd;

    // ─── Easing rank table ───────────────────────────────────────────────────────

    /// <summary>
    ///     Priority rank by <see cref="EaseFunction"/>. Lower index = higher priority.
    ///     Snap is not an enum value — it is detected by near-zero float-delta variance in
    ///     <see cref="GetEasingRank"/> and returned as rank −1 (highest).
    /// </summary>
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

    // ─── Keyboard state ──────────────────────────────────────────────────────────

    private readonly List<Key>  _ChordBuffer     = new();
    private double              _ChordWindowStart = double.NegativeInfinity;
    private bool                _ChordWindowOpen;
    private readonly HashSet<Key> _ConsumedKeys  = new();

    // ─── Mouse / ownership state ─────────────────────────────────────────────────

    /// <summary>
    ///     Unified mouse ownership queue. Index 0 = current owner (front of queue).
    /// </summary>
    private readonly List<PCOwnershipEntry> _OwnershipQueue = new();

    /// <summary>
    ///     Game time (seconds) when the most-recent hold released ownership.
    ///     Used for the BPM-scaled rolling warm-window check.
    /// </summary>
    private double _LastHoldReleaseTime = double.NegativeInfinity;

    /// <summary>Active cue instance — one at a time, destroyed and re-instantiated on each transfer.</summary>
    private PCMouseOwnershipCue _ActiveCue;

    // ─── Raw input event delegate ────────────────────────────────────────────────

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
        if (_OnInputEvent != null)
            InputSystem.onEvent -= _OnInputEvent;
        sInstance = null;
    }

    private void Update()
    {
        FlushChordWindowIfExpired();
        TickOwnershipCue();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Keyboard — raw event handler
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Receives every raw keyboard event before Unity's action-map layer.
    ///     Keys consumed by upstream layers (via <see cref="ConsumeKey"/>) are skipped.
    /// </summary>
    private void HandleRawInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        if (device is not Keyboard keyboard) return;
        if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;

        foreach (KeyControl keyControl in keyboard.allControls.OfType<KeyControl>())
        {
            if (!keyControl.wasPressedThisFrame)                     continue;
            if (_ConsumedKeys.Contains(keyControl.keyCode))          continue;

            AcceptKeyDown(keyControl.keyCode);
        }
    }

    /// <summary>
    ///     Called by system/UI layers to prevent a key from reaching the chord pool.
    ///     Claims are cleared at the end of each frame in <see cref="FlushChordWindowIfExpired"/>.
    /// </summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    // ─────────────────────────────────────────────────────────────────────────────
    // Keyboard — chord pool
    // ─────────────────────────────────────────────────────────────────────────────

    private void AcceptKeyDown(Key key)
    {
        if (!_ChordWindowOpen)
        {
            _ChordWindowStart = Player.CurrentTime;
            _ChordWindowOpen  = true;
            _ChordBuffer.Clear();
        }
        _ChordBuffer.Add(key);
    }

    private void FlushChordWindowIfExpired()
    {
        _ConsumedKeys.Clear(); // Consumed-key claims expire each frame.

        if (!_ChordWindowOpen) return;
        if (Player.CurrentTime - _ChordWindowStart < ChordGroupingWindowSec) return;

        int count = _ChordBuffer.Count;
        _ChordBuffer.Clear();
        _ChordWindowOpen = false;

        if (count > 0)
        {
            OnChord?.Invoke(count);
            InjectChordInput(count);
        }
    }

    /// <summary>
    ///     Converts a chord of <paramref name="count"/> simultaneous keydowns into hit
    ///     attempts against <see cref="PlayerInputManager.HitQueue"/>.
    ///
    ///     Each keydown hits the next available note in the front of the queue that is
    ///     within the Good timing window and has not yet been processed. This mirrors the
    ///     touch path: one touch = one hit attempt, N touches = N hits.
    ///
    ///     Discrete notes (Catch, Flickable) are skipped here — they are handled by
    ///     their own queue in <see cref="PlayerInputManager"/>.
    /// </summary>
    private void InjectChordInput(int count)
    {
        if (InputManager == null) return;

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;
        int    hits                = 0;

        for (int q = 0; q < InputManager.HitQueue.Count && hits < count; q++)
        {
            HitPlayer note = InputManager.HitQueue[q];

            if (note == null || note.IsProcessed) continue;

            // Only Normal (tap) notes — discrete notes use their own queues.
            if (note.Current.Type != HitObject.HitType.Normal || note.Current.Flickable) continue;

            double delta = judgementOffsetTime - note.Time;
            if (delta < -Player.GoodWindow) break;    // Sorted by time; nothing further is in window.
            if (delta >  Player.GoodWindow) continue; // Too late (shouldn't happen for front of queue).

            Player.Hit(note, delta);
            note.IsProcessed = true;

            if (note.Current.HoldLength > 0)
                note.PendingHoldQueue = true;

            hits++;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mouse ownership queue — public API (called by PlayerInputManager)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Registers a hold note with the ownership queue when it enters
    ///     <see cref="PlayerInputManager.HoldQueue"/>.
    ///     Called from the <c>EnqueueHoldNote</c> path in <see cref="PlayerInputManager"/>.
    /// </summary>
    /// <param name="note">The hold <see cref="HitPlayer"/> entering the hold queue.</param>
    /// <param name="laneStep">
    ///     The lane's current active <see cref="LaneStep"/> at enqueue time, used for
    ///     easing-type rank and float-delta priority evaluation.
    /// </param>
    public void EnqueueMouseCandidate(HitPlayer note, LaneStep laneStep)
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
            OnHoldStart?.Invoke(note);
            return;
        }

        bool isCursorWarm = IsCursorOccupiedOrWarm();

        if (!isCursorWarm)
        {
            // Cursor free — LCFS: push to front.
            _OwnershipQueue.Insert(0, entry);
            GrantOwnership(entry);
        }
        else
        {
            // Cursor occupied/warm — insert by priority.
            int insertIndex = FindPriorityInsertIndex(entry);
            _OwnershipQueue.Insert(insertIndex, entry);
            if (insertIndex == 0) GrantOwnership(entry);
        }

        OnHoldStart?.Invoke(note);
    }

    /// <summary>
    ///     Removes a note from the ownership queue when the hold completes or is missed.
    ///     Called from <see cref="PlayerInputManager.HoldQueue_Processor"/> when removing
    ///     an entry from <see cref="PlayerInputManager.HoldQueue"/>.
    /// </summary>
    public void NotifyHoldReleased(HitPlayer note)
    {
        if (note == null) return;

        int idx = _OwnershipQueue.FindIndex(e => e.Note == note);
        if (idx < 0) return;

        bool wasOwner = idx == 0;
        _OwnershipQueue.RemoveAt(idx);

        OnHoldEnd?.Invoke(note);

        if (wasOwner)
        {
            _LastHoldReleaseTime = Player.CurrentTime;

            if (_OwnershipQueue.Count > 0)
                GrantOwnership(_OwnershipQueue[0]);
            else
                OnMouseOwnerChanged?.Invoke(null);
        }
    }

    /// <summary>
    ///     Temporarily pushes a flick note to the front of the ownership queue for the
    ///     brief cursor claim during flick resolution.
    ///     Called from <see cref="PlayerInputManager.HitobjectProcessor"/> when a flick commits.
    ///     The caller should call <see cref="NotifyHoldReleased"/> for the flick note once
    ///     the gesture is fully resolved so the previous owner re-inherits.
    /// </summary>
    public void PushFlickToFront(HitPlayer flickNote, LaneStep laneStep)
    {
        if (flickNote == null) return;

        // Remove if already in queue to avoid duplicates.
        int existing = _OwnershipQueue.FindIndex(e => e.Note == flickNote);
        if (existing >= 0) _OwnershipQueue.RemoveAt(existing);

        var entry = new PCOwnershipEntry
        {
            Note        = flickNote,
            LaneStep    = laneStep,
            EnqueueTime = Player.CurrentTime,
        };

        _OwnershipQueue.Insert(0, entry);
        GrantOwnership(entry);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Ownership transfer internals
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Grants ownership to <paramref name="entry"/>: teleports the OS cursor to the
    ///     note's <see cref="HitPlayer.HitCoord"/> screen position, fires
    ///     <see cref="OnMouseOwnerChanged"/>, and restarts the visual cue.
    ///
    ///     <see cref="HitPlayer.HitCoord"/> is already kept current each frame by
    ///     <see cref="PlayerInputManager.HoldQueue_Processor"/>, so the position is always
    ///     the up-to-date tail position at the moment of transfer.
    /// </summary>
    private void GrantOwnership(PCOwnershipEntry entry)
    {
        Vector2 screenPos = entry.Note != null
            ? entry.Note.HitCoord.Position
            : Vector2.zero;

        Mouse.current?.WarpCursorPosition(screenPos);
        RestartCue(entry, screenPos);
        OnMouseOwnerChanged?.Invoke(entry.Note);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Priority stack evaluation
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns true when the cursor is occupied (a hold is active) or warm
    ///     (within the BPM-scaled rolling window after the last hold released).
    ///
    ///     Rolling window formula per design spec: <c>BPM / 4 / 1000</c> seconds.
    /// </summary>
    private bool IsCursorOccupiedOrWarm()
    {
        if (_OwnershipQueue.Count > 0) return true; // Occupied.

        float bpm    = PlayerScreen.sTargetSong != null
            ? PlayerScreen.sTargetSong.Timing.GetStop((float)Player.CurrentTime, out int _).BPM
            : 120f;
        float window = bpm / 4f / 1000f;

        return (Player.CurrentTime - _LastHoldReleaseTime) <= window;
    }

    private int FindPriorityInsertIndex(PCOwnershipEntry newEntry)
    {
        for (int i = 0; i < _OwnershipQueue.Count; i++)
            if (CompareOwnershipPriority(newEntry, _OwnershipQueue[i]) > 0)
                return i;
        return _OwnershipQueue.Count;
    }

    /// <summary>
    ///     Compares two ownership candidates.
    ///     Returns &gt;0 if <paramref name="a"/> should rank ahead of <paramref name="b"/>.
    /// </summary>
    private int CompareOwnershipPriority(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        // 1. Easing type rank.
        int rankA = GetEasingRank(a);
        int rankB = GetEasingRank(b);
        if (rankA != rankB) return rankB - rankA; // Lower index = higher priority.

        // 2. Float delta tiebreaker.
        float deltaA = SampleFloatDelta(a);
        float deltaB = SampleFloatDelta(b);
        if (deltaA >= 0.2f || deltaB >= 0.2f)
        {
            int deltaCmp = deltaA.CompareTo(deltaB);
            if (deltaCmp != 0) return deltaCmp;
        }

        // 3. Flick direction specificity.
        int flickCmp = CompareFlickSpecificity(a, b);
        if (flickCmp != 0) return flickCmp;

        // 4. Shortest distance from current cursor position.
        Vector2 cursorPos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        float   distA     = a.Note != null ? Vector2.Distance(cursorPos, a.Note.HitCoord.Position) : float.MaxValue;
        float   distB     = b.Note != null ? Vector2.Distance(cursorPos, b.Note.HitCoord.Position) : float.MaxValue;
        int     distCmp   = distB.CompareTo(distA); // Closer = higher priority.
        if (distCmp != 0) return distCmp;

        // 5. LCFS fallback.
        return a.EnqueueTime > b.EnqueueTime ? 1 : -1;
    }

    /// <summary>
    ///     Returns the priority rank index for <paramref name="entry"/>'s lane step easing.
    ///     Uses <see cref="LaneStep.StartEaseX"/> as the representative easing axis.
    ///     Returns −1 for Snap (detected by near-zero float-delta variance).
    /// </summary>
    private int GetEasingRank(PCOwnershipEntry entry)
    {
        // Snap detection: note barely moves over 2 beats → treat as snap, highest priority.
        if (SampleFloatDelta(entry) < 0.001f) return -1;

        IEaseDirective easing = entry.LaneStep?.StartEaseX;
        if (easing is BasicEaseDirective basic)
        {
            for (int i = 0; i < sr_EasingRank.Length; i++)
                if (sr_EasingRank[i] == basic.Function) return i;
        }

        return sr_EasingRank.Length; // Unknown — lowest.
    }

    /// <summary>
    ///     Samples the note's 0–1 positional float delta accumulated over a 2-beat window,
    ///     as <c>NotePosition × laneLength</c> per design spec.
    ///     Higher value = faster-moving tail = higher priority in the delta tiebreaker.
    /// </summary>
    private float SampleFloatDelta(PCOwnershipEntry entry)
    {
        if (entry.Note?.Original == null || entry.LaneStep == null) return 0f;

        const int   SampleCount = 8;
        const float BeatWindow  = 2f;

        float beat0 = PlayerScreen.sTargetSong != null
            ? PlayerScreen.sTargetSong.Timing.ToBeat((float)Player.CurrentTime)
            : 0f;

        float laneLength = Vector2.Distance(
            entry.LaneStep.StartPointPosition,
            entry.LaneStep.EndPointPosition
        );

        float sumDelta = 0f;
        float prevPos  = float.NaN;

        for (int i = 0; i <= SampleCount; i++)
        {
            float beat    = beat0 + (i / (float)SampleCount) * BeatWindow;
            float notePos = SampleNotePosition(entry.Note, beat);

            if (!float.IsNaN(prevPos))
                sumDelta += Mathf.Abs(notePos - prevPos) * laneLength;

            prevPos = notePos;
        }

        return sumDelta;
    }

    private float SampleNotePosition(HitPlayer note, float beat)
    {
        try
        {
            return ((HitObject)note.Original.GetStoryboardableObject(beat))?.Position ?? 0f;
        }
        catch
        {
            return note.Current?.Position ?? 0f;
        }
    }

    private static int CompareFlickSpecificity(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        bool aDir = a.Note != null && float.IsFinite(a.Note.Current?.FlickDirection ?? float.NaN);
        bool bDir = b.Note != null && float.IsFinite(b.Note.Current?.FlickDirection ?? float.NaN);

        if (aDir && !bDir) return  1;
        if (!aDir && bDir) return -1;

        if (aDir) // Both directional — largest angular distance wins (more distinct).
        {
            float delta = Mathf.Abs(Mathf.DeltaAngle(
                a.Note.Current.FlickDirection,
                b.Note.Current.FlickDirection
            ));
            // A has higher angular distance from B → A is more distinct → A wins.
            return delta > 0 ? 1 : 0;
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Ownership cue
    // ─────────────────────────────────────────────────────────────────────────────

    private void RestartCue(PCOwnershipEntry entry, Vector2 screenPos)
    {
        if (OwnershipCuePrefab == null || OwnershipCueContainer == null) return;

        if (_ActiveCue != null)
        {
            Destroy(_ActiveCue.gameObject);
            _ActiveCue = null;
        }

        _ActiveCue = Instantiate(OwnershipCuePrefab, OwnershipCueContainer);
        _ActiveCue.Restart(entry.Note, screenPos, GetLaneScreenRotation(entry.Note), GetApproachDuration(entry.Note));
    }

    private void TickOwnershipCue()
    {
        if (_ActiveCue == null) return;
        _ActiveCue.Tick(Time.deltaTime);
        if (_ActiveCue.IsDone)
        {
            Destroy(_ActiveCue.gameObject);
            _ActiveCue = null;
        }
    }

    /// <summary>
    ///     Projects the lane's first step start→end vector through the pseudocamera
    ///     to get screen-space Z rotation in degrees.
    /// </summary>
    private float GetLaneScreenRotation(HitPlayer note)
    {
        if (note?.Lane?.Current == null || Player?.Pseudocamera == null) return 0f;

        var steps = note.Lane.Current.LaneSteps;
        if (steps == null || steps.Count == 0) return 0f;

        Vector2 screenStart = Player.Pseudocamera.WorldToScreenPoint(steps[0].StartPointPosition);
        Vector2 screenEnd   = Player.Pseudocamera.WorldToScreenPoint(steps[0].EndPointPosition);
        Vector2 dir         = screenEnd - screenStart;

        return dir.sqrMagnitude > 0.001f
            ? Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg
            : 0f;
    }

    /// <summary>
    ///     Derives an approach-time estimate (seconds) from the lane's current step speed,
    ///     mirroring the <see cref="LanePlayer"/> formula: position advances at
    ///     <c>step.Speed × PlayerScreen.Speed</c> beats per second.
    /// </summary>
    private float GetApproachDuration(HitPlayer note)
    {
        const float NominalApproachBeats = 1f;

        if (note?.Lane?.Current?.LaneSteps == null || note.Lane.Current.LaneSteps.Count == 0)
            return 0.5f;

        float rate = note.Lane.Current.LaneSteps[0].Speed * Player.Speed;
        return rate > 0f ? NominalApproachBeats / rate : 0.5f;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Supporting data
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>An entry in the mouse ownership queue for one active hold or flick note.</summary>
public class PCOwnershipEntry
{
    /// <summary>The note competing for / holding cursor ownership.</summary>
    public HitPlayer Note;

    /// <summary>
    ///     The lane's active <see cref="LaneStep"/> at enqueue time.
    ///     Used for easing-type rank (via <see cref="LaneStep.StartEaseX"/>) and
    ///     float-delta evaluation (via <see cref="LaneStep.StartPointPosition"/> /
    ///     <see cref="LaneStep.EndPointPosition"/> for lane length).
    /// </summary>
    public LaneStep LaneStep;

    /// <summary>
    ///     Game time (seconds) when this entry joined the queue.
    ///     LCFS tiebreaker: higher = later = higher LCFS priority.
    /// </summary>
    public double EnqueueTime;
}
