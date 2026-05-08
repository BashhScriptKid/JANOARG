using System;
using System.Collections.Generic;
using JANOARG.Client.Behaviors.Player;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

/// <summary>
///     PC keyboard input manager.
///
///     Rewrite roadmap note: this file is being rebuilt playtest-first. Iteration 1
///     implements single tap notes. Iteration 2 adds single catch notes. Holds are
///     currently under test. Chords, flicks, and ownership cues are deferred until
///     their playtest gate.
/// </summary>
public class PCInputManager : MonoBehaviour
{
    public static PCInputManager sInstance;

    [Header("References")]
    public PlayerScreen Player;
    public PlayerInputManager InputManager;

    public GameObject Cursor;

    private readonly Dictionary<Key, PCKeyState> _Keys = new();
    private readonly HashSet<Key> _ConsumedKeys = new();
    private readonly HashSet<HitPlayer> _SnappedHoldHeads = new();
    private readonly List<Key> _KeysToRemove = new();
    private readonly CursorVelocityTracker _FlickVelocityTracker = new();
    private Vector2 _CursorPosition;
    private bool _HasCursorPosition;
    private Vector2 _FlickCenter;
    private bool _HasFlickCenter;
    private bool _Flicked;
    private bool _IsGesturing;
    private double _FlickTime = double.NegativeInfinity;
    private float _FlickDirection = float.NaN;
    private bool _FlickCenterResetPending;
    private double _FlickCenterResetClock;
    private HitPlayer _FlickCenterSnappedNote;
    private bool _SystemCursorWasOutsideWindow;
    private int _CursorMotionSuppressionFrames;
    private CursorLockMode _PreviousLockState = CursorLockMode.None;

    private Action<InputEventPtr, InputDevice> _OnInputEvent;

    private void Awake()
    {
        sInstance = this;
        _OnInputEvent = HandleRawInputEvent;
        InputSystem.onEvent += _OnInputEvent;

        UnityEngine.Cursor.visible = false;
        CenterCursorOnStartup();
    }

    private void OnDestroy()
    {
        if (_OnInputEvent != null)
            InputSystem.onEvent -= _OnInputEvent;

        if (sInstance == this)
            sInstance = null;

        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = CursorLockMode.None;
    }

    /// <summary>Called by upstream layers to block a key from reaching gameplay input.</summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    public void UpdateInput()
    {
        UpdateCursorLockState();
        UpdateCursorState();
        UpdateFlickState();

        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        ProcessHitQueue(judgementOffsetTime);
        ProcessDiscreteHitQueue(judgementOffsetTime);
        ResolveQueuedTapHits();
        ProcessHoldQueue(judgementOffsetTime);
        EndFrameKeyLifecycle();

        _ConsumedKeys.Clear();
    }

    public void EndCursorControl()
    {
        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = CursorLockMode.None;

        if (Cursor != null)
            Cursor.SetActive(false);
    }

    private void HandleRawInputEvent(InputEventPtr eventPtr, InputDevice device)
    {
        if (device is not Keyboard) return;
        if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;

        foreach (InputControl control in eventPtr.EnumerateChangedControls(device))
        {
            if (control is not KeyControl keyControl) continue;

            Key key = keyControl.keyCode;
            if (_ConsumedKeys.Contains(key)) continue;
            if (!keyControl.ReadValueFromEvent(eventPtr, out float value)) continue;

            bool pressed = value > 0.5f;
            if (pressed)
                PressKey(key);
            else
                ReleaseKey(key);
        }
    }

    private void PressKey(Key key)
    {
        if (_Keys.ContainsKey(key)) return;

        _Keys[key] = new PCKeyState
        {
            KeyCode = key,
            Initial = true,
            PressTime = Player.CurrentTime,
            PressPosition = _CursorPosition,
        };
    }

    private void ReleaseKey(Key key)
    {
        if (_Keys.TryGetValue(key, out PCKeyState keyState))
            keyState.Released = true;
    }

    private void ProcessHitQueue(double judgementOffsetTime)
    {
        for (int i = 0; i < InputManager.HitQueue.Count; i++)
        {
            HitPlayer hitObject = InputManager.HitQueue[i];

            if (!hitObject)
            {
                InputManager.HitQueue.RemoveAt(i--);
                continue;
            }

            double timingDelta = judgementOffsetTime - hitObject.Time;
            if (timingDelta < -Math.Max(Player.PassWindow, Player.GoodWindow))
                break;

            if (hitObject.IsProcessed)
                continue;

            if (hitObject.Current.HoldLength > 0 && !hitObject.PendingHoldQueue)
                hitObject.PendingHoldQueue = true;

            if (hitObject.Current.Flickable)
            {
                if (TryProcessFlickHit(hitObject, timingDelta))
                {
                    InputManager.HitQueue.RemoveAt(i--);
                    continue;
                }

                if (timingDelta > Math.Max(Player.PassWindow, Player.GoodWindow))
                {
                    Player.Hit(hitObject, float.PositiveInfinity, false);
                    hitObject.IsProcessed = true;
                    ClearQueuedHit(hitObject);
                    EnqueueHoldNote(hitObject, missed: true);
                    InputManager.HitQueue.RemoveAt(i--);
                }

                continue;
            }

            bool isTap = IsTapHead(hitObject);
            bool isCatch = IsCatchHead(hitObject);

            if (!isTap && !isCatch)
                continue;

            float window = isCatch ? Player.PassWindow : Player.GoodWindow;
            if (timingDelta < -window)
                continue;

            bool alreadyHit = isCatch ? TryQueueCatchHit(hitObject) : TryQueueTapHit(hitObject);
            if (isCatch && hitObject.InDiscreteHitQueue)
            {
                InputManager.DiscreteHitQueue.Add(hitObject);
                hitObject.InDiscreteHitQueue = false;
                InputManager.HitQueue.RemoveAt(i--);
                continue;
            }

            if (!alreadyHit && timingDelta > window)
            {
                Player.Hit(hitObject, float.PositiveInfinity, false);
                hitObject.IsProcessed = true;
                ClearQueuedHit(hitObject);
                EnqueueHoldNote(hitObject, missed: true);
            }
        }
    }

    private bool TryQueueTapHit(HitPlayer hitObject)
    {
        bool alreadyHit = false;

        foreach (PCKeyState keyState in _Keys.Values)
        {
            if (!keyState.Initial || keyState.QueuedHit != null)
                continue;

            keyState.QueuedHit = hitObject;
            alreadyHit = true;
            break;
        }

        return alreadyHit;
    }

    private bool TryQueueCatchHit(HitPlayer hitObject)
    {
        if (!HasHeldKey())
            return false;

        hitObject.InDiscreteHitQueue = true;
        return true;
    }

    private bool TryProcessFlickHit(HitPlayer hitObject, double hitobjectTimingDelta)
    {
        float screenDpi = Screen.dpi > 0 ? Screen.dpi : 100f;
        float flickThreshold = GetFlickThreshold(screenDpi);

        switch (hitObject.Current.Type)
        {
            case HitObject.HitType.Normal:
            {
                PCKeyState bestKey = null;
                Vector2 bestTapStart = default;
                float bestTapStartDist = float.MaxValue;

                foreach (PCKeyState keyState in _Keys.Values)
                {
                    if (!keyState.Initial || keyState.QueuedHit != null)
                        continue;

                    float tapStartDist = Vector2.Distance(keyState.PressPosition, hitObject.HitCoord.Position);
                    if (tapStartDist >= bestTapStartDist)
                        continue;

                    bestKey = keyState;
                    bestTapStart = keyState.PressPosition;
                    bestTapStartDist = tapStartDist;
                }

                if (bestKey == null)
                    return false;

                if (!TapFlickVerifier(hitObject, bestTapStart, bestTapStartDist, flickThreshold))
                    return false;

                if (!hitObject.IsProcessed)
                    Player.Hit(hitObject, hitobjectTimingDelta);

                hitObject.IsProcessed = true;
                EnqueueHoldNote(hitObject);
                bestKey.QueuedHit = hitObject;
                ClearFlickState();
                return true;
            }

            case HitObject.HitType.Catch:
            {
                if (!HasHeldKey())
                    return false;

                if (!FlickVerifier(hitObject, flickThreshold))
                    return false;

                if (!hitObject.IsProcessed)
                    Player.Hit(hitObject, hitobjectTimingDelta);

                hitObject.IsProcessed = true;
                EnqueueHoldNote(hitObject);
                ClearFlickState();
                return true;
            }
        }

        return false;
    }

    private void ProcessDiscreteHitQueue(double judgementOffsetTime)
    {
        for (int i = 0; i < InputManager.DiscreteHitQueue.Count; i++)
        {
            HitPlayer hitObject = InputManager.DiscreteHitQueue[i];

            if (!hitObject)
            {
                InputManager.DiscreteHitQueue.RemoveAt(i--);
                continue;
            }

            if (!IsCatchHead(hitObject))
                continue;

            if (judgementOffsetTime < hitObject.Time)
                continue;

            if (!hitObject.IsProcessed)
                Player.Hit(hitObject, judgementOffsetTime - hitObject.Time);

            hitObject.InDiscreteHitQueue = false;
            hitObject.IsProcessed = true;
            EnqueueHoldNote(hitObject);
            InputManager.DiscreteHitQueue.RemoveAt(i--);
        }
    }

    private void ResolveQueuedTapHits()
    {
        foreach (PCKeyState keyState in _Keys.Values)
        {
            HitPlayer queuedHit = keyState.QueuedHit;
            if (!queuedHit || queuedHit.IsProcessed || !IsTapHead(queuedHit))
                continue;

            Player.Hit(
                queuedHit,
                keyState.PressTime + Player.Settings.JudgmentOffset - queuedHit.Time
            );

            queuedHit.IsProcessed = true;
            EnqueueHoldNote(queuedHit);
            keyState.QueuedHit = null;
        }
    }

    private void ProcessHoldQueue(double judgementOffsetTime)
    {
        if (InputManager.HoldQueue.Count == 0)
            return;

        float beat = PlayerScreen.sTargetSong.Timing.ToBeat((float)judgementOffsetTime);
        var currentCamera = (CameraController)PlayerScreen.sTargetChart.Data.Camera.GetStoryboardableObject(beat);

        Player.Pseudocamera.transform.position = currentCamera.CameraPivot;
        Player.Pseudocamera.transform.eulerAngles = currentCamera.CameraRotation;
        Player.Pseudocamera.transform.Translate(Vector3.back * currentCamera.PivotDistance);

        for (int i = 0; i < InputManager.HoldQueue.Count; i++)
            ProcessHoldNote(InputManager.HoldQueue[i], ref i, beat, judgementOffsetTime);
    }

    private void ProcessHoldNote(HoldNoteClass holdNote, ref int queueIndex, float beat, double judgementOffsetTime)
    {
        if (!holdNote.HitObject)
        {
            InputManager.HoldQueue.RemoveAt(queueIndex--);
            return;
        }

        var lane = (Lane)holdNote.HitObject.Lane.Original.GetStoryboardableObject(beat);
        LanePosition lanePosition = lane.GetLanePosition(beat, beat, PlayerScreen.sTargetSong.Timing);

        Vector3 startPosition = lane.Position + Quaternion.Euler(lane.Rotation) * lanePosition.StartPosition;
        Vector3 endPosition = lane.Position + Quaternion.Euler(lane.Rotation) * lanePosition.EndPosition;

        LaneGroupPlayer groupPlayer = holdNote.HitObject.Lane.Group;
        while (groupPlayer)
        {
            var group = (LaneGroup)groupPlayer.Original.GetStoryboardableObject(beat);
            startPosition = group.Position + Quaternion.Euler(group.Rotation) * startPosition;
            endPosition = group.Position + Quaternion.Euler(group.Rotation) * endPosition;
            groupPlayer = groupPlayer.Parent;
        }

        var hitObject = (HitObject)holdNote.HitObject.Original.GetStoryboardableObject(beat);
        Vector3 holdStart = Vector3.LerpUnclamped(startPosition, endPosition, hitObject.Position);
        Vector3 holdEnd = Vector3.LerpUnclamped(startPosition, endPosition, hitObject.Position + hitObject.Length);

        Vector2 screenStart = Player.Pseudocamera.WorldToScreenPoint(holdStart);
        Vector2 screenEnd = Player.Pseudocamera.WorldToScreenPoint(holdEnd);

        holdNote.HitObject.HitCoord = new HitScreenCoord
        {
            Position = (screenStart + screenEnd) / 2,
            Radius = Mathf.Max(
                Vector2.Distance(screenStart, screenEnd) / 2 + Player.ScaledExtraRadius,
                Player.ScaledMinimumRadius
            )
        };

        SnapCursorToHoldOnce(holdNote.HitObject);

        holdNote.IsPlayerHolding = HasHeldKey() &&
            Vector2.Distance(_CursorPosition, holdNote.HitObject.HitCoord.Position) <=
            holdNote.HitObject.HitCoord.Radius;

        holdNote.holdPassDrainValue = Mathf.Clamp01(
            holdNote.holdPassDrainValue + Time.deltaTime / Player.PassWindow *
            (holdNote.IsPlayerHolding ? 1f : -1f)
        );

        if (!holdNote.IsScoring && holdNote.holdPassDrainValue >= 1)
            holdNote.IsScoring = true;
        else if (holdNote.IsScoring && holdNote.holdPassDrainValue == 0)
            holdNote.IsScoring = false;

        while (holdNote.HitObject.HoldTicks.Count > 0 &&
               holdNote.HitObject.HoldTicks[0] <= judgementOffsetTime + float.Epsilon)
        {
            Player.AddScore(holdNote.IsScoring ? 1 : 0, null);
            Player.HitObjectHistory.Add(new HitObjectHistoryItem(
                holdNote.HitObject.HoldTicks[0],
                HitObjectHistoryType.Catch,
                holdNote.IsScoring ? 0 : float.PositiveInfinity
            ));

            holdNote.HitObject.HoldTicks.RemoveAt(0);

            if (holdNote.IsScoring)
            {
                Color interfaceColor = new(
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.r,
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.g,
                    PlayerScreen.sCurrentChart.Palette.InterfaceColor.b,
                    0.32f
                );
                var effect = PlayerScreen.sMain.JudgeScreenManager.BorrowEffect(holdNote.HitObject, null, interfaceColor);
                var rt = (RectTransform)effect.transform;
                rt.position = Player.Pseudocamera.WorldToScreenPoint(holdNote.HitObject.transform.position);
            }
        }

        if (holdNote.HitObject.HoldTicks.Count <= 0)
        {
            _SnappedHoldHeads.Remove(holdNote.HitObject);
            Player.RemoveHitPlayer(holdNote.HitObject);
            InputManager.HoldQueue.RemoveAt(queueIndex--);
        }
    }

    private void EndFrameKeyLifecycle()
    {
        _KeysToRemove.Clear();

        foreach (KeyValuePair<Key, PCKeyState> pair in _Keys)
        {
            pair.Value.Initial = false;

            if (pair.Value.QueuedHit != null && pair.Value.QueuedHit.Current.Flickable)
                pair.Value.QueuedHit = null;

            if (pair.Value.Released)
                _KeysToRemove.Add(pair.Key);
        }

        foreach (Key key in _KeysToRemove)
            _Keys.Remove(key);

        _KeysToRemove.Clear();
    }

    private void ClearQueuedHit(HitPlayer hitObject)
    {
        foreach (PCKeyState keyState in _Keys.Values)
            if (keyState.QueuedHit == hitObject)
                keyState.QueuedHit = null;
    }

    private void EnqueueHoldNote(HitPlayer hitObject, bool missed = false)
    {
        if (!hitObject.PendingHoldQueue)
            return;

        InputManager.HoldQueue.Add(new HoldNoteClass
        {
            HitObject = hitObject,
            IsPlayerHolding = HasHeldKey(),
            holdPassDrainValue = missed ? 0 : 1,
        });

        if (!missed)
            RecenterCursorForGameplay();
    }

    private void SnapCursorToHoldOnce(HitPlayer hitObject)
    {
        if (_SnappedHoldHeads.Contains(hitObject))
            return;

        // Preserve flick state during hold snaps — this is a positional correction,
        // not a user-initiated warp, so the velocity history remains valid.
        SetCursorPosition(hitObject.HitCoord.Position, resetFlickState: false);
        _SnappedHoldHeads.Add(hitObject);
    }

    private void CenterCursorOnStartup()
    {
        RecenterCursorForGameplay();
    }

    private void RecenterCursorForGameplay()
    {
        Vector2 center = new(
            Screen.width > 0 ? Screen.width * 0.5f : 0f,
            Screen.height > 0 ? Screen.height * 0.5f : 0f
        );

        SetCursorPosition(center);

        if (Mouse.current != null)
        {
            Mouse.current.WarpCursorPosition(center);
            InputState.Change(Mouse.current.position, center);
        }
    }

    private void UpdateCursorState()
    {
        if (Mouse.current == null)
            return;

        if (_CursorMotionSuppressionFrames > 0)
        {
            _CursorMotionSuppressionFrames--;
            SyncCursorVisual();
            return;
        }

        Vector2 systemCursorPosition = Mouse.current.position.ReadValue();
        bool systemCursorInsideWindow = IsCursorInsideWindow(systemCursorPosition);

        if (!_HasCursorPosition)
        {
            Vector2 startingPosition = systemCursorPosition;
            if (startingPosition == Vector2.zero && Screen.width > 0 && Screen.height > 0)
                startingPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            _CursorPosition = startingPosition;
            _HasCursorPosition = true;
            _FlickCenter = _CursorPosition;
            _HasFlickCenter = true;
            _FlickVelocityTracker.Reset();
        }
        else if (_SystemCursorWasOutsideWindow && systemCursorInsideWindow)
        {
            SetCursorPosition(systemCursorPosition);
            _SystemCursorWasOutsideWindow = false;
        }
        else
        {
            _CursorPosition += Mouse.current.delta.ReadValue();
        }

        if (!systemCursorInsideWindow)
            _SystemCursorWasOutsideWindow = true;

        if (_CursorPosition.x < 0f) _CursorPosition.x = 0f;
        if (_CursorPosition.y < 0f) _CursorPosition.y = 0f;
        if (Screen.width > 0 && _CursorPosition.x > Screen.width) _CursorPosition.x = Screen.width;
        if (Screen.height > 0 && _CursorPosition.y > Screen.height) _CursorPosition.y = Screen.height;

        SyncCursorVisual();
    }

    private void SetCursorPosition(Vector2 position, bool resetFlickState = true)
    {
        _CursorPosition = position;
        _HasCursorPosition = true;

        // Only reset FlickCenter and velocity history when this is an intentional
        // position warp (e.g. startup, re-entry from outside window). During hold
        // takeover snaps we preserve ongoing flick tracking so a hold → flick
        // transition isn't broken by the snap.
        if (resetFlickState)
        {
            _FlickCenter = position;
            _HasFlickCenter = true;
            _FlickVelocityTracker.Reset();
        }

        // Suppress delta reads for 2 frames: InputState.Change writes the new position
        // into the input system state but the OS cursor hasn't moved, so the next frame's
        // delta would be (oldOSPosition - newFakePosition), producing a spurious jump.
        _CursorMotionSuppressionFrames = Mathf.Max(_CursorMotionSuppressionFrames, 2);

        if (Mouse.current != null)
            InputState.Change(Mouse.current.position, position);

        SyncCursorVisual();
    }

    private void SyncCursorVisual()
    {
        if (Cursor == null) return;
        Cursor.transform.position = _CursorPosition;
    }

    private static bool IsCursorInsideWindow(Vector2 position) =>
        position.x >= 0f &&
        position.y >= 0f &&
        (Screen.width <= 0 || position.x <= Screen.width) &&
        (Screen.height <= 0 || position.y <= Screen.height);

    private void UpdateCursorLockState()
    {
        CursorLockMode targetLockState = ShouldLockCursor() ? CursorLockMode.Locked : CursorLockMode.None;

        if (UnityEngine.Cursor.lockState != targetLockState)
        {
            UnityEngine.Cursor.lockState = targetLockState;

            // When releasing from Locked, Unity has been feeding a fake position at screen
            // center and accumulates delta from there. On unlock, the OS cursor jumps back
            // to wherever it physically was, producing a large spurious delta on the next
            // frame (and sometimes the one after). Suppress motion reads for 2 frames to
            // swallow it.
            if (_PreviousLockState == CursorLockMode.Locked && targetLockState == CursorLockMode.None)
                _CursorMotionSuppressionFrames = Mathf.Max(_CursorMotionSuppressionFrames, 2);
        }

        _PreviousLockState = UnityEngine.Cursor.lockState;
    }

    private bool ShouldLockCursor()
    {
        if (InputManager == null)
            return false;

        if (InputManager.HoldQueue.Count > 0)
            return true;

        if (_Flicked || _IsGesturing)
            return true;

        for (int i = 0; i < InputManager.HitQueue.Count; i++)
        {
            HitPlayer hitObject = InputManager.HitQueue[i];
            if (!hitObject || hitObject.IsProcessed || !hitObject.Current.Flickable)
                continue;

            if (Math.Abs(hitObject.Time - Player.CurrentTime) <= Player.PassWindow * 2)
                return HasHeldKey();
        }

        return false;
    }

    private void UpdateFlickState()
    {
        if (Mouse.current == null)
            return;

        if (!_HasFlickCenter)
        {
            _FlickCenter = _CursorPosition;
            _HasFlickCenter = true;
        }

        float screenDpi = Screen.dpi > 0 ? Screen.dpi : 100f;
        float flickThreshold = GetFlickThreshold(screenDpi);
        float velocityThreshold = GetFlickVelocityThreshold(screenDpi);

        _FlickVelocityTracker.Push((float)Player.CurrentTime, _CursorPosition);
        float velocityMagnitude = _FlickVelocityTracker.Speed().magnitude;
        _IsGesturing = velocityMagnitude >= velocityThreshold;

        if (!_Flicked)
        {
            bool flickableApproaching = false;
            HitPlayer enteredNote = null;

            for (int i = 0; i < InputManager.HitQueue.Count; i++)
            {
                HitPlayer hitObject = InputManager.HitQueue[i];
                if (!hitObject || hitObject.IsProcessed || !hitObject.Current.Flickable)
                    continue;

                if (Math.Abs(hitObject.Time - Player.CurrentTime) <= Player.PassWindow * 2)
                    flickableApproaching = true;

                if (enteredNote == null &&
                    Vector2.Distance(_CursorPosition, hitObject.HitCoord.Position) <= hitObject.HitCoord.Radius)
                {
                    enteredNote = hitObject;
                }
            }

            if (flickableApproaching)
            {
                if (!_FlickCenterResetPending)
                {
                    _FlickCenterResetPending = true;
                    _FlickCenterResetClock = 0;
                }

                if (enteredNote != null && _FlickCenterSnappedNote != enteredNote)
                {
                    _FlickCenter = _CursorPosition;
                    _FlickCenterSnappedNote = enteredNote;
                    _FlickCenterResetClock = 0;
                }
            }
            else
            {
                _FlickCenterResetPending = false;
                _FlickCenterResetClock += Time.deltaTime;

                if (_FlickCenterResetClock >= 0.08f)
                {
                    _FlickCenter = _CursorPosition;
                    _FlickCenterSnappedNote = null;
                    _FlickCenterResetClock = 0;
                }
            }

            float flickDistance = Vector2.Distance(_CursorPosition, _FlickCenter);
            if (flickDistance >= flickThreshold / 2f)
                _FlickDirection = -Vector2.SignedAngle(Vector2.up, _CursorPosition - _FlickCenter);

            if (_IsGesturing && flickDistance >= flickThreshold)
            {
                _Flicked = true;
                _FlickTime = Player.CurrentTime;
            }
        }

        if (_Flicked)
        {
            bool flickTimedOut = Math.Abs(Player.CurrentTime - _FlickTime) > Player.PerfectWindow;
            bool nearAnyFlickable = false;

            for (int i = 0; i < InputManager.HitQueue.Count; i++)
            {
                HitPlayer hitObject = InputManager.HitQueue[i];
                if (!hitObject || hitObject.IsProcessed || !hitObject.Current.Flickable)
                    continue;

                if (Math.Abs(hitObject.Time - Player.CurrentTime) <= Player.PassWindow)
                {
                    nearAnyFlickable = true;
                    break;
                }
            }

            if (flickTimedOut && nearAnyFlickable)
                ClearFlickState();
        }
    }

    private bool TapFlickVerifier(HitPlayer hitObject, Vector2 tapStartPos, float tapStartDist, float flickThreshold)
    {
        bool positionValid;
        if (float.IsFinite(hitObject.Current.FlickDirection))
        {
            float corridorDist = Mathf.Abs(
                (Quaternion.Euler(0, 0, hitObject.Current.FlickDirection) *
                 (tapStartPos - hitObject.HitCoord.Position)).x);

            positionValid = corridorDist < hitObject.HitCoord.Radius + flickThreshold;
        }
        else
        {
            positionValid = tapStartDist < hitObject.HitCoord.Radius;
        }

        if (!positionValid)
            return false;

        return FlickVerifier(hitObject, flickThreshold, _FlickDirection);
    }

    private bool FlickVerifier(HitPlayer hitObject, float flickThreshold, float? angle = null)
    {
        if (!float.IsNaN(hitObject.Current.FlickDirection))
        {
            if (hitObject.Current.Type == HitObject.HitType.Normal && !_Flicked)
                return true;

            float calculatedAngle = angle ?? _FlickDirection;
            return ValidateFlickDirection(hitObject.Current.FlickDirection, calculatedAngle);
        }

        return hitObject.Current.Type == HitObject.HitType.Normal || _IsGesturing;
    }

    private static bool ValidateFlickDirection(float expected, float actual)
    {
        float absDiff = Mathf.Abs(Mathf.DeltaAngle(expected, actual));
        return absDiff <= 25f || absDiff <= 27.5f;
    }

    private static float GetFlickThreshold(float screenDpi)
    {
        float minDimension = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height));
        float dpiThreshold = screenDpi * 0.2f;
        float windowThreshold = minDimension * 0.04f;

        return Mathf.Max(dpiThreshold, windowThreshold);
    }

    private static float GetFlickVelocityThreshold(float screenDpi)
    {
        float minDimension = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height));
        float sizeFactor = Mathf.Clamp(1080f / minDimension, 1f, 2.5f);

        return 1.8f * (screenDpi / 275f) * screenDpi * sizeFactor;
    }

    private void ClearFlickState()
    {
        _Flicked = false;
        _FlickTime = double.NegativeInfinity;
        _FlickDirection = float.NaN;
        _IsGesturing = false;
        _FlickCenter = _CursorPosition;
        _FlickCenterSnappedNote = null;
        _FlickCenterResetPending = false;
        _FlickCenterResetClock = 0;
        _FlickVelocityTracker.Reset();
    }

    private bool HasHeldKey()
    {
        foreach (PCKeyState keyState in _Keys.Values)
            if (!keyState.Released)
                return true;

        return false;
    }

    private static bool IsTapHead(HitPlayer hitObject) =>
        hitObject.Current.Type == HitObject.HitType.Normal &&
        !hitObject.Current.Flickable;

    private static bool IsCatchHead(HitPlayer hitObject) =>
        hitObject.Current.Type == HitObject.HitType.Catch &&
        !hitObject.Current.Flickable;

    private sealed class CursorVelocityTracker
    {
        private const int RecordMax = 10;
        private readonly Queue<(float time, Vector2 position)> _Movements = new(RecordMax);

        public void Push(float time, Vector2 position)
        {
            if (_Movements.Count == RecordMax)
                _Movements.Dequeue();

            _Movements.Enqueue((time, position));
        }

        public void Reset() => _Movements.Clear();

        public Vector2 Speed()
        {
            if (_Movements.Count < 2)
                return Vector2.zero;

            float samples = _Movements.Count;
            float list = _Movements.Peek().time;

            float sumX = 0f, sumX2 = 0f, sumX3 = 0f, sumX4 = 0f;
            Vector2 sumY = Vector2.zero, sumXY = Vector2.zero, sumX2Y = Vector2.zero;

            foreach ((float time, Vector2 position) in _Movements)
            {
                float x = time - list;
                sumY += position;
                sumX += x;
                sumXY += x * position;

                float x2 = x * x;
                sumX2 += x2;
                sumX2Y += x2 * position;

                float x3 = x2 * x;
                sumX3 += x3;
                sumX4 += x3 * x;
            }

            float correctedX = sumX2 - sumX * sumX / samples;
            float correctedCrossProduct = sumX3 - sumX * sumX2 / samples;
            float correctedXSquared = sumX4 - sumX2 * sumX2 / samples;
            float determinant = correctedX * correctedXSquared - correctedCrossProduct * correctedCrossProduct;

            if (Mathf.Approximately(determinant, 0f))
                return Vector2.zero;

            Vector2 correctedCrossXY = sumXY - sumY * (sumX / samples);
            Vector2 correctedCrossX2Y = sumX2Y - sumY * (sumX2 / samples);

            return (correctedCrossXY * correctedXSquared - correctedCrossX2Y * correctedCrossProduct) / determinant;
        }
    }
}

public class PCKeyState
{
    public Key KeyCode;
    public bool Initial;
    public bool Released;
    public double PressTime;
    public Vector2 PressPosition;
    public HitPlayer QueuedHit;
}
