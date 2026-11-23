using System;
using System.Drawing;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    /// <summary>
    /// Tab mouse interaction: selection, closing, drag-reorder within the strip, tearing a tab off
    /// into its own window, and dropping a tab onto another <see cref="FluentChromeTabsForm" />.
    /// </summary>
    public partial class FluentChromeTabsForm
    {
        private int _mouseDownTab = -1;
        private bool _mouseDownOnClose;
        private Point _mouseDownPoint;
        private bool _mouseDownWasTouch;

        private bool _dragging;
        private int _dragIndex = -1;
        private int _dragOffsetX;
        private int _dragVisualX;
        private int _dragStartGroup;

        private bool _dividerDragging;

        // The window a drag started in. A drag dropped onto another window continues there,
        // so the target needs to know where the tab came from.
        private FluentChromeTabsForm _dragOrigin;

        // On the origin window: its tab, while the drag carrying it is in another window.
        private FluentTab _tabAwayInDrag;

        /// <summary>
        /// When true, a host moves tabs between windows itself (its tabs stand for content the
        /// library cannot move). A tab dragged out of the strip is then not torn off: the drag
        /// goes on with the tab still on this strip, and is reported through
        /// <see cref="TabDragOutside" /> - the host draws what follows the cursor and decides,
        /// on release, where the tab goes. Back over the strip, the tab reorders again.
        /// Needs <see cref="AllowTabDetach" />.
        /// </summary>
        public bool HostMovesTabsBetweenWindows { get; set; }

        /// <summary>
        /// A tab being dragged outside this strip (<see cref="HostMovesTabsBetweenWindows" />):
        /// it left the strip, moved, came back, was dropped somewhere, or the drag was given up
        /// (Escape, the mouse taken away).
        /// </summary>
        public event EventHandler<FluentTabDragOutsideEventArgs> TabDragOutside;

        // The dragged tab is outside the strip (host mode).
        private bool _dragOutside;
        private DragEscapeFilter _escapeFilter;

        /// <summary>The tab being dragged on this strip, or null.</summary>
        public FluentTab DraggedTab => _dragging && _dragIndex >= 0 && _dragIndex < _tabs.Count ? _tabs[_dragIndex] : null;

        /// <summary>The window the tab being dragged here came from (this one for a drag that started here), or null.</summary>
        public FluentChromeTabsForm DraggedTabOrigin => _dragging ? _dragOrigin : null;

        /// <summary>This window's tab while a drag carries it over another window, else null.</summary>
        public FluentTab TabAwayInDrag => _tabAwayInDrag;

        /// <summary>Hands the pressed mouse button over to a native move of this window (as dragging a caption).</summary>
        public void BeginWindowMoveWithCursor()
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        }

        /// <summary>How far the cursor may leave the strip vertically before the tab tears off, in logical pixels.</summary>
        private const int DetachThreshold = 36;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (!StripVisible || e.Y >= StripHeightPx)
            {
                return;
            }

            // The split divider is grabbed before anything else — it sits between regions
            if (e.Button == MouseButtons.Left && SplitStrip && DividerHitRect.Contains(e.Location))
            {
                _dividerDragging = true;
                Capture = true;
                InvalidateStrip();
                return;
            }

            // Scroll chevrons are tested before the tabs: a scrolled-out tab passes
            // underneath them, and clicking the chevron must scroll rather than select
            // whatever happens to be behind it.
            if (e.Button == MouseButtons.Left)
            {
                for (int group = 0; group < GroupCount; group++)
                {
                    if (!GroupOverflows(group))
                    {
                        continue;
                    }
                    if (ScrollChevronRect(group, true).Contains(e.Location))
                    {
                        ScrollGroupByTab(group, -1);
                        return;
                    }
                    if (ScrollChevronRect(group, false).Contains(e.Location))
                    {
                        ScrollGroupByTab(group, 1);
                        return;
                    }
                }
            }

            int tab = HitTab(e.Location);

            if (e.Button == MouseButtons.Left)
            {
                if (tab >= 0)
                {
                    _mouseDownOnClose = TabShowsClose(tab) && TabCloseRect(TabRect(tab)).Contains(e.Location);
                    _mouseDownTab = tab;
                    _mouseDownPoint = e.Location;
                    _mouseDownWasTouch = NativeMethods.IsPromotedTouchMessage();

                    if (!_mouseDownOnClose)
                    {
                        FluentTab pressed = _tabs[tab];
                        SelectTabCore(tab);
                        TabPressed?.Invoke(this, new FluentTabEventArgs(pressed));
                    }
                }
                else
                {
                    for (int group = 0; group < GroupCount; group++)
                    {
                        if (ShowNewTabButton && NewTabButtonRect(group).Contains(e.Location))
                        {
                            RequestNewTab(group);
                            break;
                        }

                        if (ShowTabListButton && TabListButtonRect(group).Contains(e.Location))
                        {
                            ShowTabListMenu(group);
                            break;
                        }
                    }
                }
            }
            else if (e.Button == MouseButtons.Middle && tab >= 0 && _tabs[tab].CanClose)
            {
                CloseTab(_tabs[tab]);
            }
            else if (e.Button == MouseButtons.Right && tab >= 0)
            {
                // Deliberately does NOT select the tab: right-clicking is asking about a
                // tab, not switching to it, and stealing the selection would swap the
                // user's content out from under them just to read a menu. The menu
                // targets the clicked tab regardless of what is selected.
                ShowTabContextMenu(_tabs[tab], e.Location);
            }
        }

        /// <summary>Wheeling over an overflowing strip scrolls it, as in a browser.</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            if (!StripVisible || e.Y >= StripHeightPx || e.Delta == 0)
            {
                return;
            }

            for (int group = 0; group < GroupCount; group++)
            {
                if (!GroupOverflows(group))
                {
                    continue;
                }
                if (e.X >= GroupTabsLeftPx(group) && e.X <= GroupTabsRightPx(group))
                {
                    ScrollGroupByTab(group, e.Delta > 0 ? -1 : 1);
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            SetHotCaptionButton(0);

            if (_dividerDragging)
            {
                // Live resize, mirrored to the host so the app's split view follows in tandem.
                // Dragging leaves pixel-pinned mode; the host re-pins from its next layout.
                _dividerOverrideLeft = -1;
                SplitRatio = (double) e.X / Math.Max(1, ClientSize.Width);
                SplitRatioChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_dragging)
            {
                ContinueDrag(e.Location);
                return;
            }

            if (_mouseDownTab >= 0 && !_mouseDownOnClose && e.Button == MouseButtons.Left)
            {
                int dx = Math.Abs(e.X - _mouseDownPoint.X);
                int dy = Math.Abs(e.Y - _mouseDownPoint.Y);

                // A finger jitters more than a mouse — with the mouse threshold a plain
                // tap can misread as the start of a drag
                int thresholdX = _mouseDownWasTouch ? Dpi(10) : SystemInformation.DragSize.Width;
                int thresholdY = _mouseDownWasTouch ? Dpi(10) : SystemInformation.DragSize.Height;

                if (dx > thresholdX || dy > thresholdY)
                {
                    BeginDrag(e.Location);
                    return;
                }
            }

            // A trailing promoted move can arrive after a touch-up, parked at the lift
            // point — it must not re-apply the hover the up just cleared
            if (e.Button == MouseButtons.None && NativeMethods.IsPromotedTouchMessage())
            {
                return;
            }

            UpdateHoverState(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_dividerDragging)
            {
                _dividerDragging = false;
                Capture = false;
                InvalidateStrip();
            }
            else if (_dragging)
            {
                FinishDrop();
            }
            else if (_mouseDownTab >= 0 && _mouseDownOnClose && e.Button == MouseButtons.Left)
            {
                int tab = HitTab(e.Location);

                if (tab == _mouseDownTab && TabCloseRect(TabRect(tab)).Contains(e.Location))
                {
                    CloseTab(_tabs[tab]);
                }
            }

            _mouseDownTab = -1;
            _mouseDownOnClose = false;
            _mouseDownWasTouch = false;

            // A lifted finger leaves no cursor behind to move away, so without this the
            // tab under the last tap would keep its hover highlight indefinitely
            if (NativeMethods.IsPromotedTouchMessage())
            {
                ClearHoverState();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ClearHoverState();
        }

        private void BeginDrag(Point p)
        {
            // A slide still running would fight the drag: the dragged tab is painted at the
            // cursor with no offset while its neighbours are mid-flight, and reordering
            // re-keys those offsets onto positions they were never measured for. The scroll
            // glide goes for the same reason — the drag positions tabs against the settled
            // offset, so a band still sliding would put them under the wrong cursor spot.
            StopReflowAnimation();
            StopScrollGlide();

            // Dragging the only tab of a window drags the whole window, like Chrome. A host
            // moving tabs itself lets it be dropped onto another window first (ContinueDrag
            // moves the window once it leaves the strip).
            if (_tabs.Count == 1 && !HostMovesTabsBetweenWindows)
            {
                _mouseDownTab = -1;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
                return;
            }

            _dragging = true;
            _dragOrigin = this;
            _dragIndex = _mouseDownTab;
            _dragStartGroup = GroupOf(_tabs[_dragIndex]);
            _dragOffsetX = _mouseDownPoint.X - TabRect(_dragIndex).Left;
            _dragVisualX = p.X - _dragOffsetX;

            ClearHoverState();
            InvalidateStrip();
        }

        private void ContinueDrag(Point p)
        {
            if (HostMovesTabsBetweenWindows && AllowTabDetach)
            {
                // Off the strip (either side of it, or beyond the window): the host takes the
                // drag from here until the tab comes back or is let go.
                bool outside = p.Y < -Dpi(DetachThreshold) || p.Y > StripHeightPx + Dpi(DetachThreshold)
                    || p.X < 0 || p.X >= ClientSize.Width;
                if (outside)
                {
                    FluentTabDragPhase phase = _dragOutside ? FluentTabDragPhase.Moved : FluentTabDragPhase.Started;
                    if (!_dragOutside)
                    {
                        _dragOutside = true;
                        _escapeFilter = new DragEscapeFilter(this);
                        Application.AddMessageFilter(_escapeFilter);
                    }
                    RaiseDragOutside(phase);
                    return;
                }
                if (_dragOutside)
                {
                    StopDragOutside();
                    RaiseDragOutside(FluentTabDragPhase.Returned);
                }
            }
            else
            {
                // Dropping onto another FluentChromeTabsForm's strip transfers the tab there
                if (AllowTabDetach && TryTransferToOtherWindow())
                {
                    return;
                }

                // Leaving the strip vertically tears the tab off into its own window
                if (AllowTabDetach && (p.Y < -Dpi(DetachThreshold) || p.Y > StripHeightPx + Dpi(DetachThreshold)))
                {
                    DetachDraggedTab();
                    return;
                }
            }

            // Split strip: crossing the divider moves the dragged tab into the other region.
            // Reassign its group live so it previews in place; the move is reported to the
            // host on drop (EndDrag). Everything here is in logical client coordinates, so it
            // works identically in a mirrored (RTL) window.
            if (_splitStrip)
            {
                int cursorGroup = p.X >= DividerXPx ? 1 : 0;

                if (cursorGroup != GroupOf(_tabs[_dragIndex]))
                {
                    _tabs[_dragIndex].Group = cursorGroup;

                    // The target region can size tabs differently — keep the grab point sane
                    _dragOffsetX = Math.Max(0, Math.Min(_dragOffsetX, TabWidthPx(_dragIndex, cursorGroup) - Dpi(16)));
                }
            }

            // Reorder is confined to the dragged tab's own split region
            int group = GroupOf(_tabs[_dragIndex]);
            int count = GroupTabCount(group);
            int tabsLeft = GroupTabsLeftPx(group);
            // The dragged tab is the active one, so it carries the widened size; the strip
            // can only travel by the rest of the content's width.
            int maxX = tabsLeft + GroupContentWidthPx(group) - TabWidthPx(_dragIndex, group);

            _dragVisualX = Math.Max(tabsLeft, Math.Min(p.X - _dragOffsetX, maxX));

            // Resolved against real tab positions rather than a uniform slot: widths vary,
            // so dividing by one slot size drifts past the widened tab. The card's midpoint
            // is what decides, matching how the drop reads visually.
            int target = DropIndexAtPx(group, _dragVisualX + TabWidthPx(_dragIndex, group) / 2);
            target = Math.Max(0, Math.Min(target, count - 1));

            if (target != GroupPosition(_dragIndex))
            {
                FluentTab dragged = _tabs[_dragIndex];
                FluentTab selected = SelectedTab;
                int targetGlobal = GlobalIndexOfGroupPosition(group, target);

                _tabs.RemoveAt(_dragIndex);
                _tabs.Insert(Math.Min(targetGlobal, _tabs.Count), dragged);

                _selectedIndex = selected == null ? -1 : _tabs.IndexOf(selected);
                _dragIndex = _tabs.IndexOf(dragged);
            }

            InvalidateStrip();
        }

        /// <summary>The mouse was released mid-drag: outside the strip, the host gets the drop.</summary>
        private void FinishDrop()
        {
            if (_dragOutside)
            {
                FluentTab dropped = DraggedTab;
                StopDragOutside();
                EndDrag();
                Capture = false;
                if (dropped != null)
                {
                    TabDragOutside?.Invoke(this, new FluentTabDragOutsideEventArgs(dropped, Cursor.Position, FluentTabDragPhase.Dropped));
                }
                return;
            }

            EndDrag();
        }

        /// <summary>The drag outside the strip is given up (Escape, or the mouse was taken away).</summary>
        internal void CancelDragOutside()
        {
            if (!_dragging || !_dragOutside) return;
            FluentTab dragged = DraggedTab;
            StopDragOutside();
            EndDrag();
            Capture = false;
            _mouseDownTab = -1;
            if (dragged != null)
            {
                TabDragOutside?.Invoke(this, new FluentTabDragOutsideEventArgs(dragged, Cursor.Position, FluentTabDragPhase.Cancelled));
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) CancelDragOutside();
        }

        private void RaiseDragOutside(FluentTabDragPhase phase)
        {
            FluentTab dragged = DraggedTab;
            if (dragged != null)
            {
                TabDragOutside?.Invoke(this, new FluentTabDragOutsideEventArgs(dragged, Cursor.Position, phase));
            }
        }

        private void StopDragOutside()
        {
            _dragOutside = false;
            if (_escapeFilter != null)
            {
                Application.RemoveMessageFilter(_escapeFilter);
                _escapeFilter = null;
            }
        }

        /// <summary>Escape gives up a drag outside the strip, wherever the keyboard focus is.</summary>
        private sealed class DragEscapeFilter : IMessageFilter
        {
            private readonly FluentChromeTabsForm _form;

            public DragEscapeFilter(FluentChromeTabsForm form)
            {
                _form = form;
            }

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_KEYDOWN = 0x0100, VK_ESCAPE = 0x1B;
                if (m.Msg != WM_KEYDOWN || m.WParam.ToInt32() != VK_ESCAPE) return false;
                _form.BeginInvoke(new Action(_form.CancelDragOutside));
                return true;
            }
        }

        private void EndDrag()
        {
            // Report a cross-region move to the host once, on drop. The tab's group was
            // already reassigned live during the drag; fire only when it truly changed.
            if (_dragIndex >= 0 && _dragIndex < _tabs.Count && _splitStrip)
            {
                FluentTab dropped = _tabs[_dragIndex];
                int group = GroupOf(dropped);

                if (group != _dragStartGroup)
                {
                    TabDraggedToGroup?.Invoke(this, new FluentTabGroupEventArgs(dropped, group));
                }
            }

            _dragging = false;
            _dragIndex = -1;
            _dragOrigin = null;
            InvalidateStrip();
        }

        /// <summary>Tears the dragged tab off into a new window and hands the drag over to the native window move.</summary>
        private void DetachDraggedTab()
        {
            FluentTab tab = _tabs[_dragIndex];
            Point screenCursor = Cursor.Position;
            int dragOffsetX = _dragOffsetX;

            EndDrag();
            Capture = false;
            _mouseDownTab = -1;

            DetachTabCore(tab);

            FluentChromeTabsForm newWindow = CreateDetachedWindow();
            newWindow.Theme = _theme;
            newWindow.CustomThemeColor = _customThemeColor;
            newWindow.StartPosition = FormStartPosition.Manual;
            newWindow.Size = NativeMethods.IsZoomed(Handle) ? RestoreBounds.Size : Size;
            newWindow.Location = new Point(screenCursor.X - dragOffsetX - newWindow.GroupTabsLeftPx(0), screenCursor.Y - newWindow.StripHeightPx / 2);
            newWindow.AddTab(tab);
            newWindow.Show();
            newWindow.Activate();

            // Hand off to a native window move so the user keeps dragging the new window seamlessly
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(newWindow.Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        }

        /// <summary>
        /// Creates the window used when a tab is torn off. Override to customize; the default creates
        /// a new instance of the current type when it has a parameterless constructor.
        /// </summary>
        protected virtual FluentChromeTabsForm CreateDetachedWindow()
        {
            try
            {
                FluentChromeTabsForm window = (FluentChromeTabsForm) Activator.CreateInstance(GetType());

                // A subclass constructor may have pre-populated tabs; the torn-off tab replaces them
                while (window._tabs.Count > 0)
                {
                    FluentTab preexisting = window._tabs[0];
                    window.DetachTabCore(preexisting);
                    preexisting.Content?.Dispose();
                }

                return window;
            }
            catch (MissingMethodException)
            {
                return new FluentChromeTabsForm { Text = Text };
            }
        }

        /// <summary>Transfers the dragged tab to another FluentChromeTabsForm when the cursor is over its strip.</summary>
        private bool TryTransferToOtherWindow()
        {
            Point screenCursor = Cursor.Position;

            NativeMethods.POINT nativePoint = new NativeMethods.POINT { x = screenCursor.X, y = screenCursor.Y };
            IntPtr underCursor = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(nativePoint), NativeMethods.GA_ROOT);

            if (underCursor == IntPtr.Zero || underCursor == Handle)
            {
                return false;
            }

            foreach (FluentChromeTabsForm candidate in OpenTabForms)
            {
                if (candidate == this || !candidate.Visible || candidate.WindowState == FormWindowState.Minimized || candidate.Handle != underCursor)
                {
                    continue;
                }

                Point local = candidate.PointToClient(screenCursor);

                if (local.Y < 0 || local.Y >= candidate.StripHeightPx || local.X < 0 || local.X >= candidate.ClientSize.Width)
                {
                    return false;
                }

                FluentTab tab = _tabs[_dragIndex];
                int offset = _dragOffsetX;
                FluentChromeTabsForm origin = _dragOrigin ?? this;

                EndDrag();
                Capture = false;
                _mouseDownTab = -1;

                DetachTabCore(tab);

                // Walked rather than divided by a uniform slot: the target window's active
                // tab is wider than the rest, so everything past it sits further right than
                // a single slot size predicts and the drop would land one position short.
                int index = candidate.DropIndexAtPx(0, local.X);

                tab.Group = 0;
                candidate.AttachTab(index, tab);
                candidate.SelectTabCore(candidate._tabs.IndexOf(tab));
                candidate.Activate();

                // Continue the drag inside the target window
                candidate._dragging = true;
                candidate._dragOrigin = origin;
                origin._tabAwayInDrag = candidate == origin ? null : tab;
                candidate._dragIndex = candidate._tabs.IndexOf(tab);
                candidate._dragOffsetX = Math.Min(offset, candidate.TabWidthPx(candidate._dragIndex, 0) - Dpi(16));
                candidate._dragVisualX = local.X - candidate._dragOffsetX;
                candidate.Capture = true;
                candidate.InvalidateStrip();

                // A host moving tabs decides itself what becomes of a window left empty.
                if (_tabs.Count == 0 && ExitOnLastTabClose && !HostMovesTabsBetweenWindows)
                {
                    BeginInvoke(new Action(Close));
                }

                return true;
            }

            return false;
        }
    }
}
