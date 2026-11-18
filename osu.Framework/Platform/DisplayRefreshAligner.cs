// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform.Linux.Native;
using osu.Framework.Platform.Windows.Native;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Paces draw frames at exactly the refresh rate, starting each one just early enough to be finished ahead of the display's next refresh.
    /// For displays which can only vary their refresh interval in steps, on which running slightly below the refresh rate makes frames wait for up to one such step.
    /// </summary>
    internal class DisplayRefreshAligner : IDisposable
    {
        /// <summary>
        /// Time in seconds by which a frame should be finished ahead of the refresh it's to be shown at.
        /// Has to cover the time the system takes to get a finished frame to the display (around 2.5 ms when presenting directly).
        /// </summary>
        private const double safety_margin = 0.0035;

        /// <summary>
        /// The number of recent frames used to predict how long the next frame takes to render.
        /// </summary>
        private const int render_time_history = 32;

        /// <summary>
        /// The number of late frames (without an on-time one in between) after which to give up aligning for a while.
        /// </summary>
        private const int late_frames_for_fallback = 60;

        /// <summary>
        /// How long in seconds to not align for after frames kept being late (ie. due to the window being composited rather than presented directly).
        /// </summary>
        private const double fallback_duration = 10;

        private const double feedback_timeout = 0.1;
        private const double maximum_wait = 0.1;
        private const double spin_time = 0.002;
        private const int tracked_frames = 256;

        private readonly IPresentationFeedback feedback;
        private readonly INativeSleep? nativeSleep;

        private readonly (long index, double start, double refresh)[] frames = new (long, double, double)[tracked_frames];
        private readonly double[] renderTimes = new double[render_time_history];
        private int renderTimeCount;

        private double lastFrameStart;
        private long lastCompletedFrame = -1;
        private (long index, double time) lastDisplayedFrame = (-1, 0);

        private int lateFrames;
        private double fallbackUntil;

        public DisplayRefreshAligner(IPresentationFeedback feedback)
        {
            this.feedback = feedback;

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                nativeSleep = new WindowsNativeSleep();
            else if (RuntimeInfo.IsUnix && UnixNativeSleep.Available)
                nativeSleep = new UnixNativeSleep();

            feedback.Enabled = true;
        }

        /// <summary>
        /// Blocks until the next draw frame should be started.
        /// </summary>
        /// <param name="refreshRate">The refresh rate of the display.</param>
        /// <param name="fallbackFrameRate">The frame rate to run at while not aligning to the display.</param>
        public void WaitForNextFrame(double refreshRate, double fallbackFrameRate)
        {
            double now = feedback.CurrentTime;
            double period = 1 / refreshRate;

            processFeedback(now, period);

            double start;
            double refresh = 0;

            if (now < fallbackUntil || lastDisplayedFrame.time <= 0 || now - lastDisplayedFrame.time > feedback_timeout)
                start = lastFrameStart + 1 / fallbackFrameRate;
            else
            {
                double slowest = 0;

                for (int i = 0; i < Math.Min(renderTimeCount, render_time_history); i++)
                    slowest = Math.Max(slowest, renderTimes[i]);

                double lead = Math.Min(slowest, period) + safety_margin;

                // the earliest refresh the frame can be finished for, with each frame still waiting to be shown taking up a refresh of its own.
                double earliest = Math.Ceiling((now + lead - lastDisplayedFrame.time) / period);
                long waiting = feedback.NextFrameIndex - lastCompletedFrame;

                refresh = lastDisplayedFrame.time + Math.Max(earliest, waiting) * period;
                start = refresh - lead;
            }

            lastFrameStart = Math.Clamp(start, now, now + maximum_wait);

            long index = feedback.NextFrameIndex;
            frames[index % tracked_frames] = (index, lastFrameStart, refresh);

            double remaining = lastFrameStart - feedback.CurrentTime;

            if (remaining > spin_time)
            {
                var sleepTime = TimeSpan.FromSeconds(remaining - spin_time);

                if (nativeSleep?.Sleep(sleepTime) != true)
                    Thread.Sleep(sleepTime);
            }

            while (feedback.CurrentTime < lastFrameStart)
                Thread.SpinWait(10);
        }

        private void processFeedback(double now, double period)
        {
            while (feedback.TryDequeue(out var frame))
            {
                lastCompletedFrame = Math.Max(lastCompletedFrame, frame.Index);

                if (frame.DisplayedTime <= 0)
                    continue;

                if (frame.Index > lastDisplayedFrame.index)
                    lastDisplayedFrame = (frame.Index, frame.DisplayedTime);

                (long index, double start, double refresh) = frames[frame.Index % tracked_frames];

                if (index != frame.Index || frame.RenderCompletedTime <= 0)
                    continue;

                renderTimes[renderTimeCount++ % render_time_history] = frame.RenderCompletedTime - start;

                if (refresh <= 0)
                    continue;

                if (frame.DisplayedTime <= refresh + period * 0.25)
                    lateFrames = 0;
                else if (++lateFrames >= late_frames_for_fallback)
                {
                    lateFrames = 0;
                    fallbackUntil = now + fallback_duration;
                }
            }
        }

        public void Dispose()
        {
            feedback.Enabled = false;
            nativeSleep?.Dispose();
        }
    }
}
