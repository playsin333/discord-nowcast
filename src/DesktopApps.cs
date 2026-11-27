using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nowcast
{
    sealed class DesktopApp
    {
        public int ProcessId;
        public long ProcessStartTicks;
        public string ProcessName;
        public string ExecutablePath;
        public string Name;
        public string Title;
        public string Image;
        // The same process can change its window title while it stays open.
        // Keep a stable session identity for the clock, and include the title
        // only in the activity key so Discord still gets document changes.
        public string SessionKey { get { return "desktop|" + ProcessId + "|" + ProcessStartTicks; } }
        public string Key { get { return SessionKey + "|" + Title; } }
    }

    static class DesktopApps
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        public static List<DesktopApp> Available()
        {
            var found = new Dictionary<string, DesktopApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var path = "";
                    try { path = process.MainModule.FileName; } catch { }
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var processName = process.ProcessName;
                    DesktopApp row;
                    if (!found.TryGetValue(processName, out row) || (row.ExecutablePath == "" && path != ""))
                        found[processName] = new DesktopApp { ProcessId = process.Id, ProcessStartTicks = StartTicks(process), ProcessName = processName, ExecutablePath = path, Name = ProductName(path, processName), Title = process.MainWindowTitle };
                }
                catch { }
                finally { process.Dispose(); }
            }
            var list = new List<DesktopApp>(found.Values);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public static DesktopApp Running(Dictionary<string, string> selected)
        {
            if (selected == null || selected.Count == 0) return null;
            // Prefer the selected app in front, then any selected app still open.
            uint id;
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out id);
                var foreground = Read((int)id, selected);
                if (foreground != null) return foreground;
            }
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var app = Read(process.Id, selected);
                    if (app != null) return app;
                }
                catch { }
                finally { process.Dispose(); }
            }
            return null;
        }

        static DesktopApp Read(int id, Dictionary<string, string> selected)
        {
            try
            {
                using (var process = Process.GetProcessById(id))
                {
                    var path = "";
                    try { path = process.MainModule.FileName; } catch { }
                    string name = null;
                    foreach (var pair in selected)
                    {
                        var key = pair.Key;
                        if (key.StartsWith("process|", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.Equals(key.Substring(8), process.ProcessName, StringComparison.OrdinalIgnoreCase)) { name = pair.Value; break; }
                            continue;
                        }
                        var executable = key.StartsWith("exe|", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : key;
                        if (!string.IsNullOrEmpty(path) && string.Equals(Path.GetFullPath(executable), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) { name = pair.Value; break; }
                    }
                    if (name == null) return null;
                    var title = process.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) title = name;
                    return new DesktopApp { ProcessId = process.Id, ProcessStartTicks = StartTicks(process), ProcessName = process.ProcessName, ExecutablePath = path, Name = string.IsNullOrWhiteSpace(name) ? ProductName(path, process.ProcessName) : name, Title = title, Image = path == "" ? null : AppIcon.Get(path) };
                }
            }
            catch { return null; }
        }

        static long StartTicks(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }

        static string ProductName(string path, string processName)
        {
            try
            {
                var product = FileVersionInfo.GetVersionInfo(path).ProductName;
                if (!string.IsNullOrWhiteSpace(product)) return product.Trim();
            }
            catch { }
            if (processName.Equals("FL64", StringComparison.OrdinalIgnoreCase) || processName.Equals("FL", StringComparison.OrdinalIgnoreCase)) return "FL Studio";
            var name = string.IsNullOrEmpty(path) ? processName : Path.GetFileNameWithoutExtension(path);
            return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        }

        public static string NameFromPath(string path)
        {
            return ProductName(path, Path.GetFileNameWithoutExtension(path));
        }
    }

    // Discord RPC requires a URL or a Discord asset key; it cannot read a local
    // .exe or icon path. Upload only the extracted icon, never the executable.
    static class AppIcon
    {
        sealed class Cached
        {
            public string Url;
            public DateTime Renew;
            public bool Busy;
        }
        static readonly object gate = new object();
        static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        public static event Action Ready;

        public static Image Local(string path)
        {
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path)) return icon == null ? null : icon.ToBitmap();
            }
            catch { return null; }
        }

        public static string Get(string path)
        {
            Cached item;
            lock (gate)
            {
                if (!cache.TryGetValue(path, out item)) cache[path] = item = new Cached();
                if (!item.Busy && (item.Url == null || DateTime.Now >= item.Renew))
                {
                    item.Busy = true;
                    Task.Run(delegate { Upload(path, item); });
                }
                return item.Url;
            }
        }

        static void Upload(string path, Cached item)
        {
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path))
                using (var bitmap = icon == null ? null : icon.ToBitmap())
                using (var stream = new MemoryStream())
                {
                    if (bitmap == null) throw new InvalidDataException("The executable has no readable icon");
                    bitmap.Save(stream, ImageFormat.Png);
                    var uploaded = Hosts.Upload(CoverImage.Prepare(stream.ToArray()));
                    lock (gate) { item.Url = uploaded.Url; item.Renew = DateTime.Now.AddTicks(uploaded.Keep.Ticks * 4 / 5); item.Busy = false; }
                    Log.Write("Executable icon uploaded: " + Path.GetFileName(path));
                }
            }
            catch (Exception e)
            {
                lock (gate) { item.Renew = DateTime.Now.AddMinutes(15); item.Busy = false; }
                Log.Write("Executable icon unavailable: " + Path.GetFileName(path) + ": " + e.GetBaseException().Message);
            }
            var handler = Ready;
            if (handler != null) handler();
        }
    }
}
