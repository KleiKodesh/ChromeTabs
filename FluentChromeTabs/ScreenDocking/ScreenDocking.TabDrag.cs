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
    // Dragging a tab out of the strip - VS Code's way. What follows the cursor is the TAB (a
    // small floating copy with a preview of its page), never a window, so nothing is decided
    // until the button is released, and Windows' own Snap never fights the drag:
    //   the very corner / edge of a screen -> that quarter / half, even over a docked window
    //   over a tab strip           -> the tab goes into that strip at that spot
    //   at the edge of one of our
    //     windows                  -> it docks beside (or above/below) that one
    //   over the middle of another
    //     window with a tab strip  -> it joins that window's tabs, as on its strip
    //   near a screen's edge/corner -> that half / quarter of the screen
    //   anywhere else              -> a window there, tiled near the cursor if others are shown
    //   Escape                     -> nothing happens
    // A window's only tab moves the window itself instead of opening a new one.
    public static partial class ScreenDocking
    {
        // How much of a window's width (left/right) or height (top/bottom), from each edge,
        // places the tab beside it. The middle takes the tab in, like the strip.
        private const double DropZoneShare = 0.3;
        // The same for a screen, off the app: a smaller share, the screen being much larger.
        private const double ScreenZoneShare = 0.12;

        // IntoWindow: the middle of a window - the tab joins its tabs, at the end, as on its strip.
        // ScreenCell: a screen's half or quarter (ScreenDocking.Layout.cs).
        private enum DropKind { None, Strip, IntoWindow, BesideWindow, ScreenCell, Free }

        // The tab drag in progress (one at a time: there is one mouse).
        private static TabDragGhost _ghost;
        private static DropKind _dropKind;
        private static Form _dropWindow;       // Strip: that window's strip; BesideWindow: that window
        private static DropSide _dropSide;
        private static Rectangle _dropArea;        // ScreenCell: the cell
        private static SnapCell _dropCell;         // ScreenCell: which one
        private static SnapCell _dropCellAtDrop;
        private static Screen _dropScreenAtDrop;
        private static Screen _dropScreen;         // ScreenCell: on which screen
        private static DropZonePreview _dropPreview;

        // A window whose tab strip a window being MOVED (title bar) is over: on release the
        // moved window's tabs go in there.
        private static FluentChromeTabsForm _stripTarget;
        private static Form _mergingWindow;

        /// <summary>A side of a window (where a tab dropped at its edge goes).</summary>
        public enum DropSide { Left, Right, Top, Bottom }

        // ── A tab dragged out of a strip (FluentChromeTabsForm.TabDragOutside) ──────────

        private static void OnTabDragOutside(FluentChromeTabsForm source, FluentTabDragOutsideEventArgs e)
        {
            switch (e.Phase)
            {
                case FluentTabDragPhase.Started:
                    EndTabDragVisuals();
                    // A window's only tab moves its own window: nothing to start.
                    _ghost = new TabDragGhost(e.Tab.Title, e.Tab.Icon, source.DeviceDpi);
                    _ghost.MoveTo(e.ScreenPoint);
                    _ghost.Show();
                    ShowPicture(_ghost, CapturePicture(source));
                    UpdateTabDropTarget(source, e.ScreenPoint);
                    break;
                case FluentTabDragPhase.Moved:
                    _ghost?.MoveTo(e.ScreenPoint);
                    UpdateTabDropTarget(source, e.ScreenPoint);
                    break;
                case FluentTabDragPhase.Returned:
                case FluentTabDragPhase.Cancelled:
                    EndTabDragVisuals();
                    break;
                case FluentTabDragPhase.Dropped:
                    DropKind kind = _dropKind;
                    Form target = _dropWindow;
                    DropSide side = _dropSide;
                    Rectangle area = _dropArea;
                    _dropCellAtDrop = _dropCell;
                    _dropScreenAtDrop = _dropScreen;
                    EndTabDragVisuals();
                    DropTab(source, e.Tab, e.ScreenPoint, kind, target, side, area);
                    break;
            }
        }

        private static async void ShowPicture(TabDragGhost ghost, Task<Image> picture)
        {
            Image image = null;
            try { image = await picture; } catch (Exception) { }
            if (image != null && !ghost.IsDisposed) ghost.SetPicture(image);
        }

        private static void UpdateTabDropTarget(FluentChromeTabsForm source, Point cursor)
            => UpdateDropTarget(source, cursor, _ghost != null && _ghost.IsHandleCreated ? _ghost.Handle : IntPtr.Zero);

        // Where a drop at the cursor goes - for a dragged tab, `ignore` is its floating card; for a
        // window moved by its title bar, the window itself (it is under the cursor).
        private static void UpdateDropTarget(Form source, Point cursor, IntPtr ignore)
        {
            IntPtr ghost = ignore;

            // Shift held (as with Windows Snap): no zones - the tab goes exactly where it is let go.
            if ((Control.ModifierKeys & Keys.Shift) != 0)
            {
                SetDropTarget(DropKind.Free, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            if (TryScreenEdgeTarget(cursor)) return;

            // Over a strip: the tab goes in there. Its own strip is where the drag came from.
            FluentChromeTabsForm strip = FluentChromeTabsForm.FormWithStripAt(cursor, ghost);
            if (strip != null && strip != source && IsDropTarget(strip))
            {
                SetDropTarget(DropKind.Strip, strip, DropSide.Left, Rectangle.Empty);
                return;
            }
            if (strip == source)
            {
                SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            // A screen's corner is a quarter of that screen even over one of our windows (a docked
            // window reaches into the corner; its own edge zones would win there otherwise).
            Screen cornerScreen = Screen.FromPoint(cursor);
            SnapCell? corner = ScreenZone(cornerScreen.WorkingArea, cursor);
            if (corner.HasValue && !IsHalf(corner.Value))
            {
                _dropCell = corner.Value;
                _dropScreen = cornerScreen;
                SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(cornerScreen, corner.Value));
                return;
            }

            // Over one of our windows: the zone at its nearest edge places the tab beside it
            // (or above or below it); the middle of another window takes the tab in, like its strip.
            Form under = OurWindowAt(cursor, ghost);
            if (under != null)
            {
                Rectangle visible = VisibleBounds(under);
                DropSide? side = EdgeZone(visible, cursor);
                // A window's only tab dropped on its own window has nothing to be beside.
                bool alone = under == source && TabCountOf(source) <= 1;
                // Only where two windows fit (SplitArea): otherwise the middle's rule applies.
                if (side.HasValue && !alone && DockBesidePreview(under, side.Value).HasValue)
                    SetDropTarget(DropKind.BesideWindow, under, side.Value, Rectangle.Empty);
                // A tab joins only a window with a tab strip (a tool window has none).
                else if (under != source && under is FluentChromeTabsForm && IsDropTarget(under))
                    SetDropTarget(DropKind.IntoWindow, under, DropSide.Left, Rectangle.Empty);
                // Its own window away from the edges: it pops out as a window of its own, there.
                else if (under == source && !alone)
                    SetDropTarget(DropKind.Free, null, DropSide.Left, Rectangle.Empty);
                else
                    SetDropTarget(DropKind.None, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            // Just outside one of our windows: free room to pop the tab out beside it, never a snap.
            if (NearOurWindow(cursor, ghost))
            {
                SetDropTarget(DropKind.Free, null, DropSide.Left, Rectangle.Empty);
                return;
            }

            // Off the app: a screen's corner gives that quarter, the zone at its nearest edge that
            // half (Windows Snap's places); the middle opens a window where the tab is let go.
            Screen screen = Screen.FromPoint(cursor);
            SnapCell? cell = ScreenZone(screen.WorkingArea, cursor);
            if (cell.HasValue)
            {
                _dropCell = cell.Value;
                _dropScreen = screen;
                SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(screen, cell.Value));
                return;
            }

            // Open desktop, away from every zone: a window of its own right there, not docked.
            SetDropTarget(DropKind.Free, null, DropSide.Left, Rectangle.Empty);
        }

        // How close to a screen's corner (a share of its width and of its height) gives a quarter.
        private const double ScreenCornerShare = 0.08;
        // The part of that which wins even over a tab strip reaching into the corner.
        private const double ScreenCornerTightShare = 0.04;
        private const double ScreenEdgeTightShare = 0.012;

        // The free band around each of our windows, as a share of the screen's width.
        private const double WindowPaddingShare = 0.08;

        private static bool NearOurWindow(Point cursor, IntPtr ignore)
        {
            int pad = (int)(Screen.FromPoint(cursor).WorkingArea.Width * WindowPaddingShare);
            foreach (Form window in AllAppWindows())
            {
                if (window.IsDisposed || !window.Visible || window.WindowState == FormWindowState.Minimized) continue;
                if (window.IsHandleCreated && window.Handle == ignore) continue;
                Rectangle around = VisibleBounds(window);
                around.Inflate(pad, pad);
                if (around.Contains(cursor)) return true;
            }
            return false;
        }

        /// <summary>
        /// The very corner of a screen is that quarter, and a thin band along its edge that half -
        /// even over a docked window's strip or edge there, and for a window moved by its title bar
        /// too: the band is wider than where Windows Snap starts, so ours comes first.
        /// </summary>
        private static bool TryScreenEdgeTarget(Point cursor)
        {
            Screen s = Screen.FromPoint(cursor);
            Rectangle wa = s.WorkingArea;
            double cx = (cursor.X - wa.Left) / (double)Math.Max(1, wa.Width), cy = (cursor.Y - wa.Top) / (double)Math.Max(1, wa.Height);
            bool nearX = cx <= ScreenCornerTightShare || cx >= 1 - ScreenCornerTightShare;
            bool nearY = cy <= ScreenCornerTightShare || cy >= 1 - ScreenCornerTightShare;
            SnapCell? tight = nearX && nearY ? ScreenZone(wa, cursor) : null;
            if (!tight.HasValue)
            {
                if (cx <= ScreenEdgeTightShare) tight = SnapCell.Left;
                else if (cx >= 1 - ScreenEdgeTightShare) tight = SnapCell.Right;
                else if (cy <= ScreenEdgeTightShare) tight = SnapCell.Top;
                else if (cy >= 1 - ScreenEdgeTightShare) tight = SnapCell.Bottom;
            }
            if (!tight.HasValue) return false;
            _dropCell = tight.Value;
            _dropScreen = s;
            SetDropTarget(DropKind.ScreenCell, null, DropSide.Left, ScreenCellRect(s, tight.Value));
            return true;
        }

        private static SnapCell? ScreenZone(Rectangle work, Point cursor)
        {
            double x = (cursor.X - work.Left) / (double)Math.Max(1, work.Width);
            double y = (cursor.Y - work.Top) / (double)Math.Max(1, work.Height);
            bool left = x <= ScreenCornerShare, right = x >= 1 - ScreenCornerShare;
            bool top = y <= ScreenCornerShare, bottom = y >= 1 - ScreenCornerShare;
            if (top && left) return SnapCell.TopLeft;
            if (top && right) return SnapCell.TopRight;
            if (bottom && left) return SnapCell.BottomLeft;
            if (bottom && right) return SnapCell.BottomRight;
            DropSide? side = EdgeZone(work, cursor, ScreenZoneShare);
            if (!side.HasValue) return null;
            switch (side.Value)
            {
                case DropSide.Left: return SnapCell.Left;
                case DropSide.Right: return SnapCell.Right;
                case DropSide.Top: return SnapCell.Top;
                default: return SnapCell.Bottom;
            }
        }

        // The edge zone of a window the cursor is in (the nearest edge, when within its
        // share of the window), or null in the middle.
        private static DropSide? EdgeZone(Rectangle visible, Point cursor, double share = DropZoneShare)
        {
            double x = (cursor.X - visible.Left) / (double)Math.Max(1, visible.Width);
            double y = (cursor.Y - visible.Top) / (double)Math.Max(1, visible.Height);
            double toLeft = x, toRight = 1 - x, toTop = y, toBottom = 1 - y;
            double nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
            if (nearest > share) return null;
            if (nearest == toLeft) return DropSide.Left;
            if (nearest == toRight) return DropSide.Right;
            return nearest == toTop ? DropSide.Top : DropSide.Bottom;
        }

        private static void SetDropTarget(DropKind kind, Form window, DropSide side, Rectangle area)
        {
            _dropKind = kind;
            _dropWindow = window;
            _dropSide = side;
            _dropArea = area;

            // Strip and window drops mark the strip too: that is where the tab will appear.
            // The window-wide zone already shows where the tab goes: the strip lights up only for a strip drop.
            SetStripTarget(kind == DropKind.Strip ? window as FluentChromeTabsForm : null);
            if (kind == DropKind.IntoWindow) ShowDropPreview(VisibleBounds(window));
            else if (kind == DropKind.BesideWindow) ShowDropPreview(DockBesidePreview(window, side).Value);
            else if (kind == DropKind.ScreenCell) ShowDropPreview(area);
            else HideDropPreview();
        }

        private static void EndTabDragVisuals()
        {
            if (_ghost != null && !_ghost.IsDisposed) _ghost.Dispose();
            _ghost = null;
            _dropKind = DropKind.None;
            _dropWindow = null;
            SetStripTarget(null);
            HideDropPreview();
        }

        private static void DropTab(FluentChromeTabsForm source, FluentTab tab, Point cursor,
            DropKind kind, Form target, DropSide side, Rectangle area)
        {
            if (source.IsDisposed) return;
            bool onlyTab = TabCountOf(source) <= 1;

            switch (kind)
            {
                case DropKind.Strip:
                case DropKind.IntoWindow:
                    if (target == null || target.IsDisposed) return;
                    int position = kind == DropKind.Strip && target is FluentChromeTabsForm strip ? strip.DropIndexAtScreenPoint(cursor) : -1;
                    MoveTab(source, tab, target, position);
                    target.Activate();
                    return;

                case DropKind.BesideWindow:
                    if (target == null || target.IsDisposed || !DockBesidePreview(target, side).HasValue) return;
                    if (onlyTab) { DockBeside(source, target, side); TidyScreen(Screen.FromHandle(target.Handle)); }
                    else OpenWindowForTab(source, tab, window => { DockBeside(window, target, side); TidyScreen(Screen.FromHandle(target.Handle)); });
                    return;

                case DropKind.ScreenCell:
                    Screen screen = _dropScreenAtDrop;
                    SnapCell cell = _dropCellAtDrop;
                    if (onlyTab) { DockToScreen(source, screen, cell); TidyScreen(screen); }
                    else OpenWindowForTab(source, tab, window => { DockToScreen(window, screen, cell); TidyScreen(screen); });
                    return;

                case DropKind.Free:
                    // A free window where the tab was let go: the cursor on its strip, as it held the tab.
                    Rectangle normal = PreDockBounds.TryGetValue(source, out Rectangle before) ? before
                        : source.WindowState == FormWindowState.Normal ? source.Bounds : source.RestoreBounds;
                    Rectangle free = KeepOnScreen(new Rectangle(cursor.X - normal.Width / 2, cursor.Y - 16, normal.Width, normal.Height), cursor);
                    if (onlyTab)
                    {
                        Undock(source, restoreSize: false);
                        if (source.WindowState != FormWindowState.Normal) source.WindowState = FormWindowState.Normal;
                        source.Bounds = free;
                        source.Activate();
                    }
                    else OpenWindowForTab(source, tab, window => { window.Bounds = free; });
                    return;

                default:
                    // Over another program's window, or one of ours that takes no drops: cancelled, like Escape.
                    return;
            }
        }

        // A dropped window lands whole on the screen it was dropped on: no larger than its
        // working area, and moved in from any edge it would cross.
        private static Rectangle KeepOnScreen(Rectangle bounds, Point cursor)
        {
            Rectangle work = Screen.FromPoint(cursor).WorkingArea;
            int width = Math.Min(bounds.Width, work.Width), height = Math.Min(bounds.Height, work.Height);
            int left = Math.Max(work.Left, Math.Min(bounds.Left, work.Right - width));
            int top = Math.Max(work.Top, Math.Min(bounds.Top, work.Bottom - height));
            return new Rectangle(left, top, width, height);
        }

        private static Rectangle HalfOf(Rectangle area, DropSide side)
        {
            switch (side)
            {
                case DropSide.Left: return new Rectangle(area.Left, area.Top, area.Width / 2, area.Height);
                case DropSide.Right: return new Rectangle(area.Left + area.Width / 2, area.Top, area.Width - area.Width / 2, area.Height);
                case DropSide.Top: return new Rectangle(area.Left, area.Top, area.Width, area.Height / 2);
                default: return new Rectangle(area.Left, area.Top + area.Height / 2, area.Width, area.Height - area.Height / 2);
            }
        }

        private static DropSide Opposite(DropSide side)
        {
            switch (side)
            {
                case DropSide.Left: return DropSide.Right;
                case DropSide.Right: return DropSide.Left;
                case DropSide.Top: return DropSide.Bottom;
                default: return DropSide.Top;
            }
        }

        // The topmost of our windows at the point, looking past `ignore` (the drag ghost);
        // null when another program's window covers the point.
        private static Form OurWindowAt(Point cursor, IntPtr ignore)
        {
            IntPtr preview = _dropPreview != null && _dropPreview.IsHandleCreated ? _dropPreview.Handle : IntPtr.Zero;
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (h == ignore || h == preview || !IsWindowVisible(h)) continue;
                RECT r;
                if (!GetWindowRect(h, out r)) continue;
                if (cursor.X < r.Left || cursor.X >= r.Right || cursor.Y < r.Top || cursor.Y >= r.Bottom) continue;
                foreach (Form open in AllAppWindows())
                    if (!open.IsDisposed && open.IsHandleCreated && open.Handle == h
                        && open.WindowState != FormWindowState.Minimized && IsDropTarget(open))
                        return open;
                return null;
            }
            return null;
        }
    }
}
