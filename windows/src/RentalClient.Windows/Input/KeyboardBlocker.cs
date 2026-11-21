using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RentalClient.Windows.Input;

// Installs a low-level keyboard hook (WH_KEYBOARD_LL) that swallows shortcuts
// which would let the user escape the lock overlay. Globally scoped while
// installed — intercepts at the system level, before any window receives the
// keystroke — so install ONLY while the overlay is shown and uninstall on
// hide / dispose.
//
// Blocked:
//   - Windows key (LWIN, RWIN)   — any combo (Win, Win+L, Win+D, Win+R, ...)
//   - Alt+Tab                    — task switcher
//   - Alt+Esc                    — cycle windows
//   - Alt+F4                     — close foreground window
//   - Ctrl+Esc                   — start menu
//   - Ctrl+Shift+Esc             — task manager
//
// NOT blocked (cannot be intercepted from user space):
//   - Ctrl+Alt+Del (SAS)         — production mitigation via Group Policy
//                                   (HKLM\...\System\DisableLockWorkstation,
//                                   DisableTaskMgr) or shell-replacement.
//                                   Both belong to S30 iCafe8 deployment.
//   - Power button               — registry / power policy.
//
// Defensive:
//   - try/catch around the callback so an exception can't get Windows to
//     silently unhook us (LowLevelHooksTimeout, default 300 ms).
//   - Idempotent Install/Uninstall.
//   - Delegate held as field so GC can't collect while the unmanaged hook
//     still holds a function pointer to it.
public sealed class KeyboardBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_CONTROL = 0x11;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_F4 = 0x73;

    // KBDLLHOOKSTRUCT.flags bit indicating ALT is held during this event.
    private const uint LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _proc;
    private readonly object _gate = new();
    private IntPtr _hookHandle = IntPtr.Zero;

    public KeyboardBlocker()
    {
        _proc = HookCallback;
    }

    public bool IsInstalled
    {
        get
        {
            lock (_gate) return _hookHandle != IntPtr.Zero;
        }
    }

    public void Install()
    {
        lock (_gate)
        {
            if (_hookHandle != IntPtr.Zero) return;

            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            var hMod = GetModuleHandle(moduleName);
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"SetWindowsHookEx(WH_KEYBOARD_LL) failed with Win32 error {err}.");
            }
        }
    }

    public void Uninstall()
    {
        lock (_gate)
        {
            if (_hookHandle == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hook;
        lock (_gate) hook = _hookHandle;

        if (nCode < 0) return CallNextHookEx(hook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (ShouldBlock(info))
                {
                    // Non-zero return = swallow the keystroke.
                    return new IntPtr(1);
                }
            }
            catch
            {
                // Never let an exception out of the hook callback.
            }
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(KBDLLHOOKSTRUCT info)
    {
        var vk = (int)info.vkCode;
        var altDown = (info.flags & LLKHF_ALTDOWN) != 0;

        if (vk == VK_LWIN || vk == VK_RWIN) return true;

        if (altDown && (vk == VK_TAB || vk == VK_ESCAPE || vk == VK_F4)) return true;

        // Ctrl+Esc (Start) and Ctrl+Shift+Esc (Task Manager) both share this guard.
        if (vk == VK_ESCAPE && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) return true;

        return false;
    }
}
