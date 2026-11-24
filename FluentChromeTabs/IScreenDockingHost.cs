using System;
using System.Drawing;
using System.Threading.Tasks;

namespace FluentChromeTabs
{
    /// <summary>
    /// What an app does for screen docking (<see cref="ScreenDocking"/>): the library decides
    /// where a dragged tab or window goes and places the windows; the host owns what a tab is.
    /// </summary>
    public interface IScreenDockingHost
    {
        /// <summary>
        /// Moves a tab of <paramref name="source"/> into <paramref name="target"/> at a strip position
        /// (-1: the end). Completes with false when the tab could not move (it stays where it was).
        /// A window left without tabs is the host's to close.
        /// </summary>
        Task<bool> MoveTabAsync(FluentChromeTabsForm source, FluentTab tab, FluentChromeTabsForm target, int position);

        /// <summary>
        /// Opens a new window holding the tab, and calls <paramref name="place"/> with it once it is
        /// shown (the library docks or positions it). The new window must be attached too.
        /// </summary>
        void OpenWindowForTab(FluentChromeTabsForm source, FluentTab tab, Action<FluentChromeTabsForm> place);

        /// <summary>True while the window may take tabs and a place (false while it closes, say).</summary>
        bool AcceptsDrops(FluentChromeTabsForm window);

        /// <summary>How many tabs the window really has (a host's strip may still be catching up).</summary>
        int TabCount(FluentChromeTabsForm window);

        /// <summary>A picture of the window's current page for the floating tab, or a null task result for none.</summary>
        Task<Image> CaptureTabPicture(FluentChromeTabsForm window);
    }
}
