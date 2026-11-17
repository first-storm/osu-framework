// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Feedback for a frame presented by an <see cref="IRenderer"/>.
    /// Times are in seconds, in the time base of <see cref="IPresentationFeedback.CurrentTime"/>.
    /// </summary>
    /// <param name="Index">The index of the frame. Increases by one for every presented frame.</param>
    /// <param name="RenderCompletedTime">When the GPU finished rendering the frame, or zero if unknown.</param>
    /// <param name="DisplayedTime">When the frame appeared on the display, or zero if it was never shown.</param>
    public readonly record struct PresentedFrame(long Index, double RenderCompletedTime, double DisplayedTime);
}
