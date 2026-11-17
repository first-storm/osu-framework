// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Platform.Linux.Native;
using osu.Framework.Platform.Windows.Native;
using osu.Framework.Statistics;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Paces draw frames to the actual refresh instants of the display, starting each frame as late as possible
    /// while still being shown at the next refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some displays (ie. the built-in display of recent MacBook Pro models) can vary their refresh interval, but only in fixed steps.
    /// On such displays, running slightly below the refresh rate (the usual approach for variable refresh rate displays) doesn't reduce latency,
    /// as frames drift relative to the refresh instants and end up waiting for anything between zero and a whole refresh interval.
    /// Instead, this runs at exactly the refresh rate, with each frame timed to finish just in time for its refresh.
    /// </para>
    /// <para>
    /// A frame is shown at a refresh if the GPU finished rendering it a certain time ahead of that refresh (the "latch time").
    /// The latch time is a property of the system, and is the shortest time ever observed between a frame finishing and appearing on the display.
    /// To observe it, frames are initially run slightly below the refresh rate so that they drift relative to the display's refreshes.
    /// Every so often a frame just misses a refresh, with the frames right before it having only just made theirs.
    /// On a display which can refresh at any time this never happens, in which case frames are simply left running slightly below the refresh rate.
    /// </para>
    /// <para>
    /// From there on, each frame is started such that it is expected to finish rendering the latch time ahead of the next free refresh,
    /// based on how long recent frames took to render.
    /// </para>
    /// </remarks>
    internal class DisplayPhaseLock : IDisposable
    {
        /// <summary>
        /// The number of times in a row which frames have to drift past a refresh without the latch time changing for calibration to finish.
        /// </summary>
        private const int calibration_drifts = 10;

        /// <summary>
        /// The shortest time in seconds for which the latch time has to stay unchanged for calibration to finish.
        /// The latch time has been observed to take a few seconds to settle after a window is created, or after the way it's presented changes.
        /// </summary>
        private const double calibration_time = 4;

        /// <summary>
        /// The difference in latch time (in seconds) below which it's considered to not have changed.
        /// </summary>
        private const double calibration_tolerance = 0.00025;

        /// <summary>
        /// How much longer than the latch time (as a fraction of the refresh interval) a finished frame has to wait to be shown
        /// for it to be considered to have missed a refresh, rather than having been shown as soon as possible.
        /// </summary>
        private const double drift_wait_threshold = 0.25;

        /// <summary>
        /// The latch time is taken to be the n-th shortest time observed between a frame finishing and appearing on the display,
        /// to be robust against the odd bad sample.
        /// </summary>
        private const int latch_time_rank = 3;

        /// <summary>
        /// The number of recent frames used to predict how long the next frame takes to render.
        /// </summary>
        private const int render_time_history = 32;

        /// <summary>
        /// Time in seconds allowed for a frame to take longer to render than any of the recent frames.
        /// </summary>
        private const double render_time_margin = 0.001;

        /// <summary>
        /// The number of frames which have to be shown late in spite of being finished in time, without any frame being on time in between, for the latch time to be measured anew.
        /// Measuring the latch time means not being aligned to the display for a while, so this is intentionally a lot more than
        /// what could plausibly be seen for other reasons (ie. frames waiting to be shown behind slow frames while loading).
        /// </summary>
        private const int late_frames_for_recalibration = 60;

        /// <summary>
        /// How long ahead of the latch time (in seconds) a frame has to be finished for it being shown late to be unexpected.
        /// </summary>
        private const double recalibration_tolerance = 0.0005;

        /// <summary>
        /// Time in seconds after which feedback from the display is considered stale (ie. because the window isn't visible).
        /// </summary>
        private const double feedback_timeout = 0.1;

        /// <summary>
        /// The longest time in seconds to ever wait for the start of a frame.
        /// </summary>
        private const double maximum_wait = 0.1;

        /// <summary>
        /// Time in seconds to spin for at the end of a wait, to make up for imprecise thread sleeps.
        /// </summary>
        private const double spin_time = 0.002;

        private const int tracked_frames = 256;

        private static readonly GlobalStatistic<double> statistic_latch_time = GlobalStatistics.Get<double>("Display phase lock", "Latch time (ms)");
        private static readonly GlobalStatistic<double> statistic_render_time = GlobalStatistics.Get<double>("Display phase lock", "Predicted render time (ms)");
        private static readonly GlobalStatistic<int> statistic_late_frames = GlobalStatistics.Get<int>("Display phase lock", "Late frames");

        private readonly IPresentationFeedback feedback;
        private readonly INativeSleep? nativeSleep;

        private double refreshRate;
        private double period;

        /// <summary>
        /// The interval at which frames are started while not aligned to the display.
        /// </summary>
        private double unalignedPeriod;

        /// <summary>
        /// The time the most recent frame was started at.
        /// </summary>
        private double lastFrameStart;

        /// <summary>
        /// The most recent frame for which feedback was received.
        /// </summary>
        private long lastCompletedFrame = -1;

        /// <summary>
        /// The most recent frame which was shown on the display, and when.
        /// </summary>
        private (long index, double time) lastDisplayedFrame = (-1, 0);

        /// <summary>
        /// When each recent frame was started and the refresh it was aimed at (zero if none).
        /// </summary>
        private readonly (long index, double start, double refresh)[] frames = new (long, double, double)[tracked_frames];

        /// <summary>
        /// The shortest times observed between a frame finishing and appearing on the display, in ascending order.
        /// </summary>
        private readonly double[] shortestLatchTimes = new double[latch_time_rank];

        /// <summary>
        /// How long before a refresh a frame has to be finished in order to be shown at that refresh, in seconds.
        /// </summary>
        private double latchTime => shortestLatchTimes[latch_time_rank - 1];

        /// <summary>
        /// Whether the latch time is still being measured.
        /// </summary>
        private bool calibrating;

        /// <summary>
        /// The number of times frames have drifted past a refresh since the latch time last changed.
        /// </summary>
        private int calibrationDrifts;

        /// <summary>
        /// The latch time as of the last time it was considered to have changed, and when that was.
        /// </summary>
        private double calibrationLatchTime;

        private double calibrationLatchTimeChanged;

        /// <summary>
        /// The time taken from starting a frame until the GPU finished rendering it, for the most recent frames.
        /// </summary>
        private readonly double[] renderTimes = new double[render_time_history];

        private int renderTimeCount;

        /// <summary>
        /// The time expected to be taken from starting the next frame until the GPU has finished rendering it, including a margin.
        /// </summary>
        private double predictedRenderTime = render_time_margin;

        /// <summary>
        /// The number of frames in a row which were shown late in spite of being finished in time.
        /// </summary>
        private int unexpectedLateFrames;

        private volatile bool recalibrationRequested;

        public DisplayPhaseLock(IPresentationFeedback feedback)
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
        /// <param name="unalignedFrameRate">The frame rate to run at while frames aren't aligned to the display. Should be slightly below the refresh rate.</param>
        public void WaitForNextFrame(double refreshRate, double unalignedFrameRate)
        {
            double start = GetNextFrameStart(feedback.CurrentTime, refreshRate, unalignedFrameRate);

            double remaining = start - feedback.CurrentTime;

            if (remaining > spin_time)
            {
                var sleepTime = TimeSpan.FromSeconds(remaining - spin_time);

                if (nativeSleep?.Sleep(sleepTime) != true)
                    Thread.Sleep(sleepTime);
            }

            while (feedback.CurrentTime < start)
                Thread.SpinWait(10);
        }

        /// <summary>
        /// Measures the latch time anew, ie. because the window was moved to another display. Can be called from any thread.
        /// </summary>
        public void Recalibrate() => recalibrationRequested = true;

        /// <summary>
        /// Returns the time at which the next draw frame should be started.
        /// </summary>
        /// <param name="now">The current time, in the time base of the <see cref="IPresentationFeedback"/>.</param>
        /// <param name="refreshRate">The refresh rate of the display.</param>
        /// <param name="unalignedFrameRate">The frame rate to run at while frames aren't aligned to the display. Should be slightly below the refresh rate.</param>
        internal double GetNextFrameStart(double now, double refreshRate, double unalignedFrameRate)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (refreshRate != this.refreshRate || recalibrationRequested)
            {
                recalibrationRequested = false;
                this.refreshRate = refreshRate;
                period = 1 / refreshRate;
                beginCalibration();
            }

            unalignedPeriod = 1 / unalignedFrameRate;

            processFeedback();

            predictedRenderTime = predictRenderTime();

            double start;

            if (tryGetNextRefresh(now, out double refresh))
                start = refresh - latchTime - predictedRenderTime;
            else
            {
                // running slightly below the refresh rate is what makes frames drift relative to the display's refreshes while calibrating.
                // it's also the best that can be done on a display which can refresh at any time, on which calibration never finishes.
                start = lastFrameStart + unalignedPeriod;
            }

            lastFrameStart = Math.Clamp(start, now, now + maximum_wait);

            long index = feedback.NextFrameIndex;
            frames[index % tracked_frames] = (index, lastFrameStart, refresh);

            return lastFrameStart;
        }

        private void processFeedback()
        {
            while (feedback.TryDequeue(out var frame))
            {
                lastCompletedFrame = Math.Max(lastCompletedFrame, frame.Index);

                // the frame was never shown.
                if (frame.DisplayedTime <= 0)
                    continue;

                var previousDisplayedFrame = lastDisplayedFrame;

                if (frame.Index > lastDisplayedFrame.index)
                    lastDisplayedFrame = (frame.Index, frame.DisplayedTime);

                (long index, double start, double refresh) = frames[frame.Index % tracked_frames];

                if (index != frame.Index || frame.RenderCompletedTime <= 0)
                    continue;

                double renderTime = frame.RenderCompletedTime - start;

                addLatchTime(frame.DisplayedTime - frame.RenderCompletedTime);

                if (calibrating)
                {
                    // a frame which was neither started late nor took long to render, yet left the refresh after the previous frame without a new frame...
                    bool driftedPastRefresh = frame.Index == previousDisplayedFrame.index + 1
                                              && frame.DisplayedTime - previousDisplayedFrame.time > period * 1.25
                                              && wasStartedOnTime(frame.Index)
                                              && renderTime <= predictedRenderTime
                                              // ...in spite of having been finished for a good while longer than it takes to show a frame.
                                              // a display which can refresh at any time (or in very small steps) never keeps a frame waiting like this.
                                              && frame.DisplayedTime - frame.RenderCompletedTime > latchTime + period * drift_wait_threshold;

                    updateCalibration(driftedPastRefresh, frame.DisplayedTime);
                }
                else if (refresh > 0)
                {
                    bool late = frame.DisplayedTime > refresh + period * 0.25;

                    if (late)
                        statistic_late_frames.Value++;

                    // how long ahead of the latch time the frame was finished.
                    double spare = refresh - latchTime - frame.RenderCompletedTime;

                    // a few frames being late is expected after a slow frame, as they have to wait for it to be shown.
                    // but if frames keep being late in spite of having been finished well in time for the refresh they were aimed at,
                    // the latch time must have increased (ie. due to the window no longer being presented directly to the display).
                    // frames which are late for a reason (ie. waiting behind such a frame which was finished too late) don't say anything either way.
                    if (!late)
                        unexpectedLateFrames = 0;
                    else if (spare > recalibration_tolerance)
                        unexpectedLateFrames++;

                    if (unexpectedLateFrames >= late_frames_for_recalibration)
                    {
                        Logger.Log("Display phase lock is measuring the latch time anew as frames keep being shown late.", LoggingTarget.Performance);
                        beginCalibration();
                    }
                }

                renderTimes[renderTimeCount++ % render_time_history] = renderTime;
            }
        }

        /// <summary>
        /// Whether a frame was started no later than expected after the frame before it, while calibrating.
        /// </summary>
        private bool wasStartedOnTime(long frameIndex)
        {
            if (frameIndex == 0)
                return false;

            var frame = frames[frameIndex % tracked_frames];
            var previous = frames[(frameIndex - 1) % tracked_frames];

            return frame.index == frameIndex
                   && previous.index == frameIndex - 1
                   && frame.start - previous.start <= unalignedPeriod * 1.25;
        }

        #region Latch time

        private void beginCalibration()
        {
            calibrating = true;
            calibrationDrifts = 0;
            calibrationLatchTime = double.MaxValue;
            unexpectedLateFrames = 0;

            Array.Fill(shortestLatchTimes, double.MaxValue);
        }

        /// <summary>
        /// Takes note of the time between a frame finishing and appearing on the display.
        /// </summary>
        private void addLatchTime(double time)
        {
            if (time <= 0 || time >= latchTime)
                return;

            // insert in order, dropping the longest.
            int i = latch_time_rank - 1;

            for (; i > 0 && shortestLatchTimes[i - 1] > time; i--)
                shortestLatchTimes[i] = shortestLatchTimes[i - 1];

            shortestLatchTimes[i] = time;

            statistic_latch_time.Value = latchTime * 1000;
        }

        private void updateCalibration(bool driftedPastRefresh, double time)
        {
            if (Math.Abs(calibrationLatchTime - latchTime) > calibration_tolerance)
            {
                calibrationLatchTime = latchTime;
                calibrationLatchTimeChanged = time;
                calibrationDrifts = 0;
                return;
            }

            // while frames drift relative to the display's refreshes, one of them misses a refresh every so often.
            // the frames right before it are the ones which only just made theirs, so they are the ones showing the latch time.
            // this is not the case while frames are queued up behind each other (ie. after slow frames while loading), as there's always a frame ready to be shown then.
            // once frames have drifted past a refresh a few times without turning up anything new, that's it.
            if (!driftedPastRefresh || ++calibrationDrifts < calibration_drifts)
                return;

            // frames can also miss refreshes in quick succession while the way the window is presented is changing, with the latch time not having settled yet.
            if (time - calibrationLatchTimeChanged < calibration_time)
                return;

            // a latch time this long means that frames aren't going straight to the display (ie. the window is being composited with others).
            // frames would then be aligned with one of them always waiting to be shown, which hides the latch time getting shorter again:
            // each frame can't be shown any sooner than a refresh after the one before it, however early it's finished.
            // so keep measuring instead, which is also what notices the latch time getting shorter.
            if (latchTime >= period)
            {
                calibrationDrifts = 0;
                return;
            }

            calibrating = false;

            Logger.Log(FormattableString.Invariant($"Display phase lock calibrated at {refreshRate:0.##} Hz with a latch time of {latchTime * 1000:0.00} ms."), LoggingTarget.Performance);
        }

        #endregion

        /// <summary>
        /// Finds the refresh which the next frame should be shown at.
        /// </summary>
        /// <returns>Whether there is a refresh to align to.</returns>
        private bool tryGetNextRefresh(double now, out double refresh)
        {
            refresh = 0;

            if (calibrating)
                return false;

            // without recent feedback from the display (ie. because the window isn't visible) there's nothing to align to.
            if (lastDisplayedFrame.time <= 0 || now - lastDisplayedFrame.time > feedback_timeout)
                return false;

            // refreshes happen at multiples of the refresh interval after the last known one.
            // the frame can't be shown before it's finished...
            double earliest = Math.Ceiling((now + latchTime + predictedRenderTime - lastDisplayedFrame.time) / period);

            // ...and each frame which is still waiting to be shown takes up a refresh of its own.
            // importantly, this is what stops frames from staying queued up behind each other after a slow frame.
            long waiting = feedback.NextFrameIndex - lastCompletedFrame;

            refresh = lastDisplayedFrame.time + Math.Max(earliest, waiting) * period;
            return true;
        }

        /// <summary>
        /// Predicts the time taken from starting the next frame until the GPU has finished rendering it, including a margin.
        /// </summary>
        private double predictRenderTime()
        {
            int count = Math.Min(renderTimeCount, render_time_history);

            // frames missing their refresh is a lot more noticeable than starting them a little earlier than necessary,
            // so go by the slowest of the recent frames. this also keeps up with frames taking longer every now and then (ie. while loading).
            double slowest = 0;

            for (int i = 0; i < count; i++)
                slowest = Math.Max(slowest, renderTimes[i]);

            // a frame taking longer than a refresh interval is going to be late no matter what. no point in starting all frames that early.
            double predicted = Math.Min(slowest, period) + render_time_margin;

            statistic_render_time.Value = predicted * 1000;

            return predicted;
        }

        public void Dispose()
        {
            feedback.Enabled = false;
            nativeSleep?.Dispose();
        }
    }
}
