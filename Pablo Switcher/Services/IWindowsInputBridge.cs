using Pablo_Switcher.Models;
using System;
using System.Collections.Frozen;
using System.Threading.Tasks;

namespace Pablo_Switcher.Services;

public interface IWindowsInputBridge : IDisposable
{
    event Action<int, char?>? KeyPressed;
    event Action<int>? KeyReleased;

    FrozenSet<string> ExcludedProcessNames { get; }

    void StartHook();
    void StopHook();

    string? GetForegroundProcessName();
    bool ShouldBypassAutoSwitch();

    Task SwitchToLanguageAsync(LangCode targetLang);
    void SendBackspace();
    void SendUnicodeText(string text);
    Task<string?> CaptureSelectedTextAsync();
}
