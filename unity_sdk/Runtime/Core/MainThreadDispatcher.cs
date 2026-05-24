// Biomata.SDK — MainThreadDispatcher.cs
//
// Captures Unity's main-thread SynchronizationContext at startup and exposes
// Dispatch(Action) so background tasks (WebSocket event stream handlers, etc.)
// can safely update UnityEngine objects.
//
// Usage:
//   MainThreadDispatcher.Dispatch(() => myRenderer.material.color = Color.red);
//
// This is initialized automatically by [RuntimeInitializeOnLoadMethod] —
// no manual setup required.

using System;
using System.Threading;
using UnityEngine;

namespace Biomata.SDK
{
    public static class MainThreadDispatcher
    {
        private static SynchronizationContext _context;
        private static int _mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            _context      = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>True when called from Unity's main thread.</summary>
        public static bool IsMainThread =>
            Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Execute <paramref name="action"/> on Unity's main thread.
        /// If already on the main thread, executes synchronously.
        /// </summary>
        public static void Dispatch(Action action)
        {
            if (action == null) return;

            if (IsMainThread)
            {
                action();
                return;
            }

            if (_context != null)
            {
                _context.Post(_ => SafeInvoke(action), null);
            }
            else
            {
                // Fallback if context wasn't captured (edit-mode tests, etc.)
                action();
            }
        }

        private static void SafeInvoke(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
