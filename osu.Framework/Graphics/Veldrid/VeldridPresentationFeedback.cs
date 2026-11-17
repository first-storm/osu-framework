// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Rendering;
using Veldrid;
using PresentedFrame = osu.Framework.Graphics.Rendering.PresentedFrame;

namespace osu.Framework.Graphics.Veldrid
{
    internal class VeldridPresentationFeedback : IPresentationFeedback
    {
        private readonly PresentationFeedback feedback;

        public VeldridPresentationFeedback(PresentationFeedback feedback)
        {
            this.feedback = feedback;
        }

        public bool Enabled
        {
            get => feedback.Enabled;
            set => feedback.Enabled = value;
        }

        public double CurrentTime => feedback.CurrentTime;

        public long NextFrameIndex => feedback.NextFrameIndex;

        public bool TryDequeue(out PresentedFrame frame)
        {
            if (!feedback.TryDequeue(out var veldridFrame))
            {
                frame = default;
                return false;
            }

            frame = new PresentedFrame(veldridFrame.Index, veldridFrame.RenderCompletedTime, veldridFrame.DisplayedTime);
            return true;
        }
    }
}
