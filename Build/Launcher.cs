using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LootLedger
{
    internal static class Launcher
    {
        private static readonly string[] Payload = {
            "LootLedger.ps1", "Tracking.cs", "TrayUi.cs", "Overlay.xaml", "LICENSE"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            bool render = args.Length > 0 && args[0] == "--render-preview";
            try
            {
                bool preview = args.Length == 1 && args[0] == "--preview";
                if (args.Length != 0 && !preview && !(render && args.Length == 2))
                    throw new ArgumentException("Usage: LootLedger.exe [--preview | --render-preview output.png]");

                string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LootLedger");
                string packageId;
                using (var file = File.OpenRead(Assembly.GetExecutingAssembly().Location))
                using (var sha = SHA256.Create())
                    packageId = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                string app = Path.Combine(home, "app", packageId);

                using (var mutex = new Mutex(false, @"Local\LootLedger-Package-" + packageId))
                {
                    bool ownsMutex;
                    try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex)
                        throw new TimeoutException("Another copy of Loot Ledger is still preparing its files. Try again shortly.");
                    try
                    {
                        Directory.CreateDirectory(app);
                        foreach (string name in Payload)
                        {
                            byte[] bytes;
                            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LootLedger.Payload." + name))
                            using (var memory = new MemoryStream())
                            {
                                if (resource == null)
                                    throw new InvalidDataException("The application package is missing " + name + ".");
                                resource.CopyTo(memory);
                                bytes = memory.ToArray();
                            }
                            string path = Path.Combine(app, name);
                            if (!File.Exists(path) || !EqualBytes(bytes, File.ReadAllBytes(path)))
                                File.WriteAllBytes(path, bytes);
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }

                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(powershell))
                    throw new FileNotFoundException("Loot Ledger requires Windows PowerShell 5.1.");

                var command = new StringBuilder("-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File ");
                command.Append(Quote(Path.Combine(app, "LootLedger.ps1")));
                command.Append(" -DataDirectory ").Append(Quote(Path.Combine(home, ".loot-ledger")));
                if (preview)
                    command.Append(" -Preview");
                if (render)
                    command.Append(" -RenderPreview ").Append(Quote(Path.GetFullPath(args[1])));

                using (var child = Process.Start(new ProcessStartInfo {
                    FileName = powershell,
                    Arguments = command.ToString(),
                    WorkingDirectory = app,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (child == null)
                        throw new InvalidOperationException("Windows PowerShell could not be started.");
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (Exception error)
            {
                if (render)
                    Console.Error.WriteLine(error.Message);
                else
                    MessageBox.Show(error.Message, "Loot Ledger", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static bool EqualBytes(byte[] first, byte[] second)
        {
            if (first.Length != second.Length)
                return false;
            for (int index = 0; index < first.Length; index++)
                if (first[index] != second[index])
                    return false;
            return true;
        }

        // ProcessStartInfo on .NET Framework takes a Windows command-line string.
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                result.Append(character);
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
