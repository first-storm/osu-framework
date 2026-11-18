// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Reports when frames presented by an <see cref="IRenderer"/> finished rendering and when they were actually shown on the display.
    /// </summary>
    public interface IPresentationFeedback
    {
        /// <summary>
        /// Whether feedback is being collected. Disabled by default.
        /// While enabled, frames must be regularly retrieved using <see cref="TryDequeue"/>.
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// The current time in seconds, in the time base used by <see cref="PresentedFrame"/>.
        /// </summary>
        double CurrentTime { get; }

        /// <summary>
        /// The <see cref="PresentedFrame.Index"/> which the next frame to be presented is going to have.
        /// </summary>
        long NextFrameIndex { get; }

        /// <summary>
        /// Retrieves feedback for a frame which is no longer waiting to be shown.
        /// </summary>
        /// <returns>Whether feedback was available.</returns>
        bool TryDequeue(out PresentedFrame frame);
    }
}
