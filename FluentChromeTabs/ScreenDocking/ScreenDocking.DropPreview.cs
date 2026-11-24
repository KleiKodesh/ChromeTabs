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
        // ── The preview ───────────────────────────────────────────────────────────

        private static void ShowDropPreview(Rectangle bounds)
        {
            if (_dropPreview == null || _dropPreview.IsDisposed) _dropPreview = new DropZonePreview();
            if (_dropPreview.Bounds != bounds) _dropPreview.Bounds = bounds;
            if (!_dropPreview.Visible) _dropPreview.Show();
        }

        private static void HideDropPreview()
        {
            if (_dropPreview != null && !_dropPreview.IsDisposed && _dropPreview.Visible) _dropPreview.Hide();
        }

        /// <summary>The see-through sheet that marks where a dropped window will land. Never takes focus or clicks.</summary>
        private sealed class DropZonePreview : Form
        {
            public DropZonePreview()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = AccentColor;
                Opacity = 0.28;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }
        }
    }
}
