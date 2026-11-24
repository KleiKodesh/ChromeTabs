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
        /// <summary>
        /// What follows the cursor while a tab is dragged: the tab as a rounded card, with a
        /// preview of its page under it - Windows 11's rounded corners and shadow, and the
        /// app's light or dark colors.
        /// </summary>
        private sealed class TabDragGhost : Form
        {
            private readonly string _title;
            private readonly Image _icon;
            private readonly float _scale;
            private readonly bool _dark;
            private readonly Color _accent;
            private Image _picture;

            public TabDragGhost(string title, Image icon, int dpi)
            {
                _title = title ?? "";
                _icon = icon;
                _scale = dpi / 96f;
                _dark = DarkMode;
                _accent = AccentColor;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                RightToLeft = RightToLeft.Yes;
                BackColor = _dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(249, 249, 249);
                Font = new Font("Segoe UI", 9f);
                Size = new Size(Px(260), Px(44));
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            private int Px(int logical) => (int)(logical * _scale);

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int CS_DROPSHADOW = 0x20000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    cp.ClassStyle |= CS_DROPSHADOW;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Windows 11: rounded corners with the system's own shadow. Older Windows
                // ignores the attribute and keeps square corners with the class shadow.
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }

            /// <summary>The cursor holds the tab near its start (the right side: RTL).</summary>
            public void MoveTo(Point cursor)
            {
                Location = new Point(cursor.X - Width + Px(28), cursor.Y - Px(20));
            }

            /// <summary>The page's picture, shown small under the tab.</summary>
            public void SetPicture(Image picture)
            {
                _picture = picture;
                int width = Px(260);
                int previewWidth = width - Px(12);
                int previewHeight = Math.Min(Px(160), (int)((long)picture.Height * previewWidth / Math.Max(1, picture.Width)));
                Point anchor = new Point(Right - Px(28), Top + Px(20));
                Size = new Size(width, Px(44) + previewHeight + Px(6));
                MoveTo(anchor);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(BackColor);

                Color card = _dark ? Color.FromArgb(45, 45, 45) : Color.White;
                Color text = _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
                Color hairline = _dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);

                // The tab: a rounded card, an accent mark at its start, icon and title.
                var tab = new Rectangle(Px(6), Px(6), Width - Px(12), Px(32));
                using (GraphicsPath path = Rounded(tab, Px(6)))
                {
                    using (var brush = new SolidBrush(card)) g.FillPath(brush, path);
                    using (var pen = new Pen(hairline)) g.DrawPath(pen, path);
                }
                using (var mark = new SolidBrush(_accent))
                    g.FillRectangle(mark, tab.Right - Px(3), tab.Top + Px(9), Px(2), tab.Height - Px(18));

                int x = tab.Right - Px(12);
                if (_icon != null)
                {
                    int icon = Px(16);
                    x -= icon;
                    g.DrawImage(_icon, new Rectangle(x, tab.Top + (tab.Height - icon) / 2, icon, icon));
                    x -= Px(8);
                }
                var textRect = new Rectangle(tab.Left + Px(10), tab.Top, Math.Max(0, x - tab.Left - Px(10)), tab.Height);
                TextRenderer.DrawText(g, _title, Font, textRect, text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // The page, small, in rounded corners under the tab.
                if (_picture != null)
                {
                    var preview = new Rectangle(Px(6), tab.Bottom + Px(4), Width - Px(12), Height - tab.Bottom - Px(10));
                    if (preview.Height > 0)
                    {
                        using (GraphicsPath clip = Rounded(preview, Px(6)))
                        {
                            GraphicsState state = g.Save();
                            g.SetClip(clip);
                            int sourceHeight = Math.Min(_picture.Height, (int)((long)_picture.Width * preview.Height / Math.Max(1, preview.Width)));
                            g.DrawImage(_picture, preview, 0, 0, _picture.Width, sourceHeight, GraphicsUnit.Pixel);
                            g.Restore(state);
                            using (var pen = new Pen(hairline)) g.DrawPath(pen, clip);
                        }
                    }
                }
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                float d = radius * 2f;
                var path = new GraphicsPath();
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
