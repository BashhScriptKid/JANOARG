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

    private readonly Dictionary<Key, PCKeyState> _Keys = new();
    private readonly HashSet<Key> _ConsumedKeys = new();
    private readonly HashSet<HitPlayer> _SnappedHoldHeads = new();
    private readonly List<Key> _KeysToRemove = new();

    private Action<InputEventPtr, InputDevice> _OnInputEvent;

    private void Awake()
    {
        sInstance = this;
        _OnInputEvent = HandleRawInputEvent;
        InputSystem.onEvent += _OnInputEvent;
    }

    private void OnDestroy()
    {
        if (_OnInputEvent != null)
            InputSystem.onEvent -= _OnInputEvent;

        if (sInstance == this)
            sInstance = null;
    }

    /// <summary>Called by upstream layers to block a key from reaching gameplay input.</summary>
    public void ConsumeKey(Key key) => _ConsumedKeys.Add(key);

    public void UpdateInput()
    {
        double judgementOffsetTime = Player.CurrentTime + Player.Settings.JudgmentOffset;

        ProcessHitQueue(judgementOffsetTime);
        ProcessDiscreteHitQueue(judgementOffsetTime);
        ResolveQueuedTapHits();
        ProcessHoldQueue(judgementOffsetTime);
        EndFrameKeyLifecycle();

        _ConsumedKeys.Clear();
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
            Mouse.current != null &&
            Vector2.Distance(Mouse.current.position.ReadValue(), holdNote.HitObject.HitCoord.Position) <=
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
    }

    private void SnapCursorToHoldOnce(HitPlayer hitObject)
    {
        if (_SnappedHoldHeads.Contains(hitObject))
            return;

        if (Mouse.current == null)
            return;

        Mouse.current.WarpCursorPosition(hitObject.HitCoord.Position);
        InputState.Change(Mouse.current.position, hitObject.HitCoord.Position);
        _SnappedHoldHeads.Add(hitObject);
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
}

public class PCKeyState
{
    public Key KeyCode;
    public bool Initial;
    public bool Released;
    public double PressTime;
    public HitPlayer QueuedHit;
}
