// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Platform.Apple.Native;

namespace osu.Framework.Platform.MacOS.Native
{
    internal readonly struct NSScreen
    {
        internal IntPtr Handle { get; }

        private static readonly IntPtr class_pointer = Class.Get("NSScreen");
        private static readonly IntPtr sel_main_screen = Selector.Get("mainScreen");
        private static readonly IntPtr sel_responds_to_selector = Selector.Get("respondsToSelector:");
        private static readonly IntPtr sel_display_update_granularity = Selector.Get("displayUpdateGranularity");

        internal NSScreen(IntPtr handle)
        {
            Handle = handle;
        }

        /// <summary>
        /// The screen containing the window which currently has keyboard focus.
        /// </summary>
        internal static NSScreen MainScreen() => new NSScreen(Interop.SendIntPtr(class_pointer, sel_main_screen));

        /// <summary>
        /// The step (in seconds) in which the screen can vary its refresh interval. Zero if it can refresh at any time, or on macOS versions before 12.0.
        /// </summary>
        internal double DisplayUpdateGranularity
        {
            get
            {
                if (Handle == IntPtr.Zero || !Interop.SendBool(Handle, sel_responds_to_selector, sel_display_update_granularity))
                    return 0;

                return Interop.SendFloat_x64(Handle, sel_display_update_granularity);
            }
        }
    }
}
