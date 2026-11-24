using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    public static partial class ScreenDocking
    {
        // ── For hosts and tests: docking without a drag ──────────────────────────

        /// <summary>Docks a window into a screen's half or quarter (as a tab dropped there would).</summary>
        public static void Dock(Form window, Screen screen, SnapCell cell)
        {
            DockToScreen(window, screen, cell);
            TidyScreen(screen);
        }

        /// <summary>Docks a window at a side of another (as a tab dropped at that edge would).</summary>
        public static void DockNextTo(Form window, Form target, DropSide side)
        {
            DockBeside(window, target, side);
            TidyScreen(Screen.FromHandle(target.Handle));
        }

        /// <summary>The window's place: "free", or its cell and the lines of its group.</summary>
        public static string Describe(Form window)
        {
            SnapGroup g = GroupOf(window, out int slot, out int part);
            if (g == null) return "free";
            return "docked " + CellAt(g.Rows, slot, part) + " split=" + g.Split.ToString("0.00")
                + " slotSplit=" + g.Slots[slot].Split.ToString("0.00") + " cell=" + RectOf(g, slot, part < 0 ? 0 : part);
        }

        /// <summary>How many screens hold a docked group.</summary>
        public static int GroupCount => SnapGroups.Count;

        /// <summary>What the eye sees of a window (its frame without the invisible resize borders).</summary>
        public static Rectangle VisibleBoundsOf(Form form) => VisibleBounds(form);

        /// <summary>An edge drag of a window (WMSZ_* edge) to the proposed rectangle: what it becomes; the others follow.</summary>
        public static Rectangle ResizeEdge(Form window, int edge, Rectangle proposed)
        {
            var r = new RECT { Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom };
            OnWindowSizing(window, edge, ref r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>A title-bar move of a window to the proposed rectangle: what it becomes (a docked one leaves its place).</summary>
        public static Rectangle MoveBy(Form window, Rectangle proposed)
        {
            var r = new RECT { Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom };
            OnWindowMovingRect(window, ref r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }
    }
}
