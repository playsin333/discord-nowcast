using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Nowcast
{
    class BrowserWindowInfo
    {
        public string Browser;
        public string Title;
        public string Site;
    }

    // Reads visible browser windows and their active tab addresses locally. The
    // cached UI Automation lookup avoids walking each browser's UI tree every
    // time the media engine polls Windows.
    static class WindowTitles
    {
        delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        class CachedSite
        {
            public string Site;
            public DateTime Expires;
        }

        static readonly object cacheLock = new object();
        static readonly Dictionary<IntPtr, CachedSite> siteCache = new Dictionary<IntPtr, CachedSite>();

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public static List<BrowserWindowInfo> ReadBrowserWindows()
        {
            var windows = new List<BrowserWindowInfo>();
            EnumWindows((window, parameter) =>
            {
                if (!IsWindowVisible(window)) return true;
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                string processName;
                try
                {
                    using (var process = Process.GetProcessById((int)processId)) processName = process.ProcessName;
                }
                catch (Exception) { return true; }
                var browser = Sources.BrowserFromProcess(processName);
                if (browser == null) return true;
                var title = ReadTitle(window);
                if (title == "") return true;
                windows.Add(new BrowserWindowInfo { Browser = browser, Title = title, Site = ReadSite(window) });
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        static string ReadTitle(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            if (length == 0) return "";
            var text = new StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        static string ReadSite(IntPtr window)
        {
            lock (cacheLock)
            {
                CachedSite cached;
                if (siteCache.TryGetValue(window, out cached) && DateTime.UtcNow < cached.Expires)
                    return cached.Site;
            }

            string site = null;
            try
            {
                var root = AutomationElement.FromHandle(window);
                var edit = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var combo = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
                var elements = root.FindAll(TreeScope.Descendants, new OrCondition(edit, combo));
                var fallback = (string)null;
                for (int i = 0; i < elements.Count; i++)
                {
                    var element = elements[i];
                    string value = ReadValue(element);
                    var candidate = Sources.SiteFromAddress(value);
                    if (candidate == null) continue;
                    string automationId = "", name = "";
                    try { automationId = element.Current.AutomationId ?? ""; }
                    catch (Exception) { }
                    try { name = element.Current.Name ?? ""; }
                    catch (Exception) { }
                    var addressBar = automationId.IndexOf("urlbar", StringComparison.OrdinalIgnoreCase) >= 0
                        || automationId.IndexOf("omnibox", StringComparison.OrdinalIgnoreCase) >= 0
                        || automationId.IndexOf("address", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("address and search", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("адрес", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (addressBar) { site = candidate; break; }
                    if (fallback == null) fallback = candidate;
                }
                if (site == null) site = fallback;
            }
            catch (Exception)
            {
                // Browser accessibility trees vary by version and can disappear
                // while a window is closing; a missing site is simply ignored.
            }

            lock (cacheLock)
            {
                if (siteCache.Count > 128) siteCache.Clear();
                siteCache[window] = new CachedSite
                {
                    Site = site,
                    Expires = DateTime.UtcNow.AddSeconds(site == null ? 1 : 2),
                };
            }
            return site;
        }

        static string ReadValue(AutomationElement element)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                    return ((ValuePattern)pattern).Current.Value;
            }
            catch (Exception) { }
            try { return element.Current.Name; }
            catch (Exception) { return null; }
        }
    }
}
