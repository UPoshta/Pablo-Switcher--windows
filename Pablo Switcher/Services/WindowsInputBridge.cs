using Pablo_Switcher.Interop;
using Pablo_Switcher.Models;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Pablo_Switcher.Services;

public sealed class WindowsInputBridge : IWindowsInputBridge
{
    public static readonly FrozenSet<string> DefaultExcludedProcesses = new[]
    {
        // Термінали
        "WindowsTerminal.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wezterm-gui.exe",
        "alacritty.exe",
        "mintty.exe",

        // Редактори коду та IDE
        "Code.exe",
        "Cursor.exe",
        "Zed.exe",
        "sublime_text.exe",
        "pycharm64.exe",
        "idea64.exe",
        "rider64.exe",
        "devenv.exe",

        // Менеджери паролів та безпека
        "1Password.exe",
        "Bitwarden.exe",
        "KeePassXC.exe",
        "KeePass.exe"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public event Action<int, char?>? KeyPressed;
    public event Action<int>? KeyReleased;

    public FrozenSet<string> ExcludedProcessNames { get; }

    private readonly Win32Native.LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public WindowsInputBridge(IEnumerable<string>? excludedProcesses = null)
    {
        ExcludedProcessNames = excludedProcesses is not null
            ? excludedProcesses.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            : DefaultExcludedProcesses;

        // Зберігаємо делегат у полі класу, щоб GC не зібрав його під час роботи хука
        _hookProc = HookCallback;
    }

    public void StartHook()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        IntPtr moduleHandle = Win32Native.GetModuleHandle(curModule?.ModuleName);

        _hookHandle = Win32Native.SetWindowsHookEx(
            Win32Native.WH_KEYBOARD_LL,
            _hookProc,
            moduleHandle,
            0);
    }

    public void StopHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Win32Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<Win32Native.KBDLLHOOKSTRUCT>(lParam);

            // Ігноруємо натискання, які згенерувала сама програма через SendInput
            if ((hookStruct.flags & Win32Native.LLKHF_INJECTED) == 0)
            {
                int vkCode = (int)hookStruct.vkCode;

                if (msg is Win32Native.WM_KEYDOWN or Win32Native.WM_SYSKEYDOWN)
                {
                    char? ch = ResolveCharFromVk(hookStruct.vkCode, hookStruct.scanCode);
                    KeyPressed?.Invoke(vkCode, ch);
                }
                else if (msg is Win32Native.WM_KEYUP or Win32Native.WM_SYSKEYUP)
                {
                    KeyReleased?.Invoke(vkCode);
                }
            }
        }

        return Win32Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static char? ResolveCharFromVk(uint vkCode, uint scanCode)
    {
        // Якщо затиснутий Ctrl або Alt (без AltGr), це комбінація клавіш, а не друкований символ
        bool ctrlDown = (Win32Native.GetKeyState(VirtualKeys.Control) & 0x8000) != 0;
        bool altDown = (Win32Native.GetKeyState(VirtualKeys.Alt) & 0x8000) != 0;
        if (ctrlDown && !altDown)
            return null;

        byte[] keyState = new byte[256];
        if ((Win32Native.GetKeyState(VirtualKeys.Shift) & 0x8000) != 0)
            keyState[VirtualKeys.Shift] = 0x80;
        if ((Win32Native.GetKeyState(VirtualKeys.CapsLock) & 0x0001) != 0)
            keyState[VirtualKeys.CapsLock] = 0x01;
        if (ctrlDown && altDown) // Підтримка AltGr
        {
            keyState[VirtualKeys.Control] = 0x80;
            keyState[VirtualKeys.Alt] = 0x80;
        }

        IntPtr foregroundHwnd = Win32Native.GetForegroundWindow();
        uint threadId = Win32Native.GetWindowThreadProcessId(foregroundHwnd, out _);
        IntPtr hkl = Win32Native.GetKeyboardLayout(threadId);

        var sb = new StringBuilder(4);
        // Прапор 0x04 (Windows 10 1607+) не очищає стан dead-keys у системі
        int count = Win32Native.ToUnicodeEx(vkCode, scanCode, keyState, sb, sb.Capacity, 0x04, hkl);

        return count == 1 ? sb[0] : null;
    }

    public string? GetForegroundProcessName()
    {
        IntPtr hwnd = Win32Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        Win32Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        IntPtr hProcess = Win32Native.OpenProcess(Win32Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (Win32Native.QueryFullProcessImageName(hProcess, 0, sb, ref size))
            {
                return Path.GetFileName(sb.ToString());
            }
            return null;
        }
        finally
        {
            Win32Native.CloseHandle(hProcess);
        }
    }

    public bool ShouldBypassAutoSwitch()
    {
        string? procName = GetForegroundProcessName();
        if (!string.IsNullOrEmpty(procName) && ExcludedProcessNames.Contains(procName))
            return true;

        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is not null && focused.Current.IsPassword)
                return true;
        }
        catch
        {
            // UI Automation може кидати ElementNotAvailableException при швидкому закритті вікна
        }

        return false;
    }

    public Task SwitchToLanguageAsync(LangCode targetLang)
    {
        string klid = targetLang switch
        {
            LangCode.En => "00000409", // English (US)
            LangCode.Uk => "00000422", // Ukrainian
            _ => "00000409"
        };

        IntPtr hkl = Win32Native.LoadKeyboardLayout(klid, Win32Native.KLF_NOTELLSHELL);
        if (hkl == IntPtr.Zero)
        {
            TraceLogger.Log("ERROR", $"Не вдалося завантажити HKL для {klid}");
            return Task.CompletedTask;
        }

        // 0x0001 = INPUTLANGCHANGE_SYSCHARSET (обов'язково для коректного оновлення TSF у Windows 11)
        IntPtr wParam = new(0x0001);
        IntPtr foreground = Win32Native.GetForegroundWindow();
        IntPtr focused = GetFocusedOrForegroundWindow();

        if (focused != IntPtr.Zero)
        {
            Win32Native.PostMessage(focused, Win32Native.WM_INPUTLANGCHANGEREQUEST, wParam, hkl);
        }

        if (foreground != IntPtr.Zero && foreground != focused)
        {
            Win32Native.PostMessage(foreground, Win32Native.WM_INPUTLANGCHANGEREQUEST, wParam, hkl);
        }

        return Task.CompletedTask;
    }

    private static IntPtr GetFocusedOrForegroundWindow()
    {
        IntPtr foreground = Win32Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return IntPtr.Zero;

        uint threadId = Win32Native.GetWindowThreadProcessId(foreground, out _);
        var guiInfo = new Win32Native.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<Win32Native.GUITHREADINFO>()
        };

        if (Win32Native.GetGUIThreadInfo(threadId, ref guiInfo))
        {
            if (guiInfo.hwndFocus != IntPtr.Zero)
                return guiInfo.hwndFocus;
            if (guiInfo.hwndActive != IntPtr.Zero)
                return guiInfo.hwndActive;
        }

        return foreground;
    }

    public void SendBackspace()
    {
        var inputs = new Win32Native.INPUT[2];

        inputs[0].type = Win32Native.INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VirtualKeys.Backspace;

        inputs[1].type = Win32Native.INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VirtualKeys.Backspace;
        inputs[1].U.ki.dwFlags = Win32Native.KEYEVENTF_KEYUP;

        Win32Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Native.INPUT>());
    }

    public void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new Win32Native.INPUT[text.Length * 2];
        int idx = 0;

        foreach (char ch in text)
        {
            inputs[idx].type = Win32Native.INPUT_KEYBOARD;
            inputs[idx].U.ki.wScan = ch;
            inputs[idx].U.ki.dwFlags = Win32Native.KEYEVENTF_UNICODE;
            idx++;

            inputs[idx].type = Win32Native.INPUT_KEYBOARD;
            inputs[idx].U.ki.wScan = ch;
            inputs[idx].U.ki.dwFlags = Win32Native.KEYEVENTF_UNICODE | Win32Native.KEYEVENTF_KEYUP;
            idx++;
        }

        Win32Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Native.INPUT>());
    }

    public async Task<string?> CaptureSelectedTextAsync()
    {
        // 1. Спроба отримати виділення напряму через UI Automation (без буфера обміну)
        string? uiaText = TryGetSelectionViaUia();
        if (!string.IsNullOrEmpty(uiaText))
            return uiaText;

        // 2. Резервний спосіб через буфер обміну (Ctrl+C) в STA-потоці WPF
        if (Application.Current?.Dispatcher is null)
            return null;

        uint oldSeq = Win32Native.GetClipboardSequenceNumber();
        string? previousClipboardText = null;
        bool hadText = false;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    previousClipboardText = Clipboard.GetText();
                    hadText = true;
                }
            }
            catch
            {
                // Буфер обміну може бути тимчасово заблокований іншим процесом
            }
        });

        SendCtrlC();

        string? copiedText = null;
        long deadline = Environment.TickCount64 + 120;

        while (Environment.TickCount64 < deadline)
        {
            if (Win32Native.GetClipboardSequenceNumber() != oldSeq)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            copiedText = Clipboard.GetText();
                        }
                    }
                    catch
                    {
                        // Ігноруємо тимчасове блокування буфера
                    }
                });

                if (!string.IsNullOrEmpty(copiedText))
                    break;
            }

            await Task.Delay(10);
        }

        if (copiedText is not null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (hadText && previousClipboardText is not null)
                        Clipboard.SetText(previousClipboardText);
                    else
                        Clipboard.Clear();
                }
                catch
                {
                    // Ігноруємо помилку відновлення буфера
                }
            });
        }

        return string.IsNullOrEmpty(copiedText) ? null : copiedText;
    }

    private static string? TryGetSelectionViaUia()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null)
                return null;

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObj) &&
                patternObj is TextPattern textPattern)
            {
                TextPatternRange[] ranges = textPattern.GetSelection();
                if (ranges.Length > 0)
                {
                    string selected = ranges[0].GetText(-1);
                    return string.IsNullOrEmpty(selected) ? null : selected;
                }
            }
        }
        catch
        {
            // Не всі елементи підтримують TextPattern
        }

        return null;
    }

    private static void SendCtrlC()
    {
        const ushort vkC = 0x43; // 'C'
        var inputs = new Win32Native.INPUT[4];

        inputs[0].type = Win32Native.INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VirtualKeys.LControl;

        inputs[1].type = Win32Native.INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = vkC;

        inputs[2].type = Win32Native.INPUT_KEYBOARD;
        inputs[2].U.ki.wVk = vkC;
        inputs[2].U.ki.dwFlags = Win32Native.KEYEVENTF_KEYUP;

        inputs[3].type = Win32Native.INPUT_KEYBOARD;
        inputs[3].U.ki.wVk = VirtualKeys.LControl;
        inputs[3].U.ki.dwFlags = Win32Native.KEYEVENTF_KEYUP;

        Win32Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Native.INPUT>());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopHook();
            _disposed = true;
        }
    }
}
