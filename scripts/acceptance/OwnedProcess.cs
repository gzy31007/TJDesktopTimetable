using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Owns exactly one child process.
//
// The child is created inside a job object atomically: the attribute list carries
// both PROC_THREAD_ATTRIBUTE_HANDLE_LIST (only the three std handles are
// inherited) and PROC_THREAD_ATTRIBUTE_JOB_LIST (the child is in the job before
// any of its code runs). That removes the user-mode CreateProcess -> assign ->
// resume window in which a hard-killed runner could leave a suspended orphan.
// If either attribute cannot be set, Start fails closed; there is no racy
// fallback. JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE makes the kernel kill the tree
// when the runner dies, including when it is killed with TerminateProcess.
public sealed class OwnedProcess : IDisposable
{
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_APPEND_DATA = 0x00000004;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint FILE_SHARE_DELETE = 0x00000004;
    const uint CREATE_ALWAYS = 2;
    const uint OPEN_ALWAYS = 4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint STARTF_USESTDHANDLES = 0x00000100;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint INFINITE = 0xFFFFFFFF;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x00020002;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = (IntPtr)0x0002000D;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    delegate bool ControlHandler(uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(ControlHandler handler, bool add);

    IntPtr _process;
    IntPtr _job;
    int _processId;
    string _logPath;

    // Console control events are delivered on a system thread, so the handler has
    // to be CLR code (a PowerShell scriptblock delegate is not reliable there).
    // It only swallows the event while a job is active: then it terminates that
    // job and records the cancellation so the runner can report failure. Outside
    // an owned run, the host's own Ctrl+C handling is left alone.
    static ControlHandler _controlHandler;
    static bool _handlerInstalled;
    static readonly object _handlerLock = new object();
    static volatile bool _ctrlC;
    static IntPtr _activeJob;

    static void EnsureHandler()
    {
        lock (_handlerLock)
        {
            if (_handlerInstalled) return;
            _controlHandler = OnControl;
            SetConsoleCtrlHandler(_controlHandler, true);
            _handlerInstalled = true;
        }
    }

    static bool OnControl(uint type)
    {
        IntPtr job = _activeJob;
        if (job == IntPtr.Zero) return false;
        _ctrlC = true;
        TerminateJobObject(job, 1);
        return true;
    }

    public bool CtrlC { get { return _ctrlC; } }
    public int ProcessId { get { return _processId; } }
    public string LogPath { get { return _logPath; } }

    public static OwnedProcess Start(string commandLine, string workingDirectory, string logPath, bool hidden, out string error)
    {
        error = null;
        EnsureHandler();
        var process = new OwnedProcess();
        var sa = new SECURITY_ATTRIBUTES();
        sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        sa.bInheritHandle = 1;

        SafeFileHandle logOut = null;
        SafeFileHandle logErr = null;
        SafeFileHandle nul = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleArray = IntPtr.Zero;
        IntPtr jobArray = IntPtr.Zero;
        IntPtr attributeSize = IntPtr.Zero;
        IntPtr job = IntPtr.Zero;
        PROCESS_INFORMATION info = new PROCESS_INFORMATION();
        bool created = false;
        try
        {
            // Two distinct handles to the same file: the inheritance list may not
            // contain one handle twice, and FILE_APPEND_DATA keeps both writers at
            // end-of-file so stdout/stderr do not overwrite each other.
            uint share = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
            logOut = CreateFileW(logPath, FILE_APPEND_DATA, share, ref sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (logOut.IsInvalid)
            {
                error = "CreateFileW failed for '" + logPath + "': " + Marshal.GetLastWin32Error();
                return null;
            }
            logErr = CreateFileW(logPath, FILE_APPEND_DATA, share, ref sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (logErr.IsInvalid)
            {
                error = "CreateFileW failed for '" + logPath + "': " + Marshal.GetLastWin32Error();
                return null;
            }
            nul = CreateFileW("NUL", GENERIC_READ, share, ref sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (nul.IsInvalid)
            {
                error = "CreateFileW failed for NUL: " + Marshal.GetLastWin32Error();
                return null;
            }

            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                error = "CreateJobObject failed: " + Marshal.GetLastWin32Error();
                return null;
            }
            var jobInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            jobInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref jobInfo, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
            {
                error = "SetInformationJobObject failed: " + Marshal.GetLastWin32Error();
                return null;
            }

            attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeSize);
            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeSize))
            {
                error = "InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error();
                return null;
            }
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleArray, 0, nul.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, logOut.DangerousGetHandle());
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, logErr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
            {
                error = "UpdateProcThreadAttribute(HANDLE_LIST) failed: " + Marshal.GetLastWin32Error();
                return null;
            }
            jobArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobArray, 0, job);
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobArray, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                error = "UpdateProcThreadAttribute(JOB_LIST) failed (Windows 10 or newer is required): " + Marshal.GetLastWin32Error();
                return null;
            }

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            startup.StartupInfo.dwFlags = (int)STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = nul.DangerousGetHandle();
            startup.StartupInfo.hStdOutput = logOut.DangerousGetHandle();
            startup.StartupInfo.hStdError = logErr.DangerousGetHandle();
            startup.lpAttributeList = attributeList;

            uint flags = EXTENDED_STARTUPINFO_PRESENT;
            if (hidden) flags |= CREATE_NO_WINDOW;

            string cwd = workingDirectory;
            if (string.IsNullOrEmpty(cwd)) cwd = Environment.CurrentDirectory;

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, cwd, ref startup, out info))
            {
                int code = Marshal.GetLastWin32Error();
                error = "CreateProcessW failed for '" + commandLine + "': Win32 error " + code;
                return null;
            }
            created = true;

            process._process = info.hProcess;
            process._processId = info.dwProcessId;
            process._logPath = logPath;
            process._job = job;
            _activeJob = job;
            info.hProcess = IntPtr.Zero;
            job = IntPtr.Zero;
            if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
            info.hThread = IntPtr.Zero;
            return process;
        }
        finally
        {
            if (nul != null && !nul.IsInvalid) nul.Dispose();
            if (logOut != null && !logOut.IsInvalid) logOut.Dispose();
            if (logErr != null && !logErr.IsInvalid) logErr.Dispose();
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (jobArray != IntPtr.Zero) Marshal.FreeHGlobal(jobArray);
            if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread);
            if (!created)
            {
                // Any process we managed to create but did not hand over must not
                // survive: it would be outside a job and impossible to find again.
                if (info.hProcess != IntPtr.Zero)
                {
                    TerminateProcess(info.hProcess, 1);
                    CloseHandle(info.hProcess);
                }
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }
    }

    public bool WaitForExit(uint milliseconds, out uint exitCode)
    {
        exitCode = 0;
        if (_process == IntPtr.Zero) return true;
        uint wait = WaitForSingleObject(_process, milliseconds);
        if (wait != 0) return false;
        GetExitCodeProcess(_process, out exitCode);
        return true;
    }

    public bool WaitForExit()
    {
        uint code;
        return WaitForExit(INFINITE, out code);
    }

    public uint ExitCode()
    {
        uint code = 0;
        if (_process != IntPtr.Zero) GetExitCodeProcess(_process, out code);
        return code;
    }

    public void Terminate()
    {
        if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
    }

    public void Dispose()
    {
        if (_process != IntPtr.Zero)
        {
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }
        if (_job != IntPtr.Zero)
        {
            if (_activeJob == _job) _activeJob = IntPtr.Zero;
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }
}
