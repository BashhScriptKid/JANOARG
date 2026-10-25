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
    private bool TryResolveTapFlick(TouchClass touch, HitPlayer note, float flickDistanceThreshold)
    {
        if (!note || note.IsReturned || note.IsProcessed) return false;

        if (!touch.FlickTracker.IsFlicked) return false;

        // The tap only claims the note; the flick is the rhythmic action and is what gets judged,
        // so it has to land inside PassWindow. Outside it the claim is left standing rather than
        // consumed, so an early or late flick costs nothing and can simply be repeated until the
        // claim expires at GoodWindow.
        double flickTimingDelta = Player.CurrentTime + Player.Settings.JudgmentOffset - note.Time;

        if (Math.Abs(flickTimingDelta) > Player.PassWindow) return false;

        Vector2 current = touch.Touch.screenPosition;

        if (FlickTravel(touch, note) < flickDistanceThreshold)
            return false;

        Vector2 offset = current - note.HitCoord.Position;
        float radius = note.HitCoord.Radius;

        if (IsDirectionalFlick(note.Current.FlickDirection)) // Directional
        {
            // The beam runs indefinitely both ways, so containment only asks whether the finger is
            // still near the flick axis; which way it went is the angle check's job.
            if (FlickPerpendicular(note.Current.FlickDirection, offset) >= radius) return false;

            if (!ValidateFlickDirection(
                    note.Current.FlickDirection,
                    FlickTracker.AngleOf(FlickStrokeVector(touch, note))))
                return false;
        }
        else if (!note.IsScreenPointInHitBox(current, FlickFollowScale)) // Omnidirectional: expanded band
        {
            return false;
        }

        // Committed, not judged. The DiscreteHitQueue pass scores it once the note is within a
        // perfect window of its own time, and that pass runs later in this same frame — so a flick
        // landing inside the window resolves immediately, while an earlier one is held rather than
        // replying before the note reaches the line. Same path catch-flicks already take.
        note.InDiscreteHitQueue = true;

        touch.Flicked = false;
        touch.FlickTracker.ConsumeFlick();
        touch.QueuedHit = null; // claim discharged

        return true;
    }

        private void HitobjectProcessor(HitPlayer hitIteration, float flickDistanceThreshold, double hitobjectTimingDelta,
            ref bool alreadyHit)
    {
        if (hitIteration.Current.Flickable) // Flick notes (catch/tap)
        {
            // Tap-flicks are a two-stage gesture: the tap claims the note into the finger's
            // QueuedHit slot, and a flick resolves that claim. With per-event samples the flick can
            // already be latched on the same frame the tap is made, so stage 2 is attempted right
            // after a fresh claim below as well as on later frames — otherwise a flick that began and
            // ended inside one frame would be claimed and then swept unjudged.
            if (hitIteration.Current.Type == HitObject.HitType.Normal)
            {
                foreach (TouchClass touch in TouchClasses)
                {
                    // Stage 2 — this finger already claimed this note; see whether the flick lands.
                    if (touch.QueuedHit == hitIteration)
                    {
                        if (TryResolveTapFlick(touch, hitIteration, flickDistanceThreshold))
                            alreadyHit = true;

                        break;
                    }

                    // Stage 1 — claim it. The note's band, identical for directional and
                    // omnidirectional notes; direction governs only where the flick may travel,
                    // never where the tap may land.
                    if (!touch.Tapped) continue;

                    if (!hitIteration.IsScreenPointInHitBox(touch.Touch.startScreenPosition))
                        continue;

                    // A catch owning the touch suppresses this claim too — a flickable normal's tap is
                    // still a premature normal tap when the finger is there for the catch.
                    if (CatchSuppressesTap(touch, hitIteration, hitobjectTimingDelta))
                        continue;

                    float tapDistance = Vector2.Distance(
                        touch.Touch.startScreenPosition,
                        hitIteration.HitCoord.Position);

                    // Front-most note wins when boxes overlap (lower z = closer to the camera);
                    // distance only breaks an exact tie.
                    if (touch.QueuedHit &&
                        hitIteration.Frontness > touch.QueuedHit.Frontness &&
                        !(Mathf.Approximately((float)hitIteration.Frontness, (float)touch.QueuedHit.Frontness) &&
                          tapDistance < touch.QueuedHitDistance))
                        continue;

                    touch.QueuedHit = hitIteration;
                    touch.QueuedHitDistance = tapDistance;
                    hitIteration.IsTapped = true;
                    alreadyHit = true;

                    // A flick that is already latched (the whole gesture fitted in this one frame)
                    // can be resolved now rather than waiting for a frame that will never come.
                    if (TryResolveTapFlick(touch, hitIteration, flickDistanceThreshold))
                        alreadyHit = true;

                    break;
                }

                return;
            }

            // ── Catch-flicks: no tap to anchor to, so they stay single-stage. Behaviour is
            // unchanged; the branches removed from the verifier below were the Normal-only ones,
            // which were unreachable from here anyway.
            float distance = 0;

            foreach (TouchClass touch in TouchClasses)
            {
                distance = 0; // don't let a previous touch's value leak into this one

                // Bands overlap, so only the front-most catch for this touch may take the flick.
                if (hitIteration != touch.PriorityCatch) continue;

                if (touch.QueuedHit != null && !touch.Flicked) continue;

                // A pending tap-flick claim survives across frames. Only a catch in front of it may
                // steal the latched flick; otherwise the claim resolves it when its note comes round.
                if (touch.QueuedHit != null && touch.QueuedHit != hitIteration &&
                    touch.QueuedHit.Current.Flickable &&
                    touch.QueuedHit.Current.Type == HitObject.HitType.Normal &&
                    !touch.QueuedHit.IsProcessed &&
                    touch.QueuedHit.Frontness <= hitIteration.Frontness)
                    continue;

                if (!f_flickVerifier(hitIteration, touch)) continue;

                // Per feedback request, catch flicks are queued rather than hit on the spot
                hitIteration.InDiscreteHitQueue = true;

                touch.Flicked = false;
                touch.FlickTracker.ConsumeFlick();

                // Mark the touch as occupied by this hitobject for the rest of the frame instead of
                // freeing it — otherwise a second close-in-time/position note processed later in
                // this same HitQueue pass would pass the "touch.QueuedHit != null" guard above and
                // get hit by the same physical touch.
                touch.QueuedHit = hitIteration;
                touch.DiscreteHitobjectIsInRange = true;
                touch.DiscreteHitobjectDistance = distance;

                alreadyHit = true;

                break; // First valid touch wins
            }

            #region CATCH-FLICK LOCAL FUNCTION
            bool f_flickVerifier(HitPlayer hitObject, TouchClass touch)
            {
                // Containment, and unconditionally. This was gated on !touch.Flicked while a hit
                // required touch.Flicked, so the two were mutually exclusive and the check could
                // never affect the outcome — a flick anywhere on screen cleared the note. Either
                // end of the finger's travel counts, so a note swept through mid-flick still
                // registers.
                Vector2 center = hitObject.HitCoord.Position;
                float   radius = hitObject.HitCoord.Radius;

                float startDistance =
                    Vector2.Distance(touch.Touch.startScreenPosition, center);

                float currentDistance =
                    Vector2.Distance(touch.Touch.screenPosition, center);

                distance = Mathf.Min(startDistance, currentDistance);

                if (IsDirectionalFlick(hitObject.Current.FlickDirection))
                {
                    // Directional containment is the same beam the tap path uses, not a circle:
                    // either end of the stroke may sit on the note, but the finger has to stay near
                    // the flick axis.
                    float perpendicularStart = FlickPerpendicular(
                        hitObject.Current.FlickDirection, touch.Touch.startScreenPosition - center);

                    float perpendicularCurrent = FlickPerpendicular(
                        hitObject.Current.FlickDirection, touch.Touch.screenPosition - center);

                    if (perpendicularStart >= radius && perpendicularCurrent >= radius)
                        return false;
                }
                else
                {
                    // Follow expansion. Once the discrete-hitobject bookkeeping already believes this
                    // finger belongs to this note, widen the band so the flick's own travel cannot
                    // shake it off. That bookkeeping is set after HitobjectProcessor runs, so a note
                    // can only expand from the second frame a finger is on it — in range first, then
                    // it grows, which is the order we want anyway. Either end of the finger's travel
                    // counts, so a note swept through mid-flick still registers.
                    float followScale =
                        touch.DiscreteHitobjectIsInRange && touch.NearestDiscreteHitobject == hitObject
                            ? FlickFollowScale
                            : 1f;

                    bool startOnBand = hitObject.IsScreenPointInHitBox(
                        touch.Touch.startScreenPosition, followScale);

                    bool currentOnBand = hitObject.IsScreenPointInHitBox(
                        touch.Touch.screenPosition, followScale);

                    if (!startOnBand && !currentOnBand)
                        return false;
                }

                // With no tap frame to anchor to, the gesture is the entire confirmation.
                if (!touch.Flicked) return false;

                // Travel — the gate the tap path has always had and this one never did. Gated on
                // speed alone, a catch-flick only ever implied fireSpeed * dt of movement: about
                // 10px at 60fps, 2.5px at 240fps. A short sharp jab cleared the note, and it got
                // cheaper the faster the device rendered. Measured from where the finger engaged
                // this note, so each note in a stream demands its own movement rather than
                // inheriting a sweep's accumulated distance.
                if (FlickTravel(touch, hitObject) < flickDistanceThreshold)
                    return false;

                // Judge direction from the whole stroke (see FlickStrokeVector), not from the frozen
                // inter-sample delta that fired the tracker — that is a few pixels and its angle is
                // mostly touch jitter.
                if (IsDirectionalFlick(hitObject.Current.FlickDirection))
                    return ValidateFlickDirection(
                        hitObject.Current.FlickDirection,
                        FlickTracker.AngleOf(FlickStrokeVector(touch, hitObject)));

                return true;
            }
            #endregion

            return;
        }

        switch (hitIteration.Current.Type)
        {
            case HitObject.HitType.Normal:
                foreach (TouchClass touch in TouchClasses)
                {
                    if (!touch.Tapped) continue;

                    // The note's own baked band.
                    if (!hitIteration.IsScreenPointInHitBox(touch.Touch.screenPosition))
                        continue;

                    // A catch owning the touch blocks a nearby normal from triggering prematurely —
                    // the finger is there for the catch, not this note. See CatchSuppressesTap for the
                    // escapes.
                    if (CatchSuppressesTap(touch, hitIteration, hitobjectTimingDelta))
                        continue;

                    float distance = Vector2.Distance(
                        touch.Touch.screenPosition, hitIteration.HitCoord.Position);

                    if (
                        // Front-most note wins when boxes overlap (lower z = closer to the
                        // camera); distance only breaks an exact tie.
                        !touch.QueuedHit ||
                        hitIteration.Frontness < touch.QueuedHit.Frontness ||
                        (Mathf.Approximately((float)hitIteration.Frontness, (float)touch.QueuedHit.Frontness) &&
                         distance < touch.QueuedHitDistance)
                    )
                    {
                        touch.QueuedHit = hitIteration;
                        touch.QueuedHitDistance = distance;
                        alreadyHit = true;
                    }
                }

                return;

            case HitObject.HitType.Catch:
                foreach (TouchClass touch in TouchClasses)
                {
                    // The note's own baked band.
                    if (!hitIteration.IsScreenPointInHitBox(touch.Touch.screenPosition))
                        continue;

                    float distance = Vector2.Distance(touch.Touch.screenPosition, hitIteration.HitCoord.Position);

                    bool shouldAssign =
                        hitIteration == touch.PriorityCatch && // front-most overlapping catch wins
                        !hitIteration.InDiscreteHitQueue && // Slow down on the assigning, due to the nature of catch notes
                        touch.QueuedHit == null; // This touch is already occupied by another hitobject this frame

                    if (shouldAssign)
                    {
                        hitIteration.InDiscreteHitQueue = true;
                        touch.DiscreteHitobjectDistance = distance;
                        touch.DiscreteHitobjectIsInRange = true;
                        touch.QueuedHit = hitIteration; // Occupy this touch for the rest of the frame
                        touch.QueuedHitDistance = distance; // Keep the tie-break distance in sync with the occupancy
                        alreadyHit = true;
                    }
                }

                return;
        }
    }
        }
}
