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
        // ── Never covering each other ───────────────────────────────────────────

        // Our windows shown on a screen now (not minimized, not maximized, not hidden).
        private static List<Form> TileableWindowsOn(Screen screen, Form except = null)
        {
            var list = new List<Form>();
            foreach (Form w in AllAppWindows())
                if (w != except && !w.IsDisposed && w.Visible && w.IsHandleCreated && w.WindowState == FormWindowState.Normal
                    && IsDropTarget(w) && Screen.FromHandle(w.Handle).DeviceName == screen.DeviceName)
                    list.Add(w);
            return list;
        }

        /// <summary>
        /// A window let go at <paramref name="cursor"/> (a tab dropped in an empty spot, a window
        /// moved by its title bar): alone on its screen it stays where it is; otherwise it takes
        /// the place nearest the cursor, and with none left joins the window under the cursor.
        /// </summary>
        private static void TileNear(Form window, Point cursor)
        {
            if (window.IsDisposed) return;
            Screen screen = Screen.FromPoint(cursor);
            List<Form> others = TileableWindowsOn(screen, window);
            if (others.Count == 0) { TidyScreen(screen); return; }

            Form under = others.Find(w => VisibleBounds(w).Contains(cursor))
                ?? others.OrderBy(w => DistanceTo(VisibleBounds(w), cursor)).First();
            DropSide side = NearestSide(VisibleBounds(under), cursor);
            if (TryDockBeside(window, under, side)) { TidyScreen(screen); return; }
            foreach (DropSide other in new[] { DropSide.Left, DropSide.Right, DropSide.Top, DropSide.Bottom })
                if (TryDockBeside(window, under, other)) { TidyScreen(screen); return; }
            SnapGroup group = SnapGroups.Find(g => g.ScreenName == screen.DeviceName);
            int empty = group == null ? -1 : group.Slots[0].IsEmpty ? 0 : group.Slots[1].IsEmpty ? 1 : -1;
            if (empty >= 0) { DockToScreen(window, screen, CellAt(group.Rows, empty, -1)); TidyScreen(screen); return; }
            // No place left: it joins the window under the cursor (a window with a tab strip).
            MergeAllTabs(window, under is FluentChromeTabsForm ? under : others.Find(w => w is FluentChromeTabsForm) ?? under);
        }

        private static bool TryDockBeside(Form window, Form target, DropSide side)
        {
            if (!CanDockBeside(target, side, out _, out _, out _)) return false;
            DockBeside(window, target, side);
            return IsDocked(window);
        }

        /// <summary>
        /// Every window shown on the screen that is not docked while others are takes a place
        /// (an empty half, else a split of a half), or joins the largest window as tabs.
        /// </summary>
        private static void TidyScreen(Screen screen)
        {
            List<Form> shown = TileableWindowsOn(screen);
            if (shown.Count < 2) return;
            foreach (Form loose in shown.FindAll(w => !IsDocked(w)))
            {
                SnapGroup group = SnapGroups.Find(g => g.ScreenName == screen.DeviceName);
                if (group == null)
                {
                    // Two loose windows: the screen becomes two halves, the other one on the side it is on.
                    Form other = shown.Find(w => w != loose && !IsDocked(w)) ?? shown.Find(w => w != loose);
                    bool otherLeft = VisibleBounds(other).Left + VisibleBounds(other).Width / 2 <= VisibleBounds(loose).Left + VisibleBounds(loose).Width / 2;
                    DockToScreen(other, screen, otherLeft ? SnapCell.Left : SnapCell.Right);
                    if (loose == other) continue;
                    DockToScreen(loose, screen, otherLeft ? SnapCell.Right : SnapCell.Left);
                    continue;
                }
                if (IsDocked(loose)) continue;
                int empty = group.Slots[0].IsEmpty ? 0 : group.Slots[1].IsEmpty ? 1 : -1;
                if (empty >= 0) { DockToScreen(loose, screen, CellAt(group.Rows, empty, -1)); continue; }
                int half = group.Slots[0].B == null ? 0 : group.Slots[1].B == null ? 1 : -1;
                if (half >= 0 && CanDockBeside(group.Slots[half].A, group.Rows ? DropSide.Right : DropSide.Bottom, out _, out _, out _))
                {
                    DockBeside(loose, group.Slots[half].A, group.Rows ? DropSide.Right : DropSide.Bottom);
                    continue;
                }
                Form largest = shown.Where(IsDocked).OrderByDescending(w => w is FluentChromeTabsForm).ThenByDescending(w => VisibleBounds(w).Width * VisibleBounds(w).Height).First();
                MergeAllTabs(loose, largest);
            }
        }

        private static void MergeAllTabs(Form source, Form target)
        {
            if (target.IsDisposed || !(target is FluentChromeTabsForm into)) return;
            if (!(source is FluentChromeTabsForm from))
            {
                // A tool window becomes a tab there.
                source.BeginInvoke(new Action(() => _host?.MergeIntoTabs(source, into)));
                into.Activate();
                return;
            }
            var tabs = new List<FluentTab>(from.Tabs);
            if (tabs.Count == 0) return;
            source.BeginInvoke(new Action(() => { _ = MergeWindowIntoAsync(from, tabs, into, -1); }));
            target.Activate();
        }

        private static double DistanceTo(Rectangle r, Point p)
        {
            int dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right), dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            return Math.Sqrt(dx * (double)dx + dy * (double)dy);
        }

        // The side of a rectangle nearest the point, by share of its width and height.
        private static DropSide NearestSide(Rectangle r, Point p)
        {
            double x = (p.X - r.Left) / (double)Math.Max(1, r.Width), y = (p.Y - r.Top) / (double)Math.Max(1, r.Height);
            double toLeft = x, toRight = 1 - x, toTop = y, toBottom = 1 - y;
            double nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
            if (nearest == toLeft) return DropSide.Left;
            if (nearest == toRight) return DropSide.Right;
            return nearest == toTop ? DropSide.Top : DropSide.Bottom;
        }

        // Minimized docked windows: their place, for when they come back.
        private static readonly Dictionary<Form, KeyValuePair<Screen, SnapCell>> MinimizedPlaces = new Dictionary<Form, KeyValuePair<Screen, SnapCell>>();
    }
}
