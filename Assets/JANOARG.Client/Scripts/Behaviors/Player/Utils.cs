using System.Linq;
using JANOARG.Client.UI;
using JANOARG.Shared.Data.ChartInfo;
using UnityEngine;

namespace JANOARG.Client.Behaviors.Player
{
    public static class Utils
    {
        public static int[] typeMappings = new int []{
            0,// CameraPivot_X,
            1,// CameraPivot_Y,
            2,// CameraPivot_Z,
            3,// PivotDistance,
            4,// CameraRotation_X,
            5,// CameraRotation_Y
            6,// CameraRotation_Z
            0,// BackgroundColor_R
            1,// BackgroundColor_G
            2,// BackgroundColor_B
            3,// InterfaceColor_R
            4,// InterfaceColor_G
            5,// InterfaceColor_B
            6,// InterfaceColor_A
            0,// LaneColor_R
            1,// LaneColor_G
            2,// LaneColor_B
            3,// LaneColor_A
            4,// JudgeColor_R
            5,// JudgeColor_G
            6,// JudgeColor_B
            7,// JudgeColor_A
            0,// NormalColor_R
            1,// NormalColor_G
            2,// NormalColor_B
            3,// NormalColor_A
            4,// CatchColor_R
            5,// CatchColor_G
            6,// CatchColor_B
            7,// CatchColor_A
            8,// HoldTailColor_R
            9,// HoldTailColor_G
            10,// HoldTailColor_B
            11,// HoldTailColor_A
            0,// Position_Y
            1,// Position_Z
            2,// Position_X
            3,// Rotation_X
            4,// Rotation_Y
            5,// Rotation_Z
            0,// Offset_X
            1,// Offset_Y
            2,// Offset_Z
            3,// OffsetRotation_X
            4,// OffsetRotation_Y
            5,// OffsetRotation_Z
            0,// StartPos_X
            1,// StartPos_Y
            2,// StartPos_Z
            3,// EndPos_X
            4,// EndPos_Y
            5,// EndPos_Z
            6,// Speed
            0,// Position
            1// Length
        };
        public static (LanePosition, int index) laneLocalPositionWithoutOffset(this Lane lane, float time, Metronome timing, int cachedIndex = 0)
        {
            var steps = lane.LaneSteps;
            var first = steps.First();
            var last = steps.Last();
            var timeFirst = first.Offset;
            if (time <= timeFirst)
            {
                return (
                    new LanePosition
                    {
                        StartPosition = first.StartPointPosition,
                        EndPosition = first.EndPointPosition,
                        Offset = float.NaN
                    },
                    index: cachedIndex
                );
            }
            var timeLast = last.Offset;
            if (time >= timeLast)
            {
                return (
                    new LanePosition
                    {
                        StartPosition = last.StartPointPosition,
                        EndPosition = last.EndPointPosition,
                        Offset = float.NaN
                    },
                    index: cachedIndex
                );
            }
            var i = cachedIndex;
            var cachedStep = steps[i];
            if (cachedStep.Offset > time)
            {
                while (i > 0)
                {
                    i -= 1;
                    var st = steps[i - 1];
                    if (st.Offset < time)
                    {
                        cachedStep = st;
                        break;
                    }
                }
            }
            else while (i + 1 < steps.Count)
            {
                if (steps[i + 1].Offset > time)
                {
                    break;
                }
                i += 1;
                cachedStep = steps[i];
            }

            var next = steps[i + 1];
            var delta = (time - cachedStep.Offset) / (next.Offset - cachedStep.Offset);
            if (next.IsLinear)
                return (
                    new LanePosition
                    {
                        StartPosition = Vector2.LerpUnclamped(cachedStep.StartPointPosition, next.StartPointPosition, delta),
                        EndPosition = Vector2.LerpUnclamped(cachedStep.EndPointPosition, next.EndPointPosition, delta),
                        Offset = float.NaN
                    },
                    index: i
                );
            else
                return (
                    new LanePosition
                    {
                        StartPosition = new Vector2(
                            Mathf.LerpUnclamped(cachedStep.StartPointPosition.x, next.StartPointPosition.x, next.StartEaseX.Get(delta)),
                            Mathf.LerpUnclamped(cachedStep.StartPointPosition.y, next.StartPointPosition.y, next.StartEaseY.Get(delta))
                        ),
                        EndPosition = new Vector2(
                            Mathf.LerpUnclamped(cachedStep.EndPointPosition.x, next.EndPointPosition.x, next.EndEaseX.Get(delta)),
                            Mathf.LerpUnclamped(cachedStep.EndPointPosition.y, next.EndPointPosition.y, next.EndEaseY.Get(delta))
                        ),
                        Offset = float.NaN
                    },
                    index: i
                );

        }

        // why sec? who knows, should of used it for everything instead of this beatpos bs
        public static LaneStep ceilStepOrLastWithSeconds(this Lane lane, float timeSec, Metronome timing, int cachedIndex)
        {
            var steps = lane.LaneSteps;
            var first = steps.First();
            var last = steps.Last();
            var timeFirst = first.Offset;
            if (timeSec <= timing.ToSeconds(timeFirst))
                return first;
            var timeLast = last.Offset;
            if (timeSec >= timing.ToSeconds(timeLast))
            {
                return last;
            }
            var i = cachedIndex;
            var cachedStep = steps[i];
            while (timing.ToSeconds(cachedStep.Offset) < timeSec)
            {
                i += 1;
                cachedStep = steps[i];
            }
            while (i > 0)
            {
                if (timing.ToSeconds(steps[i - 1]) > timeSec)
                {
                    i -= 1;
                    cachedStep = steps[i];
                    continue;
                }
                break;
            }
            return cachedStep;
        }
    }

    // Best used one per sbable property (precaution). 
    // sb shall not be changed while this is in use (invariant).
    public struct StoryboardableSampler
    {
        Storyboardable sb;
        int cachedIndex;
#nullable enable
        Timestamp? cachedTs;
        public StoryboardableSampler(Storyboardable sb)
        {
            cachedIndex = 0;
            cachedTs = null;
            this.sb = sb;
        }

        // if you change timestamptypes' ordering i will be vewy sad.
        public float Sample(float timeInBeat, int typeIndex)
        {
            var type = sb.timestampTypes[typeIndex];
            var list = sb.Storyboard.FromType(type.ID);
            var last = list.LastOrDefault();
            if (timeInBeat >= last.Offset)
                return last.Target;
            var first = list.FirstOrDefault();
            if (timeInBeat <= first.Offset)
                return type.StoryboardGetter(sb);
            var cachedTimestamp = cachedTs ?? list[cachedIndex];

            if (cachedTimestamp.Offset > timeInBeat)
            {
                while (cachedIndex > 0)
                {
                    cachedIndex -= 1;
                    var ts = list[cachedIndex - 1];
                    if (ts.Offset < timeInBeat)
                    {
                        cachedTimestamp = ts;
                        break;
                    }
                }
            }
            else while (cachedIndex + 1 < list.Length)
            {
                if (list[cachedIndex + 1].Offset > timeInBeat)
                {
                    break;
                }
                cachedIndex += 1;
                cachedTimestamp = list[cachedIndex];
            }
            var from = float.IsNaN(cachedTimestamp.From) ? type.StoryboardGetter(sb) : cachedTimestamp.From;
            cachedTs = cachedTimestamp;
            return Mathf.LerpUnclamped(
                            from,
                            cachedTimestamp.Target,
                            cachedTimestamp.Easing.Get((timeInBeat - cachedTimestamp.Offset) / cachedTimestamp.Duration)
                        );
        }
    }

    public struct StoryboardableMultisampler
    {
        Storyboardable sb;
        int[] cachedIndex;
        Timestamp?[] cachedTs;
        float[] samples;

        public StoryboardableMultisampler(Storyboardable sb)
        {
            this.sb = sb;
            cachedIndex = new int[sb.timestampTypes.Length];
            cachedTs = new Timestamp?[sb.timestampTypes.Length];
            samples = new float[sb.timestampTypes.Length];
            Resample(0f);
        }

        public void Resample(float atBeat)
        {
            for (int i = 0; i < sb.timestampTypes.Length; i++)
            {
                resampleOne(ref cachedIndex[i], ref cachedTs[i], ref samples[i], i, atBeat);
            }
        }

        public float Get(TimestampIDs kind)
        {
            return samples[Utils.typeMappings[(int)kind]];
        }

        private void resampleOne(ref int cindex, ref Timestamp? cts, ref float sample, int tindex, float beat)
        {
            var type = sb.timestampTypes[tindex];
            // this call mutates (yikes)
            var list = sb.Storyboard.FromType(type.ID);
            var last = list.LastOrDefault();
            if (beat >= last.Offset)
            {
                sample = last.Target;
                return;
            }
            var first = list.FirstOrDefault();
            if (beat <= first.Offset)
            {
                sample = type.StoryboardGetter(sb);
                return;
            }
            var cachedTimestamp = cts ?? list[cindex];

            if (cachedTimestamp.Offset > beat)
            {
                while (cindex > 0)
                {
                    cindex -= 1;
                    var ts = list[cindex - 1];
                    if (ts.Offset < beat)
                    {
                        cachedTimestamp = ts;
                        break;
                    }
                }
            }
            else while (cindex + 1 < list.Length)
            {
                if (list[cindex + 1].Offset > beat)
                {
                    break;
                }
                cindex += 1;
                cachedTimestamp = list[cindex];
            }
            var from = float.IsNaN(cachedTimestamp.From) ? type.StoryboardGetter(sb) : cachedTimestamp.From;
            cts = cachedTimestamp;
            sample = Mathf.LerpUnclamped(
                from,
                cachedTimestamp.Target,
                cachedTimestamp.Easing.Get((beat - cachedTimestamp.Offset) / cachedTimestamp.Duration)
            );
        }
    }
}
