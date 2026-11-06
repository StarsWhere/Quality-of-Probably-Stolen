using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Render on a private desktop. This desktop is never switched to or used for input.
public sealed class BackgroundPlayer : IDisposable
{
    private IntPtr desktop;
    private IntPtr processHandle;
    public Process Process { get; private set; }
    public int ExitCode { get; private set; } = int.MinValue;

    public static BackgroundPlayer Start(string executable, string directory, string arguments)
    {
        var result = new BackgroundPlayer();
        string name = "ShopAssistQA_" + Guid.NewGuid().ToString("N");
        // Read/write objects, create windows/menus and enumerate. No SwitchDesktop right.
        result.desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x00c7, IntPtr.Zero);
        if (result.desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the private QA desktop");
        try
        {
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = "winsta0\\" + name,
                Flags = 1, ShowWindow = 4 // SW_SHOWNOACTIVATE, on the private desktop only.
            };
            ProcessInfo info;
            if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments),
                IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, directory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot launch the Player on the private QA desktop");
            result.processHandle = info.Process;
            try { result.Process = Process.GetProcessById((int)info.ProcessId); }
            finally { CloseHandle(info.Thread); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        try
        {
            if (Process != null && !Process.HasExited) { Process.Kill(); Process.WaitForExit(); }
            if (processHandle != IntPtr.Zero)
            {
                uint code;
                if (!GetExitCodeProcess(processHandle, out code) || code == 259)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the private Player exit code");
                ExitCode = unchecked((int)code);
            }
        }
        finally
        {
            if (desktop != IntPtr.Zero) { CloseDesktop(desktop); desktop = IntPtr.Zero; }
            if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string Reserved, Desktop, Title;
        internal int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedBytes;
        internal IntPtr ReservedData, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
}
