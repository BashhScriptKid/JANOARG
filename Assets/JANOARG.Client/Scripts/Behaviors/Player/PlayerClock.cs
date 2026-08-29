using System;

namespace JANOARG.Client.Behaviors.Player
{
    /// <summary>
    ///     Everything <see cref="PlayerScreen.Update" /> needs to decide, per frame, about the song clock:
    ///     where the clock is now, whether the audio source needs restarting, and whether the run is over.
    /// </summary>
    /// <remarks>
    ///     Deliberately free of any UnityEngine dependency so the decision can be exercised as a table of
    ///     synthetic frames — offsets, buffer wraps, lag spikes — without a device, a chart or four minutes
    ///     of audio. The bug this was extracted for only ever reproduced on one player's phone.
    /// </remarks>
    public struct PlayerClockFrame
    {
        /// <summary>AudioSettings.dspTime this frame.</summary>
        public double DspNow;

        /// <summary>AudioSettings.dspTime as of last frame.</summary>
        public double LastDspTime;

        /// <summary>Chart time (playback position + AudioOffset) as of last frame.</summary>
        public double PrevChartTime;

        /// <summary>Time.unscaledDeltaTime, used only when the DSP clock reports no progress.</summary>
        public double FrameDelta;

        /// <summary>AudioSource.timeSamples.</summary>
        public int TimeSamples;

        /// <summary>AudioClip.frequency.</summary>
        public int Frequency;

        /// <summary>AudioSource.isPlaying.</summary>
        public bool IsPlaying;

        /// <summary>AudioClip.length.</summary>
        public double ClipLength;

        /// <summary>The player's audio calibration offset, in seconds. Applies to chart time only.</summary>
        public double AudioOffset;

        /// <summary>
        ///     The DSP time at which the currently scheduled playback is due to finish, or NaN if nothing is
        ///     scheduled. This is what separates "the source dropped out" from "the source finished".
        /// </summary>
        public double SongEndDSP;

        /// <summary>
        ///     The DSP time at which chart time zero falls — i.e. when the song's first scheduled playback
        ///     begins. NaN before the run starts. Set once and never moved by a mid-song restart, so the
        ///     lead-in stays anchored to a single point rather than accumulating per-frame deltas.
        /// </summary>
        public double SongStartDSP;

        /// <summary>Latched by a previous frame — once the song has ended it never un-ends.</summary>
        public bool SongEnded;

        public bool ResultExec;

        public int HitsRemaining;

        public int HoldQueueCount;

        /// <summary>The widest judgment window, i.e. how long a note stays resolvable after its time.</summary>
        public double GoodWindow;
    }

    public struct PlayerClockDecision
    {
        /// <summary>Chart time: what notes, judgment and visuals are timed against. Offset applied.</summary>
        public double ChartTime;

        /// <summary>Raw playback position within the clip. No offset. What song progress is measured against.</summary>
        public double PlaybackTime;

        /// <summary>The audio source genuinely dropped out mid-song and should be rescheduled.</summary>
        public bool RestartAudio;

        /// <summary>Position to seek to when <see cref="RestartAudio" /> is set.</summary>
        public double RestartSeekTime;

        /// <summary>Latched end of playback. Carry back into the next frame's <see cref="PlayerClockFrame.SongEnded" />.</summary>
        public bool SongEnded;

        /// <summary>Hand off to the result screen.</summary>
        public bool TriggerResult;
    }

    public static class PlayerClock
    {
        /// <summary>Frame deltas above this are a lag spike, not elapsed song time.</summary>
        public const double SpikeThreshold = 0.1;

        /// <summary>
        ///     timeSamples resets to 0 when a non-looping source finishes, and there is a window where
        ///     isPlaying still reads true while it has already wrapped. A sample-derived position that moves
        ///     the clock backwards by more than this is that wrap — not a seek — and accepting it would
        ///     teleport the run back to the start of the song.
        /// </summary>
        public const double MaxBackwardJump = 0.25;

        public static PlayerClockDecision Advance(in PlayerClockFrame f)
        {
            double rawDelta     = f.DspNow - f.LastDspTime;
            double prevPlayback = f.PrevChartTime - f.AudioOffset;

            // The song finished on schedule. This is an exact question, not a proximity one: we scheduled
            // the playback ourselves, so we know precisely when it is due to end, on any device and at any
            // audio latency.
            bool scheduledEnded = !double.IsNaN(f.SongEndDSP) && f.DspNow >= f.SongEndDSP;
            bool ended          = f.SongEnded || scheduledEnded;

            // Lead-in: the source has not started yet, so the clock is simply how far we are from the
            // scheduled start. Anchored, not accumulated — at the moment of scheduling this reproduces the
            // serialized chart origin exactly, and it reaches 0 precisely when the audio begins, so the
            // handover to sample-derived time is continuous with nothing to snap.
            bool leadIn = !double.IsNaN(f.SongStartDSP) && f.DspNow < f.SongStartDSP;

            if (leadIn)
                return new PlayerClockDecision
                {
                    PlaybackTime    = f.DspNow - f.SongStartDSP,
                    ChartTime       = f.DspNow - f.SongStartDSP + f.AudioOffset,
                    RestartAudio    = false,
                    RestartSeekTime = 0,
                    SongEnded       = ended,
                    TriggerResult   = false,
                };

            double sampleTime   = f.Frequency > 0 ? (double)f.TimeSamples / f.Frequency : 0;
            bool   sampleUsable = f.IsPlaying && f.PrevChartTime >= 0 && f.Frequency > 0;

            if (sampleUsable && sampleTime < prevPlayback - MaxBackwardJump)
            {
                // Wrapped, not seeked. Fall through to the free-running clock and latch the end.
                sampleUsable = false;
                ended        = true;
            }

            double newPlayback;

            if (rawDelta > SpikeThreshold)
                // Lag spike — resync to the audio if we still trust it, otherwise drop the frame rather
                // than rushing the chart forward by the whole stall.
                newPlayback = sampleUsable ? sampleTime : prevPlayback;
            else if (sampleUsable)
                // Audio is the source of truth during playback.
                newPlayback = sampleTime;
            else
                newPlayback = prevPlayback + (rawDelta > 0 ? rawDelta : f.FrameDelta);

            // Only restart a source that stopped *before* its scheduled end. Past that point it did not
            // drop out, it finished, and rescheduling it is what loops the song.
            bool restart = f is { IsPlaying: false, ResultExec: false }
                           && !ended
                           && newPlayback >= 0
                           && newPlayback < f.ClipLength;

            return new PlayerClockDecision
            {
                PlaybackTime    = newPlayback,
                ChartTime       = newPlayback + f.AudioOffset,
                RestartAudio    = restart,
                RestartSeekTime = newPlayback,
                SongEnded       = ended,
                TriggerResult = ShouldTriggerResult(
                    newPlayback,
                    f.ClipLength,
                    f.GoodWindow,
                    ended,
                    f.HitsRemaining,
                    f.HoldQueueCount,
                    f.ResultExec),
            };
        }

        /// <summary>
        ///     Whether the run is over. Split out from <see cref="Advance" /> because the hit counts it reads
        ///     are only final after input has been processed for the frame, whereas the clock and the audio
        ///     lifecycle have to be settled before input runs.
        /// </summary>
        public static bool ShouldTriggerResult(
            double playbackTime,
            double clipLength,
            double goodWindow,
            bool   songEnded,
            int    hitsRemaining,
            int    holdQueueCount,
            bool   resultExec)
        {
            if (resultExec || playbackTime <= 0)
                return false;

            // The chart ran out of notes before the audio did — the common case. The ending animation
            // handles waiting for the music to finish from here.
            if (hitsRemaining <= 0 && holdQueueCount == 0)
                return true;

            // Otherwise the audio has finished with notes still pending. A note sitting at the very end of
            // the clip is only resolvable once the clock has run past it by the judgment window, which is
            // after the audio stopped; firing the instant the audio ends would strand those notes unjudged
            // and report a short score. Bounded, so a stranded hold cannot hold the run open forever.
            bool audioFinished = songEnded || playbackTime >= clipLength;

            return audioFinished && playbackTime >= clipLength + goodWindow;
        }
    }
}
