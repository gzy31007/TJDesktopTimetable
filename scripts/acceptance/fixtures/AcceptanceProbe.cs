using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Test fixture: a real child process the acceptance runner launches. No mocks.
// Modes:
//   sleep        --sleep-ms N            do nothing, then exit
//   exit         --exit-code N           exit with N
//   driver       --pids <json> [--signal-after-ms N] [--sleep-ms N] [--exit-code N]
//                spawns a grandchild (this exe, sleep mode), writes both pids,
//                optionally broadcasts CTRL_C_EVENT to its own console, then sleeps
public static class AcceptanceProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);

    static string Arg(string[] a, string name, string fallback)
    {
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return fallback;
    }

    static string PidsJson(int pid, int? grandchild)
    {
        var sb = new StringBuilder();
        sb.Append("{\"pid\":").Append(pid);
        if (grandchild.HasValue) sb.Append(",\"grandchild\":").Append(grandchild.Value);
        sb.Append('}');
        return sb.ToString();
    }

    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "sleep";
        if (mode == "sleep")
        {
            Thread.Sleep(int.Parse(Arg(args, "--sleep-ms", "120000")));
            return 0;
        }
        if (mode == "exit")
        {
            return int.Parse(Arg(args, "--exit-code", "0"));
        }
        if (mode == "driver")
        {
            string pids = Arg(args, "--pids", "");
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            var child = Process.Start(new ProcessStartInfo(exe, "sleep --sleep-ms 120000")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            string snapshot = PidsJson(Process.GetCurrentProcess().Id, child.Id);
            if (pids.Length > 0) File.WriteAllText(pids, snapshot);
            Console.WriteLine(snapshot);
            Console.Out.Flush();
            Console.Error.WriteLine("driver stderr marker " + snapshot);
            Console.Error.Flush();

            string signalAfter = Arg(args, "--signal-after-ms", "");
            if (signalAfter.Length > 0)
            {
                Thread.Sleep(int.Parse(signalAfter));
                bool sent = GenerateConsoleCtrlEvent(0, 0);
                Console.WriteLine("ctrl_event_sent=" + sent + " error=" + Marshal.GetLastWin32Error());
                Console.Out.Flush();
            }
            Thread.Sleep(int.Parse(Arg(args, "--sleep-ms", "120000")));
            return int.Parse(Arg(args, "--exit-code", "0"));
        }
        if (mode == "inject")
        {
            // Deliberately has no console of its own: detach, attach to the
            // target runner's console, then deliver a real CTRL_C_EVENT.
            string pid = Arg(args, "--pid", "");
            int delay = int.Parse(Arg(args, "--delay-ms", "200"));
            FreeConsole();
            bool attached = AttachConsole(uint.Parse(pid));
            int attachError = Marshal.GetLastWin32Error();
            Thread.Sleep(delay);
            bool sent = GenerateConsoleCtrlEvent(0, 0);
            Console.Error.WriteLine("attached=" + attached + " error=" + attachError + " sent=" + sent);
            Thread.Sleep(1500);
            return 0;
        }
        Console.Error.WriteLine("unknown mode: " + mode);
        return 64;
    }
}
