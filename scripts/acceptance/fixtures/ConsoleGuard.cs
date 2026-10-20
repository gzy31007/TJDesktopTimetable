using System;
using System.IO;
using System.Runtime.InteropServices;

// Console control events are delivered on a system thread, so the handler has to
// be real CLR code. A PowerShell scriptblock delegate is not reliable there.
public static class ConsoleGuard
{
    public delegate bool HandlerRoutine(uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    static HandlerRoutine _handler;
    static string _path;

    public static int Count;

    public static bool Register(string path)
    {
        _path = path;
        _handler = OnControl;
        return SetConsoleCtrlHandler(_handler, true);
    }

    public static bool Unregister()
    {
        return _handler != null && SetConsoleCtrlHandler(_handler, false);
    }

    static bool OnControl(uint type)
    {
        Count++;
        try { File.AppendAllText(_path, "control=" + type + "\r\n"); } catch { }
        return true;
    }
}
