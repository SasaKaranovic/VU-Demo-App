namespace VU1WPF
{
    /// <summary>
    /// Compile-time geometry of the on-screen dial needle.
    ///
    /// All coordinates are in e-ink display pixels (the display is 200 x 144, origin top-left).
    /// The needle pivot sits below the visible display, like on the real dial, so only the
    /// upper part of the needle is visible.
    ///
    /// The defaults were derived from the scale on the bundled templates (image_pack): the
    /// tick marks lie on an arc centred at roughly (100, 225) and span about -40° .. +40°.
    /// Tweak these values to match the real hardware and rebuild.
    /// </summary>
    public static class DialGeometry
    {
        /// <summary>Needle pivot X (display pixels).</summary>
        public const double PivotX = 100;

        /// <summary>Needle pivot Y (display pixels; values &gt; 144 are below the display).</summary>
        public const double PivotY = 225;

        /// <summary>Needle angle at 0 %, in degrees (0 = straight up, negative = left).</summary>
        public const double AngleAtMin = -40;

        /// <summary>Needle angle at 100 %, in degrees.</summary>
        public const double AngleAtMax = 40;

        /// <summary>Distance from the pivot where the visible (white) needle starts.</summary>
        public const double NeedleInnerRadius = 80;

        /// <summary>Distance from the pivot where the white part turns orange.</summary>
        public const double NeedleSplitRadius = 118;

        /// <summary>Needle tip distance from the pivot.</summary>
        public const double NeedleTipRadius = 150;

        /// <summary>Needle stroke thickness in display pixels.</summary>
        public const double NeedleThickness = 2.2;

        /// <summary>Duration of the needle movement animation (ms). 0 disables animation.</summary>
        public const int NeedleAnimationMs = 450;

        /// <summary>Converts a 0..100 % value to a needle angle.</summary>
        public static double PercentToAngle(double percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            return AngleAtMin + (AngleAtMax - AngleAtMin) * percent / 100.0;
        }
    }
}
