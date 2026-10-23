// discord-nowcast: shows the track playing in any Windows app as your
// Discord status, with a tray icon to control it.
//
// Built by build.ps1 with the C# compiler that ships with .NET Framework
// 4.8, so the code stays within C# 5: no string interpolation, no ?.,
// no expression-bodied members.

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Nowcast
{
    static class Program
    {
        // Raise with every release, together with a CHANGELOG.md entry.
        public const string Version = "1.4.11";
        public const string Repo = "playsin333/discord-nowcast";
        // The project's Discord server: questions, bugs, screenshots of the status.
        public const string DiscordInvite = "https://discord.gg/pHzmvghmJR";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            bool isUpdate = args.Contains("--updated");
            Updater.CleanUp();

            // One copy at a time. After an update the old copy is still closing,
            // so wait for it; a copy that was killed leaves the mutex abandoned,
            // which is ours to take.
            var mutex = new Mutex(false, "Local\\discord-nowcast");
            bool isFirst;
            try
            {
                isFirst = mutex.WaitOne(isUpdate ? 15000 : 1000);
            }
            catch (AbandonedMutexException)
            {
                isFirst = true;
            }
            if (!isFirst)
            {
                MessageBox.Show(Text.AlreadyRunning, "discord-nowcast", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log.Start();
            Log.Write("discord-nowcast " + Version + " " + Application.ExecutablePath);
            try
            {
                Application.Run(new TrayApp(isUpdate));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
