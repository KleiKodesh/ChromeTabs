using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FluentChromeTabs
{
    /// <summary>Non-client handling: reclaiming the caption area, hit testing, and the drawn caption buttons.</summary>
    public partial class FluentChromeTabsForm
    {
        private int _hotCaptionButton;
        private int _pressedCaptionButton;

        // WinForms' own Form.RestoreWindowBoundsIfNecessary() grows the window by the
        // reclaimed-caption height on every restore-from-minimize: its cached restore
        // rectangle disagrees with this custom caption-reclaiming frame, so each restore
        // adds ~one caption height and the error compounds over repeated cycles.
        //
        // We snapshot the true Normal bounds at minimize time, then after the restore
        // settles (posted via BeginInvoke, since the bogus correction arrives on a later
        // WM_SIZE, not synchronously during SC_RESTORE) reassert them if WinForms drifted.
        private Rectangle? _boundsBeforeMinimize;
        private bool _restoreFixPending;

        private static int ResizeBorderHeight()
        {
            return NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSIZEFRAME) + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXPADDEDBORDER);
        }

        protected override void WndProc(ref Message m)
        {
            // Snapshot the real Normal bounds as the window leaves Normal for Minimized,
            // and arm the post-restore correction when it comes back.
            if (m.Msg == NativeMethods.WM_SIZE)
            {
                int sizeType = m.WParam.ToInt32();
                if (sizeType == NativeMethods.SIZE_MINIMIZED)
                {
                    _boundsBeforeMinimize = RestoreBounds;
                }
                else if (sizeType == NativeMethods.SIZE_MAXIMIZED)
                {
                    // Restored straight to maximized (e.g. taskbar-restoring a window that
                    // was maximized before minimizing) — the Normal-bounds correction below
                    // doesn't apply; drop the snapshot so it can't fire on a later restore.
                    _boundsBeforeMinimize = null;
                }
                else if (sizeType == NativeMethods.SIZE_RESTORED && _boundsBeforeMinimize.HasValue && !_restoreFixPending)
                {
                    _restoreFixPending = true;
                    Rectangle expected = _boundsBeforeMinimize.Value;
                    _boundsBeforeMinimize = null;

                    // Run after all restore-related messages (including the stray WM_SIZE
                    // where WinForms grows RestoreBounds) have been processed.
                    BeginInvoke(new Action(() =>
                    {
                        _restoreFixPending = false;
                        if (WindowState == FormWindowState.Normal && Bounds != expected)
                        {
                            Bounds = expected;
                        }
                    }));
                }
            }

            switch (m.Msg)
            {
                case NativeMethods.WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero)
                    {
                        // Let the default handler compute the standard frame, then push the top edge
                        // back up so the caption becomes client space. Left/right/bottom resize borders
                        // stay untouched. When maximized the frame hangs off the monitor edge, so the
                        // resize border height goes back on top.
                        NativeMethods.NCCALCSIZE_PARAMS parameters =
                            (NativeMethods.NCCALCSIZE_PARAMS) Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.NCCALCSIZE_PARAMS));
                        int originalTop = parameters.rgrc0.top;

                        base.WndProc(ref m);

                        // The maximized top inset compensates for the resize frame hanging off the
                        // monitor edge — a borderless window (fullscreen mode) has no frame and sits
                        // flush with the screen, so adding it there leaves a dead black band on top.
                        bool maximizedWithFrame = NativeMethods.IsZoomed(Handle) && FormBorderStyle != FormBorderStyle.None;

                        parameters = (NativeMethods.NCCALCSIZE_PARAMS) Marshal.PtrToStructure(m.LParam, typeof(NativeMethods.NCCALCSIZE_PARAMS));
                        parameters.rgrc0.top = originalTop + (maximizedWithFrame ? ResizeBorderHeight() : 0);
                        Marshal.StructureToPtr(parameters, m.LParam, false);

                        m.Result = IntPtr.Zero;
                        return;
                    }

                    break;

                case NativeMethods.WM_NCHITTEST:
                {
                    base.WndProc(ref m);

                    if ((int) m.Result == NativeMethods.HTCLIENT)
                    {
                        Point p = PointToClient(ScreenPointFromLParam(m.LParam));

                        if (!NativeMethods.IsZoomed(Handle) && p.Y < ResizeBorderHeight())
                        {
                            m.Result = new IntPtr(NativeMethods.HTTOP);
                        }
                        else if (StripVisible && p.Y < StripHeightPx)
                        {
                            if (CaptionButtonRect(NativeMethods.HTCLOSE).Contains(p))
                            {
                                m.Result = new IntPtr(NativeMethods.HTCLOSE);
                            }
                            else if (CaptionButtonRect(NativeMethods.HTMAXBUTTON).Contains(p))
                            {
                                // Returning HTMAXBUTTON is what makes the Windows 11 snap-layouts flyout appear
                                m.Result = new IntPtr(NativeMethods.HTMAXBUTTON);
                            }
                            else if (CaptionButtonRect(NativeMethods.HTMINBUTTON).Contains(p))
                            {
                                m.Result = new IntPtr(NativeMethods.HTMINBUTTON);
                            }
                            else if (HitTab(p) < 0 && !HitsStripButton(p) && !(SplitStrip && DividerHitRect.Contains(p)))
                            {
                                // Empty strip drags the window and double-click maximizes, both natively
                                m.Result = new IntPtr(NativeMethods.HTCAPTION);
                            }
                        }
                    }

                    return;
                }

                case NativeMethods.WM_NCMOUSEMOVE:
                {
                    int hit = (int) m.WParam;
                    bool overButton = hit == NativeMethods.HTMINBUTTON || hit == NativeMethods.HTMAXBUTTON || hit == NativeMethods.HTCLOSE;

                    SetHotCaptionButton(overButton ? hit : 0);

                    if (overButton)
                    {
                        // Our own (localizable) tooltip in place of Windows' OS-language one.
                        // Key by hit-code so moving within one button doesn't re-show.
                        ShowStripToolTip(hit, CaptionButtonToolTip(hit), CaptionButtonRect(hit));

                        // Ask for WM_NCMOUSELEAVE so hover state clears when the cursor leaves the window
                        NativeMethods.TRACKMOUSEEVENT tme = new NativeMethods.TRACKMOUSEEVENT
                        {
                            cbSize = (uint) Marshal.SizeOf(typeof(NativeMethods.TRACKMOUSEEVENT)),
                            dwFlags = NativeMethods.TME_LEAVE | NativeMethods.TME_NONCLIENT,
                            hwndTrack = Handle
                        };
                        NativeMethods.TrackMouseEvent(ref tme);
                    }
                    else
                    {
                        ShowStripToolTip(null, null, Rectangle.Empty);
                    }

                    break;
                }

                case NativeMethods.WM_NCMOUSELEAVE:
                    SetHotCaptionButton(0);
                    ShowStripToolTip(null, null, Rectangle.Empty);

                    if (_pressedCaptionButton != 0)
                    {
                        _pressedCaptionButton = 0;
                        InvalidateStrip();
                    }

                    break;

                case NativeMethods.WM_NCLBUTTONDOWN:
                {
                    int hit = (int) m.WParam;

                    if (hit == NativeMethods.HTMINBUTTON || hit == NativeMethods.HTMAXBUTTON || hit == NativeMethods.HTCLOSE)
                    {
                        // Handled here so DefWindowProc doesn't paint classic-theme buttons over the strip
                        _pressedCaptionButton = hit;
                        InvalidateStrip();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    break;
                }

                case NativeMethods.WM_NCLBUTTONUP:
                {
                    int hit = (int) m.WParam;

                    if (_pressedCaptionButton != 0)
                    {
                        bool clicked = hit == _pressedCaptionButton;
                        _pressedCaptionButton = 0;
                        InvalidateStrip();

                        if (clicked)
                        {
                            switch (hit)
                            {
                                case NativeMethods.HTCLOSE:
                                    Close();
                                    break;

                                case NativeMethods.HTMINBUTTON:
                                    WindowState = FormWindowState.Minimized;
                                    break;

                                case NativeMethods.HTMAXBUTTON:
                                    WindowState = NativeMethods.IsZoomed(Handle) ? FormWindowState.Normal : FormWindowState.Maximized;
                                    break;
                            }
                        }

                        m.Result = IntPtr.Zero;
                        return;
                    }

                    break;
                }

                case NativeMethods.WM_NCRBUTTONUP:
                    if ((int) m.WParam == NativeMethods.HTCAPTION)
                    {
                        ShowSystemMenu(ScreenPointFromLParam(m.LParam));
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    break;
            }

            base.WndProc(ref m);
        }

        private static Point ScreenPointFromLParam(IntPtr lParam)
        {
            long value = lParam.ToInt64();
            return new Point((short) (value & 0xFFFF), (short) ((value >> 16) & 0xFFFF));
        }

        private void ShowSystemMenu(Point screen)
        {
            IntPtr menu = NativeMethods.GetSystemMenu(Handle, false);
            int command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD, screen.X, screen.Y, Handle, IntPtr.Zero);

            if (command != 0)
            {
                NativeMethods.SendMessage(Handle, NativeMethods.WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);
            }
        }

        private void SetHotCaptionButton(int htCode)
        {
            if (_hotCaptionButton != htCode)
            {
                _hotCaptionButton = htCode;
                InvalidateStrip();
            }
        }
    }
}
