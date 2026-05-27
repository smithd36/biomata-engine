using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Writes simulation time data into the agent observation each tick.
    ///
    /// All time-based providers in the scene share the same Unity time source so
    /// every agent sees a consistent clock.
    ///
    /// Observation keys written:
    /// <list type="bullet">
    ///   <item>
    ///     <c>sim_time</c> — <c>double</c>. Seconds elapsed since the simulation
    ///     started (<c>Time.time</c>). Always written.
    ///   </item>
    ///   <item>
    ///     <c>time_of_day</c> — <c>double</c>. Only written when
    ///     <see cref="dayLengthSeconds"/> is greater than zero.
    ///     Default format is a 0.0–1.0 fraction of the day cycle;
    ///     enable <see cref="useHourFormat"/> for a 0–24 hour value instead.
    ///     Resets to 0 at the start of each day cycle.
    ///   </item>
    ///   <item>
    ///     <c>frame_count</c> — <c>int</c>. Only written when
    ///     <see cref="includeFrameCount"/> is enabled.
    ///   </item>
    /// </list>
    ///
    /// Example — configure a 10-minute day in 24-hour format:
    /// <code>
    ///   dayLengthSeconds = 600   // 10 real minutes = 1 game day
    ///   useHourFormat    = true  // time_of_day: 0.0 .. 24.0
    /// </code>
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Time")]
    public class TimeObservationProvider : ObservationProviderBase
    {
        [Tooltip(
            "Real-time length of one in-game day in seconds. " +
            "Set to 0 to disable time_of_day output.\n" +
            "Example: 600 = 10 real minutes per day.")]
        [Min(0f)]
        [SerializeField] private float dayLengthSeconds = 0f;

        [Tooltip(
            "When enabled, time_of_day is expressed as 0–24 hours. " +
            "When disabled (default), it is a 0.0–1.0 fraction of the day cycle.")]
        [SerializeField] private bool useHourFormat = false;

        [Tooltip(
            "Write the Unity frame_count integer into the observation. " +
            "Useful for debugging tick alignment; disable in production.")]
        [SerializeField] private bool includeFrameCount = false;

        public override void Populate(Dictionary<string, object> observation)
        {
            observation["sim_time"] = (double)Time.time;

            if (dayLengthSeconds > 0f)
            {
                float fraction             = (Time.time % dayLengthSeconds) / dayLengthSeconds;
                observation["time_of_day"] = useHourFormat
                    ? (object)(double)(fraction * 24f)
                    : (object)(double)fraction;
            }

            if (includeFrameCount)
                observation["frame_count"] = Time.frameCount;
        }
    }
}
