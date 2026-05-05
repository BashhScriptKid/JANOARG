using JANOARG.Client.UI;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;

namespace JANOARG.Client.Behaviors.Player
{
    /// <summary>
    ///     Canvas overlay that tracks mouse cursor ownership in the PC input system.
    ///     Placed on the judgement-line canvas, repositioned to the owning note's centre
    ///     in screen space on each ownership transfer — the jump itself is the visual signal.
    ///
    ///     Shape: 4-sided <see cref="GraphicCircleGPU"/> polygon, rotated 25° relative to
    ///     the lane's world-space rotation projected into screen space.
    ///
    ///     Animation phases (driven externally by <see cref="PCInputManager.Update"/>):
    ///     <list type="number">
    ///       <item>Outlined radius fill → 1 as note approaches hit window — InOutExpo.
    ///             Duration derived from lane step speed.</item>
    ///       <item>Inner fill → 1 as window closes — OutCircle.</item>
    ///       <item>Alpha → 0 as misaligned window closes — OutCubic.
    ///             Duration halved if note is discrete (catch / flick).</item>
    ///     </list>
    ///
    ///     On each ownership transfer the cue is restarted from scratch via
    ///     <see cref="Restart"/>.
    /// </summary>
    public class PCMouseOwnershipCue : MonoBehaviour
    {
        // ── Shape components ────────────────────────────────────────────────────

        /// <summary>4-sided polygon background ring.</summary>
        public GraphicCircleGPU Ring;

        /// <summary>4-sided polygon inner fill.</summary>
        public GraphicCircleGPU Fill;

        /// <summary>CanvasGroup driving the overall alpha fade in phase 3.</summary>
        public CanvasGroup Group;

        // ── Runtime state ────────────────────────────────────────────────────────

        /// <summary>The note that currently owns the cursor.</summary>
        public HitPlayer Owner { get; private set; }

        /// <summary>
        ///     Linear progress in [0, 1] across the full animation lifetime.
        ///     Driven externally each frame by <see cref="PCInputManager"/>.
        /// </summary>
        public float Progress { get; private set; }

        /// <summary>Total animation duration in seconds, set from lane step speed on <see cref="Restart"/>.</summary>
        public float Duration { get; private set; }

        /// <summary>
        ///     Whether the owning note is discrete (catch or omnidirectional/directional flick).
        ///     Halves phase-3 duration per the design spec.
        /// </summary>
        private bool _IsDiscrete;

        // ── Phase boundary constants ─────────────────────────────────────────────

        // Phase 1: ring fill   [0 .. PhaseOnEnd)
        // Phase 2: inner fill  [PhaseOnEnd .. PhaseTwoEnd)
        // Phase 3: alpha fade  [PhaseTwoEnd .. 1]
        private const float PhaseOneEnd = 0.55f;
        private const float PhaseTwoEnd = 0.75f;

        // ────────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Ring != null)
            {
                Ring.sides = 4;
                Ring.fillAmount = 0f;
                Ring.insideRadius = 0.65f;
            }

            if (Fill != null)
            {
                Fill.sides = 4;
                Fill.fillAmount = 0f;
                Fill.insideRadius = 0f;
            }

            if (Group != null)
                Group.alpha = 1f;
        }

        /// <summary>
        ///     Restarts the cue for a new owner. Called by <see cref="PCInputManager"/>
        ///     on every ownership transfer.
        /// </summary>
        /// <param name="owner">The note now owning the cursor.</param>
        /// <param name="screenPosition">Screen-space position to teleport the cue to.</param>
        /// <param name="laneRotationDeg">
        ///     World-space rotation of the owning note's lane, projected to screen
        ///     orientation (Z-axis degrees). The cue polygon is rotated by +25° on top.
        /// </param>
        /// <param name="durationSec">
        ///     Full animation duration derived from the lane's step speed. Callers
        ///     should pass <c>1f / (stepSpeed * PlayerScreen.sMain.Speed)</c> or an
        ///     equivalent approach-time estimate so the ring fills just as the note
        ///     enters its hit window.
        /// </param>
        public void Restart(HitPlayer owner, Vector2 screenPosition, float laneRotationDeg, float durationSec)
        {
            Owner    = owner;
            Progress = 0f;
            Duration = Mathf.Max(durationSec, 0.05f);

            _IsDiscrete = owner != null &&
                          (owner.Current.Type == HitObject.HitType.Catch || owner.Current.Flickable);

            // Teleport to the owning note's screen position.
            ((RectTransform)transform).position = screenPosition;

            // Rotate polygon 25° relative to the lane's screen-space orientation.
            transform.localEulerAngles = new Vector3(0f, 0f, laneRotationDeg + 25f);

            // Reset visuals to phase-start state.
            if (Ring  != null) { Ring.fillAmount  = 0f; Ring.insideRadius  = 0.65f; }
            if (Fill  != null) { Fill.fillAmount  = 0f; Fill.insideRadius  = 0f;    }
            if (Group != null) Group.alpha = 1f;
        }

        /// <summary>
        ///     Advances the animation by <paramref name="deltaTime"/> seconds.
        ///     Called by <see cref="PCInputManager"/> each frame while this cue is active.
        /// </summary>
        public void Tick(float deltaTime)
        {
            Progress = Mathf.Clamp01(Progress + deltaTime / Duration);
            ApplyAnimation(Progress);
        }

        private void ApplyAnimation(float t)
        {
            // ── Phase 1: ring fill grows — InOutExpo ────────────────────────────
            if (t < PhaseOneEnd)
            {
                float p1 = t / PhaseOneEnd;
                float eased = Ease.Get(p1, EaseFunction.Exponential, EaseMode.InOut);
                if (Ring != null) Ring.fillAmount = eased;
            }
            else
            {
                if (Ring != null) Ring.fillAmount = 1f;
            }

            // ── Phase 2: inner fill grows — OutCircle ───────────────────────────
            if (t >= PhaseOneEnd && t < PhaseTwoEnd)
            {
                float p2 = (t - PhaseOneEnd) / (PhaseTwoEnd - PhaseOneEnd);
                float eased = Ease.Get(p2, EaseFunction.Circle, EaseMode.Out);
                if (Fill != null) Fill.fillAmount = eased;
            }
            else if (t >= PhaseTwoEnd)
            {
                if (Fill != null) Fill.fillAmount = 1f;
            }

            // ── Phase 3: group alpha fades out — OutCubic ───────────────────────
            // Duration is halved for discrete notes (catch / flick).
            if (t >= PhaseTwoEnd)
            {
                float phase3Duration = _IsDiscrete
                    ? (1f - PhaseTwoEnd) * 0.5f
                    : (1f - PhaseTwoEnd);

                float p3 = Mathf.Clamp01((t - PhaseTwoEnd) / phase3Duration);
                float eased = Ease.Get(p3, EaseFunction.Cubic, EaseMode.Out);
                if (Group != null) Group.alpha = 1f - eased;
            }
        }

        /// <summary>Returns true once the animation has fully completed.</summary>
        public bool IsDone => Progress >= 1f;
    }
}
