using System;
using System.Collections.Generic;
using System.Linq;
using JANOARG.Client.Behaviors.Common;
using JANOARG.Client.Behaviors.Player;
using JANOARG.Shared.Data.ChartInfo;
using Unity.Collections;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;


namespace JANOARG.Client.Behaviors.Player.PlayerInput.Core
{
public partial class PlayerInputEngine
{
    private void HoldQueue_Processor(HoldNoteClass holdNoteEntry, ref int queuePtr, float beat, double judgementOffsetTime)
    {
        if (!holdNoteEntry.HitObject || holdNoteEntry.HitObject.IsReturned)
        {
            holdNoteEntry.ReturnDedicatedEffects();
            HoldQueue.RemoveAt(queuePtr);
            queuePtr--; // Pointer rollback

            return;
        }

        holdNoteEntry.UpdateDedicatedEffects(Time.deltaTime);

        // Rebuild this hold's box against the current lane state every frame so it follows the lane
        // (and the hold head) while held, rather than staying at its start-beat bake. Same band rule
        // as taps/catches.
        UpdateHoldHitBox(holdNoteEntry.HitObject, beat);

        // Re-acquire the holding touch every frame; AssignedTouch must clear itself when the finger
        // leaves the band, lifts, or the band moves away, or the drain never decays and every tick
        // scores for free.
        holdNoteEntry.AssignedTouch = TouchClasses.Find(touch =>
            holdNoteEntry.HitObject.IsScreenPointInHitBox(touch.Touch.screenPosition));

        // Taking advantage of inline checks, since List<T>.Find() can give null
        holdNoteEntry.IsPlayerHolding = holdNoteEntry.AssignedTouch != null;

        // Update Drain value
        holdNoteEntry.holdPassDrainValue = Mathf.Clamp01( holdNoteEntry.holdPassDrainValue + Time.deltaTime / Player.PassWindow * (holdNoteEntry.IsPlayerHolding 
                ? 1f : -1f)
        );

        //Debug.Log($"Updating drain value: {holdNoteEntry.holdPassDrainValue}");


        // Check if the hold note is eligible for scoring
        if (!holdNoteEntry.IsScoring && holdNoteEntry.holdPassDrainValue >= 1)
            holdNoteEntry.IsScoring = true;
        else if (holdNoteEntry.IsScoring && holdNoteEntry.holdPassDrainValue == 0)
            holdNoteEntry.IsScoring = false;


        // Hold ticks processing
        while (holdNoteEntry.HitObject.HoldTicks.Count > 0 &&
               holdNoteEntry.HitObject.HoldTicks[0] <= judgementOffsetTime + float.Epsilon)
        {
            Player.AddScore(
                holdNoteEntry.IsScoring
                    ? 1 : 0,
                null
            );

            //Debug.Log(
            //    $"Hold tick at {holdNoteEntry.HitObject.HoldTicks[0]} processed. " +
            //    $"HoldEligible: {holdNoteEntry.IsScoring}, Score: {(holdNoteEntry.IsScoring ? 1 : 0)}");

            Player.HitObjectHistory.Add(
                new HitObjectHistoryItem( holdNoteEntry.HitObject.HoldTicks[0], HitObjectHistoryType.Catch, holdNoteEntry.IsScoring 
                        ? 0 : float .PositiveInfinity // Catch note-like hitobject history
                )
            );

            //Debug.Log(
            //    $"Hold tick at {holdNoteEntry.HitObject.HoldTicks[0]} removed from hold ticks list.");

            // Remove the first hold tick (and pretty much shift the next tick in)
            holdNoteEntry.HitObject.HoldTicks.RemoveAt(0);

            // Handle hold tick just like how HitPlayer does — reuses a small dedicated
            // pool of effects instead of borrowing/returning one per tick (see
            // HoldNoteClass.EnsureDedicatedEffects for why: at high BPM, ticks fire
            // far more often than a single effect's animation takes to finish).
            if (holdNoteEntry.IsScoring)
            {
                Color interfaceColor = new Color(PlayerScreen.sCurrentChart.Palette.InterfaceColor.r, PlayerScreen.sCurrentChart.Palette.InterfaceColor.g, PlayerScreen.sCurrentChart.Palette.InterfaceColor.b, 0.32f);
                holdNoteEntry.EnsureDedicatedEffects(interfaceColor);
                JudgeScreenEffect effect = holdNoteEntry.PulseDedicatedEffect();

                if (effect != null)
                {
                    var rt = (RectTransform)effect.transform;
                    rt.position = CommonSys.sMain.MainCamera.WorldToScreenPoint(holdNoteEntry.HitObject.transform.position);
                }
            }

            // Missed hold tick, no effect
        }

        if (holdNoteEntry.HitObject.HoldTicks.Count <= 0) // No hold ticks left
        {
            // RemoveHitPlayer already purges this entry from HoldQueue (see
            // PlayerInputEngine.PurgeHitPlayer) — an explicit RemoveAt(queuePtr) here would
            // now delete whatever shifted into that index instead, dropping an unrelated hold note.
            Player.RemoveHitPlayer(holdNoteEntry.HitObject);
            queuePtr--; // Pointer rollback
        }
    }

}
}
