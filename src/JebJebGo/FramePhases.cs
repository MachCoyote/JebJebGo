using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace JebJebGo
{
    /// <summary>
    /// Field diagnostics: where the last completed frame's time went, measured
    /// with timestamps from two components that run first and last in each
    /// script phase, the UI canvas callback, and end of frame. Phases (ms):
    /// <c>Gap</c> (end of previous frame to the first Update: physics, input,
    /// vsync), <c>Update</c> (all Update scripts), <c>Coroutines</c> (yield-null
    /// coroutines and animation, between Update and LateUpdate), <c>LateUpdate</c>,
    /// <c>Canvas</c> (UI layout and graphic rebuilds, ending at
    /// Canvas.willRenderCanvases), <c>Render</c> (to end of frame).
    /// </summary>
    internal static class FramePhases
    {
        private static long frameEnd = -1;
        private static long updateStart;
        private static long updateEnd;
        private static long lateStart;
        private static long lateEnd;
        private static long canvasDone;

        public static double Gap { get; private set; }
        public static double Update { get; private set; }
        public static double Coroutines { get; private set; }
        public static double LateUpdate { get; private set; }
        public static double Canvas { get; private set; }
        public static double Render { get; private set; }

        public static void Install(GameObject host)
        {
            host.AddComponent<First>();
            host.AddComponent<Last>();
            UnityEngine.Canvas.willRenderCanvases += () => canvasDone = Stopwatch.GetTimestamp();
        }

        public static string Describe()
        {
            return "update " + Update.ToString("0") + ", coroutines " + Coroutines.ToString("0") + ", late " + LateUpdate.ToString("0")
                + ", canvas " + Canvas.ToString("0") + ", render " + Render.ToString("0") + ", between frames " + Gap.ToString("0");
        }

        private static double Ms(long from, long to)
        {
            return from <= 0 || to < from ? 0.0 : (to - from) * 1000.0 / Stopwatch.Frequency;
        }

        private static void FrameDone(long now)
        {
            // Close the frame: canvas is the part of render+canvas before the canvas callback.
            long canvasEnd = canvasDone >= lateEnd ? canvasDone : lateEnd;
            Coroutines = Ms(updateEnd, lateStart);
            LateUpdate = Ms(lateStart, lateEnd);
            Canvas = Ms(lateEnd, canvasEnd);
            Render = Ms(canvasEnd, now);
            frameEnd = now;
        }

        [DefaultExecutionOrder(-32000)]
        private sealed class First : MonoBehaviour
        {
            private void Update()
            {
                long now = Stopwatch.GetTimestamp();
                Gap = frameEnd < 0 ? 0.0 : Ms(frameEnd, now);
                updateStart = now;
            }

            private void LateUpdate()
            {
                lateStart = Stopwatch.GetTimestamp();
            }
        }

        [DefaultExecutionOrder(32000)]
        private sealed class Last : MonoBehaviour
        {
            private readonly WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();

            private void Start()
            {
                StartCoroutine(EndOfFrame());
            }

            private void Update()
            {
                updateEnd = Stopwatch.GetTimestamp();
                FramePhases.Update = Ms(updateStart, updateEnd);
            }

            private void LateUpdate()
            {
                lateEnd = Stopwatch.GetTimestamp();
            }

            private IEnumerator EndOfFrame()
            {
                while (true)
                {
                    yield return endOfFrame;
                    FrameDone(Stopwatch.GetTimestamp());
                }
            }
        }
    }
}
