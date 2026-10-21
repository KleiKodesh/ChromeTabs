using System;
using System.Drawing;

namespace FluentChromeTabs
{
    /// <summary>Color theme for a <see cref="FluentChromeTabsForm" />.</summary>
    public enum FluentChromeTabsTheme
    {
        /// <summary>Follow the Windows apps light/dark setting, and react when it changes.</summary>
        Auto,

        /// <summary>Always light.</summary>
        Light,

        /// <summary>Always dark.</summary>
        Dark
    }

    /// <summary>Resolved color set for painting the tab strip. Modeled on the Edge / Windows 11 title bar palette.</summary>
    internal sealed class Palette
    {
        public Color Strip;
        public Color TabActive;
        public Color TabHover;
        public Color TabBorder;
        public Color TextActive;
        public Color TextInactive;
        public Color Separator;
        public Color ButtonHover;
        public Color ButtonPressed;
        public Color CloseButtonHover;

        public static readonly Palette Light = new Palette
        {
            Strip = Color.FromArgb(243, 243, 243),
            TabActive = Color.White,
            TabHover = Color.FromArgb(222, 222, 222),
            TabBorder = Color.FromArgb(214, 214, 214),
            TextActive = Color.FromArgb(26, 26, 26),
            TextInactive = Color.FromArgb(96, 96, 96),
            Separator = Color.FromArgb(208, 208, 208),
            ButtonHover = Color.FromArgb(230, 230, 230),
            ButtonPressed = Color.FromArgb(222, 222, 222),
            CloseButtonHover = Color.FromArgb(196, 43, 28)
        };

        public static readonly Palette Dark = new Palette
        {
            Strip = Color.FromArgb(32, 32, 32),
            TabActive = Color.FromArgb(51, 51, 51),
            TabHover = Color.FromArgb(43, 43, 43),
            TabBorder = Color.FromArgb(72, 72, 72),
            TextActive = Color.FromArgb(245, 245, 245),
            TextInactive = Color.FromArgb(158, 158, 158),
            Separator = Color.FromArgb(63, 63, 63),
            ButtonHover = Color.FromArgb(45, 45, 45),
            ButtonPressed = Color.FromArgb(56, 56, 56),
            CloseButtonHover = Color.FromArgb(196, 43, 28)
        };

        /// <summary>Blends <paramref name="from" /> toward <paramref name="to" /> by <paramref name="amount" /> (0..1).</summary>
        public static Color Blend(Color from, Color to, double amount)
        {
            return Color.FromArgb(
                (int) (from.R + (to.R - from.R) * amount),
                (int) (from.G + (to.G - from.G) * amount),
                (int) (from.B + (to.B - from.B) * amount));
        }

        /// <summary>Perceived-luminance check used to pick readable text colors for arbitrary chrome colors.</summary>
        public static bool IsDarkColor(Color color)
        {
            return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B < 128;
        }

        /// <summary>
        /// WCAG contrast ratio between two opaque colors, 1.0 (identical) to 21.0
        /// (black on white). Used to check that a dimmed glyph is still visible against
        /// its surface, which a fixed blend cannot guarantee once the chrome color is
        /// user-supplied.
        /// </summary>
        public static double ContrastRatio(Color a, Color b)
        {
            double la = RelativeLuminance(a);
            double lb = RelativeLuminance(b);

            if (la < lb)
            {
                double swap = la;
                la = lb;
                lb = swap;
            }

            return (la + 0.05) / (lb + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            return 0.2126 * LinearizeChannel(color.R)
                + 0.7152 * LinearizeChannel(color.G)
                + 0.0722 * LinearizeChannel(color.B);
        }

        private static double LinearizeChannel(byte value)
        {
            double v = value / 255.0;

            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// Derives a complete palette from a single user-supplied chrome color: the tab strip takes the
        /// color itself, and tab surfaces, text, separators, and button states are derived from its
        /// perceived luminance so both dark and light custom colors stay readable.
        /// </summary>
        public static Palette FromColor(Color chrome)
        {
            bool dark = IsDarkColor(chrome);
            Color text = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
            Color tabActive = Blend(chrome, Color.White, dark ? 0.12 : 0.5);

            return new Palette
            {
                Strip = chrome,
                TabActive = tabActive,
                // Light strips sit near white, so lightening toward the active tab barely
                // shows — darken toward the text instead.
                TabHover = dark ? Blend(chrome, tabActive, 0.45) : Blend(chrome, text, 0.09),
                TabBorder = Blend(tabActive, text, 0.18),
                TextActive = text,
                TextInactive = Blend(text, chrome, 0.35),
                Separator = Blend(chrome, text, 0.25),
                ButtonHover = Blend(chrome, text, 0.08),
                ButtonPressed = Blend(chrome, text, 0.15),
                CloseButtonHover = Color.FromArgb(196, 43, 28)
            };
        }
    }
}
