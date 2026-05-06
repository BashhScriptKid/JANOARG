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
///     No lane-to-key binding. Keydowns buffered in a short chord-grouping window
///     (<see cref="ChordGroupingWindowSec"/>). When the window closes,
///     <see cref="InjectChordInput"/> fires and consumes notes from <see cref="PlayerInputManager.HitQueue"/>
///     — N simultaneous keydowns = N note hits, across Normal and Catch alike.<br/>
///     Additionally, <em>held</em> keys count as continuous input: any key currently held
///     down lets catch notes auto-clear as they enter their timing window, mirroring the
///     touch behaviour where a finger resting on the screen clears catch notes on arrival.</para>
///
///     <para><b>Mouse — unified ownership queue.</b><br/>
///     Only hold notes and flick notes claim the cursor. Plain taps never spawn the cue.
///     The cue position is derived from the note's world-space transform via the pseudocamera,
///     matching the pipeline used in <see cref="PlayerInputManager.HoldQueue_Processor"/>.</para>
/// </summary>
public class PCInputManager : MonoBehaviour
{
    // ─── Singleton ───────────────────────────────────────────────────────────────

    public static PCInputManager sInstance;

    // ─── Inspector ───────────────────────────────────────────────────────────────

    [Header("References")]
    public PlayerScreen       Player;
    public PlayerInputManager InputManager;

    public RectTransform      OwnershipCueContainer;
    public PCMouseOwnershipCue OwnershipCuePrefab;

    [Header("Tuning")]
    [Tooltip("Chord-grouping window in seconds. Keys arriving within this window are " +
             "merged into a single chord. Not coupled to any timing window — tune freely.")]
    public float ChordGroupingWindowSec = 0.020f;

    // ─── Events ──────────────────────────────────────────────────────────────────

    public event Action<int>       OnChord;
    public event Action<HitPlayer> OnMouseOwnerChanged;
    public event Action<float>     OnFlick;
    public event Action<HitPlayer> OnHoldStart;
    public event Action<HitPlayer> OnHoldEnd;

    // ─── Easing rank ─────────────────────────────────────────────────────────────

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

    private readonly List<Key>    _ChordBuffer      = new();
    private double                _ChordWindowStart = double.NegativeInfinity;
    private bool                  _ChordWindowOpen;
    private readonly HashSet<Key> _ConsumedKeys     = new();

    /// <summary>
    ///     Number of keys currently held down.  Incremented on keydown, decremented on
    ///     keyup, clamped to ≥ 0.  When > 0 the player is considered to be "holding"
    ///     and catch notes clear automatically as they enter their window, exactly like a
    ///     finger resting on the screen.
    /// </summary>
    private int _HeldKeyCount;

    // ─── Mouse / ownership state ─────────────────────────────────────────────────

    private readonly List<PCOwnershipEntry> _OwnershipQueue      = new();
    private double                          _LastHoldReleaseTime = double.NegativeInfinity;
    private PCMouseOwnershipCue             _ActiveCue;

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
        if (_OnInputEvent != null)
            InputSystem.onEvent -= _OnInputEvent;
        sInstance = null;
    }

    private void Update()
    {
        FlushChordWindowIfExpired();
        ProcessHeldKeys();
        TickOwnershipCue();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Raw input — keyboard
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Subscribes to raw input events to intercept keydowns before Unity's action-map
    ///     layer.  We only allocate inside <c>StateEvent</c> / <c>DeltaStateEvent</c> frames
    ///     for a keyboard device, and we iterate only changed controls via
    ///     <see cref="InputEventPtr.EnumerateChangedControls"/> to avoid the per-event
    ///     alloc that <c>allControls.OfType&lt;KeyControl&gt;()</c> causes.
    /// </summary>
    private void HandleRawInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        if (device is not Keyboard keyboard) return;
        if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;

        foreach (InputControl control in eventPtr.EnumerateChangedControls(device))
        {
            if (control is not KeyControl keyControl) continue;

            Key key = keyControl.keyCode;
            if (_ConsumedKeys.Contains(key)) continue;

            if (keyControl.wasPressedThisFrame)
            {
                _HeldKeyCount++;
                AcceptKeyDown(key);
            }
            else if (keyControl.wasReleasedThisFrame && _HeldKeyCount > 0)
            {
                _HeldKeyCount--;
            }
        }
    }

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
        _ConsumedKeys.Clear();

        if (!_ChordWindowOpen) return;
        if (Player.CurrentTime - _ChordWindowStart < ChordGroupingWindowSec) return;

        int count        = _ChordBuffer.Count;
        _ChordBuffer.Clear();
        _ChordWindowOpen = false;

        if (count > 0)
        {
            OnChord?.Invoke(count);
            InjectChordInput(count);
        }
    }

    /// <summary>
    ///     Converts N simultaneous keydowns into N note hits against
    ///     <see cref="PlayerInputManager.HitQueue"/>.
    ///
    ///     Note type does not restrict eligibility — Normal and Catch are both consumed
    ///     here, as many as <paramref name="count"/> allows. The rule is purely: how many
    ///     keys hit = how many notes cleared, regardless of type.
    ///
    ///     Flickable notes are excluded — they require a mouse gesture and are handled
    ///     separately.
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
            if (note.Current.Flickable) continue; // Flicks need mouse gesture.

            double delta = judgementOffsetTime - note.Time;
            if (delta < -Player.GoodWindow) break;  // Time-sorted; nothing further in window.
            if (delta >  Player.GoodWindow) continue;

            HitNote(note, delta, judgementOffsetTime);
            hits++;
        }

        // Also drain DiscreteHitQueue catch notes — simultaneous catch+normal combos.
        for (int q = 0; q < InputManager.DiscreteHitQueue.Count && hits < count; q++)
        {
            HitPlayer note = InputManager.DiscreteHitQueue[q];
            if (note == null || note.IsProcessed || note.Current.Flickable) continue;
            if (note.Current.Type != HitObject.HitType.Catch) continue;

            double delta = judgementOffsetTime - note.Time;
            if (Mathf.Abs((float)delta) > Player.PassWindow) continue;

            HitNote(note, delta, judgementOffsetTime);
            hits++;
        }
    }

    /// <summary>
    ///     Continuously clears catch notes that enter their window while any key is held,
    ///     mirroring the touch behaviour of a resting finger.  Called every frame.
    ///     Does not consume chord budget — this is a passive hold, not a new keypress.
    /// </summary>
    private void ProcessHeldKeys()
    {
        if (_HeldKeyCount <= 0 || InputManager == null) return;

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        // Check both HitQueue and DiscreteHitQueue for catch notes now in window.
        for (int q = 0; q < InputManager.HitQueue.Count; q++)
        {
            HitPlayer note = InputManager.HitQueue[q];
            if (note == null || note.IsProcessed) continue;
            if (note.Current.Type != HitObject.HitType.Catch || note.Current.Flickable) continue;

            double delta = judgementOffsetTime - note.Time;
            if (delta < -Player.PassWindow) break;
            if (delta >  Player.PassWindow) continue;

            HitNote(note, delta, judgementOffsetTime);
        }

        for (int q = 0; q < InputManager.DiscreteHitQueue.Count; q++)
        {
            HitPlayer note = InputManager.DiscreteHitQueue[q];
            if (note == null || note.IsProcessed) continue;
            if (note.Current.Type != HitObject.HitType.Catch || note.Current.Flickable) continue;

            double delta = judgementOffsetTime - note.Time;
            if (Mathf.Abs((float)delta) > Player.PassWindow) continue;

            HitNote(note, delta, judgementOffsetTime);
        }
    }

    /// <summary>
    ///     Hits a note and enqueues it into the hold queue if applicable.
    ///     Mirrors what <see cref="PlayerInputManager"/> does via the touch path.
    /// </summary>
    private void HitNote(HitPlayer note, double delta, double judgementOffsetTime)
    {
        Player.Hit(note, delta);
        note.IsProcessed = true;

        if (note.Current.HoldLength > 0)
            note.PendingHoldQueue = true;

        // Remove from whichever queue it lives in so the main processor doesn't double-handle it.
        InputManager.HitQueue.Remove(note);
        InputManager.DiscreteHitQueue.Remove(note);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mouse ownership queue — public API (called by PlayerInputManager)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Registers a hold or flickable note with the ownership queue.
    ///     Plain taps without hold length or flick are ignored — they don't need the cursor.
    /// </summary>
    public void EnqueueMouseCandidate(HitPlayer note, LaneStep laneStep)
    {
        if (note == null) return;

        // Only hold notes and flick notes get the cursor cue.
        bool needsCursor = note.Current.HoldLength > 0 || note.Current.Flickable;
        if (!needsCursor) return;

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

        if (!IsCursorOccupiedOrWarm())
        {
            _OwnershipQueue.Insert(0, entry);
            GrantOwnership(entry);
        }
        else
        {
            int insertIndex = FindPriorityInsertIndex(entry);
            _OwnershipQueue.Insert(insertIndex, entry);
            if (insertIndex == 0) GrantOwnership(entry);
        }

        OnHoldStart?.Invoke(note);
    }

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

    public void PushFlickToFront(HitPlayer flickNote, LaneStep laneStep)
    {
        if (flickNote == null) return;

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
    // Ownership transfer
    // ─────────────────────────────────────────────────────────────────────────────

    private void GrantOwnership(PCOwnershipEntry entry)
    {
        Vector2 screenPos = GetNoteScreenPosition(entry.Note);
        Mouse.current?.WarpCursorPosition(screenPos);
        RestartCue(entry, screenPos);
        OnMouseOwnerChanged?.Invoke(entry.Note);
    }

    /// <summary>
    ///     Computes the note's current screen-space position by running its world position
    ///     through <see cref="PlayerScreen.Pseudocamera"/>, matching the pipeline in
    ///     <see cref="PlayerInputManager.HoldQueue_Processor"/>.
    ///     Falls back to <see cref="HitPlayer.HitCoord"/> if already computed this frame.
    /// </summary>
    private Vector2 GetNoteScreenPosition(HitPlayer note)
    {
        if (note == null) return Vector2.zero;

        // HitCoord is updated every frame by HoldQueue_Processor for active holds.
        // For notes not yet in the hold queue (just enqueued) HitCoord may be stale,
        // so recompute from the note's GameObject world position via the pseudocamera.
        if (Player?.Pseudocamera != null && note.transform != null)
            return Player.Pseudocamera.WorldToScreenPoint(note.transform.position);

        return note.HitCoord.Position;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Priority stack
    // ─────────────────────────────────────────────────────────────────────────────

    private bool IsCursorOccupiedOrWarm()
    {
        if (_OwnershipQueue.Count > 0) return true;

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

    private int CompareOwnershipPriority(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        int rankA = GetEasingRank(a), rankB = GetEasingRank(b);
        if (rankA != rankB) return rankB - rankA;

        float deltaA = SampleFloatDelta(a), deltaB = SampleFloatDelta(b);
        if (deltaA >= 0.2f || deltaB >= 0.2f)
        {
            int c = deltaA.CompareTo(deltaB);
            if (c != 0) return c;
        }

        int flickCmp = CompareFlickSpecificity(a, b);
        if (flickCmp != 0) return flickCmp;

        Vector2 cursor = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        float   dA     = a.Note != null ? Vector2.Distance(cursor, GetNoteScreenPosition(a.Note)) : float.MaxValue;
        float   dB     = b.Note != null ? Vector2.Distance(cursor, GetNoteScreenPosition(b.Note)) : float.MaxValue;
        int     dCmp   = dB.CompareTo(dA);
        if (dCmp != 0) return dCmp;

        return a.EnqueueTime > b.EnqueueTime ? 1 : -1;
    }

    private int GetEasingRank(PCOwnershipEntry entry)
    {
        if (SampleFloatDelta(entry) < 0.001f) return -1; // Snap

        IEaseDirective easing = entry.LaneStep?.StartEaseX;
        if (easing is BasicEaseDirective basic)
            for (int i = 0; i < sr_EasingRank.Length; i++)
                if (sr_EasingRank[i] == basic.Function) return i;

        return sr_EasingRank.Length;
    }

    private float SampleFloatDelta(PCOwnershipEntry entry)
    {
        if (entry.Note?.Original == null || entry.LaneStep == null) return 0f;

        const int   SampleCount = 8;
        const float BeatWindow  = 2f;

        float beat0      = PlayerScreen.sTargetSong?.Timing.ToBeat((float)Player.CurrentTime) ?? 0f;
        float laneLength = Vector2.Distance(entry.LaneStep.StartPointPosition, entry.LaneStep.EndPointPosition);
        float sumDelta   = 0f;
        float prevPos    = float.NaN;

        for (int i = 0; i <= SampleCount; i++)
        {
            float beat    = beat0 + (i / (float)SampleCount) * BeatWindow;
            float notePos = SampleNotePosition(entry.Note, beat);
            if (!float.IsNaN(prevPos)) sumDelta += Mathf.Abs(notePos - prevPos) * laneLength;
            prevPos = notePos;
        }

        return sumDelta;
    }

    private float SampleNotePosition(HitPlayer note, float beat)
    {
        try   { return ((HitObject)note.Original.GetStoryboardableObject(beat))?.Position ?? 0f; }
        catch { return note.Current?.Position ?? 0f; }
    }

    private static int CompareFlickSpecificity(PCOwnershipEntry a, PCOwnershipEntry b)
    {
        bool aDir = a.Note != null && float.IsFinite(a.Note.Current?.FlickDirection ?? float.NaN);
        bool bDir = b.Note != null && float.IsFinite(b.Note.Current?.FlickDirection ?? float.NaN);
        if (aDir && !bDir) return  1;
        if (!aDir && bDir) return -1;
        if (aDir)
        {
            float delta = Mathf.Abs(Mathf.DeltaAngle(a.Note.Current.FlickDirection, b.Note.Current.FlickDirection));
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

        if (_ActiveCue != null) { Destroy(_ActiveCue.gameObject); _ActiveCue = null; }

        _ActiveCue = Instantiate(OwnershipCuePrefab, OwnershipCueContainer);
        _ActiveCue.Restart(entry.Note, screenPos, GetLaneScreenRotation(entry.Note), GetApproachDuration(entry.Note));
    }

    private void TickOwnershipCue()
    {
        if (_ActiveCue == null) return;
        _ActiveCue.Tick(Time.deltaTime);
        if (_ActiveCue.IsDone) { Destroy(_ActiveCue.gameObject); _ActiveCue = null; }
    }

    private float GetLaneScreenRotation(HitPlayer note)
    {
        if (note?.Lane?.Current == null || Player?.Pseudocamera == null) return 0f;

        var steps = note.Lane.Current.LaneSteps;
        if (steps == null || steps.Count == 0) return 0f;

        // StartPointPosition / EndPointPosition are local to the lane — we need to apply
        // lane + group transforms before projecting, just like HoldQueue_Processor does.
        // For rotation we only need the direction, so we can use the note's own transform
        // as a proxy: the lane's forward direction in screen space.
        Vector3 start = note.transform.position;
        Vector3 end   = start + note.transform.right; // approximate lane direction in world space

        Vector2 screenStart = Player.Pseudocamera.WorldToScreenPoint(start);
        Vector2 screenEnd   = Player.Pseudocamera.WorldToScreenPoint(end);
        Vector2 dir         = screenEnd - screenStart;

        return dir.sqrMagnitude > 0.001f ? Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg : 0f;
    }

    private float GetApproachDuration(HitPlayer note)
    {
        const float NominalApproachBeats = 1f;
        if (note?.Lane?.Current?.LaneSteps == null || note.Lane.Current.LaneSteps.Count == 0) return 0.5f;
        float rate = note.Lane.Current.LaneSteps[0].Speed * Player.Speed;
        return rate > 0f ? NominalApproachBeats / rate : 0.5f;
    }
}

/// <summary>An entry in the mouse ownership queue for one active hold or flick note.</summary>
public class PCOwnershipEntry
{
    public HitPlayer Note;
    public LaneStep  LaneStep;
    public double    EnqueueTime;
}
