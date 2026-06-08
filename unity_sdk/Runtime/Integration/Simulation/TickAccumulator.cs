using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Lightweight rate limiter shared by <see cref="UnitySimulationManager"/> and
    /// <see cref="BiomataSimulationBootstrapper"/> to avoid duplicating the same
    /// dt-accumulation / interval-check pattern in two places.
    ///
    /// Call <see cref="Advance"/> every frame with the elapsed delta time and the
    /// desired tick rate (ticks per second). It returns <c>true</c> — and resets the
    /// internal accumulator — exactly once per interval.
    /// </summary>
    internal struct TickAccumulator
    {
        private float _elapsed;

        /// <summary>
        /// Accumulate <paramref name="dt"/> seconds and return <c>true</c> when the
        /// interval implied by <paramref name="tickRate"/> has elapsed.
        /// Resets the accumulator on <c>true</c> so the next call starts fresh.
        /// </summary>
        /// <param name="dt">Elapsed time this frame (e.g. <c>Time.deltaTime</c>).</param>
        /// <param name="tickRate">Ticks per second. 0 fires on every call.</param>
        public bool Advance(float dt, float tickRate)
        {
            _elapsed += dt;
            float interval = tickRate > 0f ? 1f / tickRate : float.Epsilon;
            if (_elapsed < interval) return false;
            _elapsed = 0f;
            return true;
        }

        /// <summary>Reset the accumulator without firing a tick.</summary>
        public void Reset() => _elapsed = 0f;
    }
}
