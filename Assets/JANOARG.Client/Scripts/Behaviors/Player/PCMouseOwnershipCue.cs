using JANOARG.Client.UI;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;

namespace JANOARG.Client.Behaviors.Player
{
    /// <summary>
    ///     Canvas overlay that tracks mouse cursor ownership in the PC input system.
    ///     Placed on the judgement-line canvas, repositioned to the owning note's screen
    ///     position on each ownership transfer — the jump itself is the primary visual signal.
    ///
    ///     Shape: 4-sided <see cref="GraphicCircleGPU"/> polygon, rotated 25° relative to
    ///     the owning lane's screen-space orientation.
    ///
    ///     Animation phases (driven externally each frame by <see cref="PCInputManager.Update"/>):
    ///     <list type="number">
    ///       <item><b>Phase 1 [0..55%]</b> — ring <c>fillAmount</c> → 1, InOutExpo.
    ///             Duration derived from lane step speed.</item>
    ///       <item><b>Phase 2 [55%..75%]</b> — inner fill <c>fillAmount</c> → 1, OutCircle.</item>
    ///       <item><b>Phase 3 [75%..100%]</b> — <c>CanvasGroup.alpha</c> → 0, OutCubic.
    ///             Duration halved for discrete notes (catch / flick).</item>
    ///     </list>
    ///
    ///     On each ownership transfer the cue is restarted from scratch via
    ///     <see cref="Restart"/>. No pooling — one instance at a time.
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

        /// <summary>Whether the owning note is discrete (catch or flickable), which halves phase-3 duration.</summary>
        private bool _IsDiscrete;

        // ── Phase boundaries ─────────────────────────────────────────────────────

        private const float PhaseOneEnd = 0.55f;
        private const float PhaseTwoEnd = 0.75f;

        // ────────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Ring != null) { Ring.sides = 4; Ring.fillAmount = 0f; Ring.insideRadius = 0.65f; }
            if (Fill != null) { Fill.sides = 4; Fill.fillAmount = 0f; Fill.insideRadius = 0f; }
            if (Group != null) Group.alpha = 1f;
        }

        /// <summary>
        ///     Restarts the cue for a new owner.
        ///     Called by <see cref="PCInputManager"/> on every ownership transfer.
        /// </summary>
        /// <param name="owner">The note now owning the cursor.</param>
        /// <param name="screenPosition">Screen-space position to teleport to.</param>
        /// <param name="laneRotationDeg">
        ///     Screen-space Z rotation of the owning lane in degrees.
        ///     The cue polygon is rotated by +25° on top of this.
        /// </param>
        /// <param name="durationSec">Full animation duration in seconds.</param>
        public void Restart(HitPlayer owner, Vector2 screenPosition, float laneRotationDeg, float durationSec)
        {
            Owner    = owner;
            Progress = 0f;
            Duration = Mathf.Max(durationSec, 0.05f);

            _IsDiscrete = owner != null && (owner.Current.Type == HitObject.HitType.Catch || owner.Current.Flickable);

            ((RectTransform)transform).position    = screenPosition;
            transform.localEulerAngles             = new Vector3(0f, 0f, laneRotationDeg + 25f);

            if (Ring  != null) { Ring.fillAmount  = 0f; Ring.insideRadius = 0.65f; }
            if (Fill  != null) { Fill.fillAmount  = 0f; Fill.insideRadius = 0f;    }
            if (Group != null) Group.alpha = 1f;
        }

        /// <summary>
        ///     Advances the animation by <paramref name="deltaTime"/> seconds.
        ///     Called each frame by <see cref="PCInputManager"/> while this cue is active.
        /// </summary>
        public void Tick(float deltaTime)
        {
            Progress = Mathf.Clamp01(Progress + deltaTime / Duration);
            ApplyAnimation(Progress);
        }

        private void ApplyAnimation(float t)
        {
            // Phase 1: ring fill grows — InOutExpo
            if (Ring != null)
                Ring.fillAmount = t < PhaseOneEnd
                    ? Ease.Get(t / PhaseOneEnd, EaseFunction.Exponential, EaseMode.InOut)
                    : 1f;

            // Phase 2: inner fill grows — OutCircle
            // GraphicCircleGPU works the other way; 0 is thin outline, 1 is filled
            if (Fill != null)
            {
                if (t >= PhaseOneEnd && t < PhaseTwoEnd)
                    // Possibly nefficient but I'll leave this to you for optimisation
                    Fill.fillAmount = 1 - Ease.Get((t - PhaseOneEnd) / (PhaseTwoEnd - PhaseOneEnd), EaseFunction.Circle, EaseMode.Out);
                else if (t >= PhaseTwoEnd)
                    Fill.fillAmount = 1f;
            }

            // Phase 3: group alpha fades — OutCubic; halved for discrete notes
            if (Group != null && t >= PhaseTwoEnd)
            {
                float phase3Duration = _IsDiscrete ? (1f - PhaseTwoEnd) * 0.5f : (1f - PhaseTwoEnd);
                float p3             = Mathf.Clamp01((t - PhaseTwoEnd) / phase3Duration);
                Group.alpha = 1f - Ease.Get(p3, EaseFunction.Cubic, EaseMode.Out);
            }
        }

        /// <summary>Returns true once the animation has fully completed.</summary>
        public bool IsDone => Progress >= 1f;
    }
}
