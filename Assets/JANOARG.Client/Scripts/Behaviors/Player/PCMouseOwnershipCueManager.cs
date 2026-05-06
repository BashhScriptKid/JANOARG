using System.Collections.Generic;
using JANOARG.Client.Behaviors.Player;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;

namespace JANOARG.Client.Behaviors.Player
{
    /// <summary>
    ///     Self-contained canvas overlay manager for the PC mouse ownership cue.
    ///     Lives on its own GameObject with its own Canvas, analogous to
    ///     <see cref="PlayerHitboxVisualizer"/> — no dependency on PlayerScreen's canvas
    ///     hierarchy.
    ///
    ///     <see cref="PCInputManager.UpdateInput"/> ticks this each frame via
    ///     <see cref="UpdateCue"/>. The canvas is Screen Space - Overlay so it renders
    ///     on top of everything without a camera reference.
    /// </summary>
    public class PCMouseOwnershipCueManager : MonoBehaviour
    {
        public static PCMouseOwnershipCueManager sMain;

        /// <summary>Prefab to instantiate on each ownership transfer.</summary>
        public PCMouseOwnershipCue CuePrefab;

        /// <summary>RectTransform to parent cue instances under (should be this Canvas).</summary>
        public RectTransform CueRoot;

        private PCMouseOwnershipCue _ActiveCue;

        private void Awake() => sMain = this;

        /// <summary>
        ///     Spawns or restarts the cue for a new owner.
        ///     Called by <see cref="PCInputManager"/> on every ownership transfer.
        /// </summary>
        public void OnOwnerChanged(HitPlayer note, float laneRotationDeg, float durationSec)
        {
            if (CuePrefab == null || CueRoot == null) return;

            if (_ActiveCue != null)
            {
                Destroy(_ActiveCue.gameObject);
                _ActiveCue = null;
            }

            if (note == null) return; // Queue emptied — no new cue.

            Vector2 screenPos = note.HitCoord.Position;

            _ActiveCue = Instantiate(CuePrefab, CueRoot);
            _ActiveCue.Restart(note, screenPos, laneRotationDeg, durationSec);
        }

        /// <summary>
        ///     Advances the active cue animation and follows the owner's current screen position.
        ///     Called every frame by <see cref="PCInputManager.UpdateInput"/>.
        /// </summary>
        public void UpdateCue(float deltaTime)
        {
            if (_ActiveCue == null) return;

            // Track the owner's live HitCoord so the cue follows the hold tail.
            if (_ActiveCue.Owner != null)
                ((RectTransform)_ActiveCue.transform).position = _ActiveCue.Owner.HitCoord.Position;

            _ActiveCue.Tick(deltaTime);

            if (_ActiveCue.IsDone)
            {
                Destroy(_ActiveCue.gameObject);
                _ActiveCue = null;
            }
        }
    }
}
