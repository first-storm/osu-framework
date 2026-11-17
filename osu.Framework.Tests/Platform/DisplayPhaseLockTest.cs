// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;

namespace osu.Framework.Tests.Platform
{
    [TestFixture]
    public class DisplayPhaseLockTest
    {
        private const double refresh_rate = 120;
        private const double period = 1 / refresh_rate;

        /// <summary>
        /// The frame rate to run at while not aligned to the display.
        /// </summary>
        private const double unaligned_frame_rate = refresh_rate - 2;

        private const double latch_time = 0.0025;
        private const double render_time = 0.002;

        /// <summary>
        /// The time taken from starting a frame until the next one could be started.
        /// </summary>
        private const double draw_time = 0.0003;

        // matches the allowance for frames taking longer than predicted.
        private const double render_time_margin = 0.001;

        private TestDisplay display = null!;
        private DisplayPhaseLock phaseLock = null!;

        private double time;

        private void setUp(double granularity, double refreshInterval = period)
        {
            display = new TestDisplay(refreshInterval, granularity, latch_time);
            phaseLock = new DisplayPhaseLock(display);

            time = 1;
        }

        [TearDown]
        public void TearDown()
        {
            phaseLock.Dispose();
        }

        /// <summary>
        /// Once calibrated, frames should be started just in time to finish the latch time ahead of a refresh.
        /// </summary>
        // a display with a fixed refresh rate.
        [TestCase(period)]
        // a display which can vary its refresh interval in steps of half the shortest one.
        [TestCase(period / 2)]
        public void TestFramesAreStartedJustInTime(double granularity)
        {
            setUp(granularity);

            runFrames(1000);
            var frames = runFrames(1000);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + render_time + render_time_margin).Within(0.0003));
            Assert.That(intervals(frames), Is.All.EqualTo(period).Within(1e-6));
        }

        /// <summary>
        /// Each frame should be shown, with none of them waiting behind another one.
        /// </summary>
        [TestCase(period)]
        [TestCase(period / 2)]
        public void TestFramesDoNotStayQueuedAfterSlowFrame(double granularity)
        {
            setUp(granularity);

            runFrames(1000);

            // a single frame which takes several refresh intervals to render.
            runFrames(1, _ => 0.03);

            // frames which were started before the slow one was known to be slow are unavoidably late,
            // and frames are started earlier for a moment in case more slow frames follow.
            runFrames(50);

            var frames = runFrames(500);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + render_time + render_time_margin).Within(0.0003));
            Assert.That(intervals(frames), Is.All.EqualTo(period).Within(1e-6));
        }

        /// <summary>
        /// Frames which only just miss their refresh shouldn't cause any refresh to go without a new frame on a display which can show them a little later.
        /// </summary>
        [Test]
        public void TestSlightlySlowFrameDoesNotCauseSkippedRefresh()
        {
            setUp(period / 2);

            runFrames(1000);

            var frames = runFrames(200, i => i == 100 ? render_time + render_time_margin + 0.001 : render_time);

            // the slow frame is shown half a refresh interval late, with everything after it following at regular intervals.
            Assert.That(intervals(frames).Max(), Is.EqualTo(period * 1.5).Within(1e-6));
            Assert.That(intervals(frames).Count(i => i > period + 1e-6), Is.EqualTo(1));
        }

        /// <summary>
        /// Frames should keep being on time when they start taking longer to render.
        /// </summary>
        [Test]
        public void TestAdaptsToRenderTime()
        {
            setUp(period / 2);

            runFrames(1000);

            const double slower_render_time = render_time + 0.003;

            // give it a moment to notice.
            runFrames(100, _ => slower_render_time);

            var frames = runFrames(500, _ => slower_render_time);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + slower_render_time + render_time_margin).Within(0.0003));
            Assert.That(intervals(frames), Is.All.EqualTo(period).Within(1e-6));
        }

        /// <summary>
        /// The latch time should still be found if frames are queued up to begin with.
        /// </summary>
        [TestCase(0.05)]
        [TestCase(0.2)]
        [TestCase(0.5)]
        public void TestCalibratesWithSlowFramesAtStart(double slowFrameProbability)
        {
            setUp(period / 2);

            var random = new Random(1337);

            // lots of slow frames, as seen while loading. some of them leave refreshes without a new frame, others have frames queue up behind them.
            runFrames(600, _ => random.NextDouble() < slowFrameProbability ? random.NextDouble() * 0.05 : render_time);

            runFrames(1000);
            var frames = runFrames(500);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + render_time + render_time_margin).Within(0.0003));
        }

        /// <summary>
        /// The latch time should be measured anew if it increases.
        /// </summary>
        [TestCase(period)]
        [TestCase(period / 2)]
        public void TestRecoversFromLatchTimeIncreasing(double granularity)
        {
            setUp(granularity);

            runFrames(1000);

            const double longer_latch_time = latch_time + 0.003;

            display.LatchTime = longer_latch_time;

            runFrames(1000);
            var frames = runFrames(500);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(longer_latch_time + render_time + render_time_margin).Within(0.0003));
            Assert.That(intervals(frames), Is.All.EqualTo(period).Within(1e-6));
        }

        /// <summary>
        /// While the latch time is longer than a refresh interval (ie. due to the window being composited with others rather than presented directly to the display),
        /// frames should be left running slightly below the refresh rate, and be aligned again once that's over.
        /// </summary>
        [TestCase(period)]
        [TestCase(period / 2)]
        public void TestDoesNotAlignWhileLatchTimeIsLong(double granularity)
        {
            setUp(granularity);

            runFrames(1000);

            display.LatchTime = latch_time + 0.011;

            runFrames(1000);
            var frames = runFrames(2000);

            Assert.That(frames.Zip(frames.Skip(1), (a, b) => b.Start - a.Start), Is.All.EqualTo(1 / unaligned_frame_rate).Within(1e-6));

            display.LatchTime = latch_time;

            runFrames(1500);
            frames = runFrames(500);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + render_time + render_time_margin).Within(0.0003));
            Assert.That(intervals(frames), Is.All.EqualTo(period).Within(1e-6));
        }

        /// <summary>
        /// On a display which can refresh at any time, there are no refreshes to align to.
        /// Frames should be left running slightly below the refresh rate, at which they are shown as soon as they are finished.
        /// </summary>
        [Test]
        public void TestDoesNotAlignToFreelyRefreshingDisplay()
        {
            setUp(1e-7);

            runFrames(1000);
            var frames = runFrames(1000);

            Assert.That(frames.Select(f => f.Displayed - f.Start), Is.All.EqualTo(latch_time + render_time).Within(1e-6));
            Assert.That(intervals(frames), Is.All.EqualTo(1 / unaligned_frame_rate).Within(1e-6));
        }

        /// <summary>
        /// Frames which take a while to render shouldn't be mistaken for frames having missed a refresh on a display which can refresh at any time.
        /// </summary>
        [TestCase(0.02, 0.012)]
        [TestCase(0.05, 0.012)]
        [TestCase(0.05, 0.05)]
        [TestCase(0.2, 0.05)]
        public void TestDoesNotAlignToFreelyRefreshingDisplayWithSlowFrames(double slowFrameProbability, double slowestRenderTime)
        {
            setUp(1e-7);

            var random = new Random(1337);

            runFrames(20000, _ => random.NextDouble() < slowFrameProbability ? random.NextDouble() * slowestRenderTime : render_time);

            // give frames which are still waiting to be shown a moment to clear.
            runFrames(500);
            var frames = runFrames(1000);

            Assert.That(intervals(frames), Is.All.EqualTo(1 / unaligned_frame_rate).Within(1e-6));
        }

        /// <summary>
        /// The latch time should be measured anew on request, ie. after the window was moved to a display which can refresh at any time.
        /// </summary>
        [Test]
        public void TestRecalibrate()
        {
            setUp(period / 2);

            runFrames(1000);

            display.Granularity = 1e-7;
            phaseLock.Recalibrate();

            runFrames(100);
            var frames = runFrames(1000);

            Assert.That(intervals(frames), Is.All.EqualTo(1 / unaligned_frame_rate).Within(1e-6));
        }

        /// <summary>
        /// If the display refreshes at a lower rate than reported (ie. due to a power saving mode), every refresh should still get a new frame.
        /// </summary>
        [Test]
        public void TestDisplayRefreshingSlowerThanReported()
        {
            setUp(period * 2, period * 2);

            runFrames(1000);
            var frames = runFrames(1000);

            Assert.That(intervals(frames), Is.All.EqualTo(period * 2).Within(1e-6));
        }

        /// <summary>
        /// Without any feedback from the display (ie. the window isn't visible), frames should be run at the unaligned frame rate.
        /// </summary>
        [Test]
        public void TestRunsUnalignedWithoutFeedback()
        {
            setUp(period);

            runFrames(1000);

            display.Visible = false;

            // give it a moment to notice.
            runFrames(50);

            var frames = runFrames(200);

            Assert.That(frames.Zip(frames.Skip(1), (a, b) => b.Start - a.Start), Is.All.EqualTo(1 / unaligned_frame_rate).Within(1e-6));
        }

        private static IEnumerable<double> intervals(IReadOnlyList<TestDisplay.Frame> frames)
            => frames.Zip(frames.Skip(1), (a, b) => b.Displayed - a.Displayed);

        /// <summary>
        /// Runs a number of frames.
        /// </summary>
        /// <param name="count">The number of frames to run.</param>
        /// <param name="getRenderTime">The time taken to render each frame, given its index within this run. Uses <see cref="render_time"/> if not provided.</param>
        /// <returns>The frames which were run, once they have been displayed.</returns>
        private IReadOnlyList<TestDisplay.Frame> runFrames(int count, Func<int, double>? getRenderTime = null)
        {
            var frames = new List<TestDisplay.Frame>();

            for (int i = 0; i < count; i++)
            {
                display.CurrentTime = time;

                double start = phaseLock.GetNextFrameStart(time, refresh_rate, unaligned_frame_rate);

                Assert.That(start, Is.GreaterThanOrEqualTo(time));

                var frame = display.Present(start, start + (getRenderTime?.Invoke(i) ?? render_time));

                frames.Add(frame);

                // the draw thread is free again shortly after, regardless of how long the GPU takes...
                // ...unless too many frames are waiting to be shown already.
                time = Math.Max(start + draw_time, frame.PresentReturned);
            }

            return frames;
        }

        /// <summary>
        /// A display which shows a frame at the first refresh which is at least the latch time after the frame was finished,
        /// and at least a refresh interval after the previous frame was shown.
        /// </summary>
        private class TestDisplay : IPresentationFeedback
        {
            public class Frame
            {
                public long Index;
                public double Start;
                public double RenderCompleted;
                public double Displayed;

                /// <summary>
                /// When presenting the frame returned control.
                /// </summary>
                public double PresentReturned;
            }

            public bool Enabled { get; set; }

            public double CurrentTime { get; set; }

            public long NextFrameIndex { get; private set; }

            /// <summary>
            /// Whether frames are shown at all.
            /// </summary>
            public bool Visible = true;

            /// <summary>
            /// How long before a refresh a frame has to be finished in order to be shown at that refresh.
            /// </summary>
            public double LatchTime;

            /// <summary>
            /// Refreshes can only happen at multiples of this.
            /// </summary>
            public double Granularity;

            private readonly double refreshInterval;

            /// <summary>
            /// The number of frames which can be waiting to be shown at a time. Presenting any more than this blocks, as with a real swapchain.
            /// </summary>
            private const int max_waiting_frames = 3;

            private readonly Queue<Frame> pending = new Queue<Frame>();
            private readonly Queue<double> waiting = new Queue<double>();
            private double lastDisplayed;

            public TestDisplay(double refreshInterval, double granularity, double latchTime)
            {
                this.refreshInterval = refreshInterval;
                Granularity = granularity;

                LatchTime = latchTime;
            }

            public Frame Present(double start, double renderCompleted)
            {
                var frame = new Frame { Index = NextFrameIndex++, Start = start, RenderCompleted = renderCompleted, PresentReturned = start };

                if (Visible)
                {
                    if (waiting.Count == max_waiting_frames)
                    {
                        // wait for the oldest frame to be shown. the GPU can't start on this one any earlier either.
                        frame.PresentReturned = Math.Max(start, waiting.Dequeue());
                        frame.RenderCompleted += frame.PresentReturned - start;
                    }

                    double earliest = Math.Max(frame.RenderCompleted + LatchTime, lastDisplayed + refreshInterval);

                    frame.Displayed = lastDisplayed = Math.Ceiling(earliest / Granularity - 1e-9) * Granularity;

                    waiting.Enqueue(frame.Displayed);
                }

                pending.Enqueue(frame);
                return frame;
            }

            public bool TryDequeue(out PresentedFrame frame)
            {
                // feedback only becomes available once a frame has been shown (or right away if it's never going to be).
                if (!Enabled || !pending.TryPeek(out var next) || next.Displayed > CurrentTime)
                {
                    frame = default;
                    return false;
                }

                pending.Dequeue();

                frame = new PresentedFrame(next.Index, next.RenderCompleted, next.Displayed);
                return true;
            }
        }
    }
}
