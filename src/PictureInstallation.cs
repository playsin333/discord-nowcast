using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Nowcast
{
    static class PictureInstallation
    {
        static readonly object gate = new object();
        internal static string IdentityPath = Path.Combine(Log.Folder, "status-installation");
        // Keep this outside config.json and diagnostics. It is a random installation
        // token for replacing pictures, not the Discord account or text status.
        internal static string Installation()
        {
            lock (gate)
            {
                if (File.Exists(IdentityPath))
                {
                    var saved = File.ReadAllText(IdentityPath).Trim();
                    if (!Regex.IsMatch(saved, "^[a-f0-9]{64}$")) throw new IOException("Invalid installation token");
                    return saved;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(IdentityPath));
                var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(IdentityPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(token);
                return token;
            }
        }
    }
}
