using Pablo_Switcher.Models;
using Pablo_Switcher.Services;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Pablo_Switcher.Services;

public sealed class AsyncKeyboardEngine : IDisposable
{
    private static readonly FrozenSet<int> EvaluationLengths = new[] { 3, 4 }.ToFrozenSet();

    private const long InjectionCooldownMs = 50;
    private const int BackspaceDelayMs = 5;
    private const int LayoutSwitchDelayMs = 20;
    private const long DoubleTapWindowMs = 350;

    private static readonly FrozenDictionary<HotkeyPreset, FrozenSet<int>> DoubleTapKeySets =
        new Dictionary<HotkeyPreset, FrozenSet<int>>
        {
            [HotkeyPreset.DoubleShift] = VirtualKeys.ShiftKeys,
            [HotkeyPreset.DoubleAlt] = VirtualKeys.AltKeys,
            [HotkeyPreset.DoubleCtrl] = VirtualKeys.CtrlKeys,
            [HotkeyPreset.DoubleWin] = VirtualKeys.WinKeys,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<HotkeyPreset, int> SingleTapKeys =
        new Dictionary<HotkeyPreset, int>
        {
            [HotkeyPreset.KeyF12] = VirtualKeys.F12,
            [HotkeyPreset.KeyF6] = VirtualKeys.F6,
            [HotkeyPreset.KeyCapsLock] = VirtualKeys.CapsLock,
        }.ToFrozenDictionary();

    private readonly PrefixLanguageDetector _detector;
    private readonly IWindowsInputBridge _bridge;
    private readonly Action<LangCode, string, string, string> _onSwitchCallback;

    private readonly List<char> _buffer = new();
    private readonly List<char> _lastWordBuffer = new();
    private readonly HashSet<int> _pressedModifiers = new();
    private readonly Channel<KeyStrokeEvent> _eventChannel;
    private readonly SemaphoreSlim _actionLock = new(1, 1);

    private volatile bool _enabled;
    private volatile HotkeyPreset _hotkeyPreset;
    private string? _currentProcessName;
    private LastConversionRecord? _lastConversion;
    private bool _suppressAutoForCurrentWord;

    private volatile bool _isInjecting;
    private long _lastInjectionTs;
    private long _lastCleanModTs;
    private bool _modUsedWithOtherKey;

    private CancellationTokenSource? _cts;
    private Task? _consumerTask;

    public AsyncKeyboardEngine(
        PrefixLanguageDetector detector,
        IWindowsInputBridge bridge,
        HotkeyPreset hotkeyPreset,
        bool enabled,
        Action<LangCode, string, string, string> onSwitchCallback)
    {
        _detector = detector;
        _bridge = bridge;
        _hotkeyPreset = hotkeyPreset;
        _enabled = enabled;
        _onSwitchCallback = onSwitchCallback;

        _currentProcessName = _bridge.GetForegroundProcessName();

        _eventChannel = Channel.CreateUnbounded<KeyStrokeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                EnqueueReset();
            }
        }
    }

    public HotkeyPreset HotkeyPreset
    {
        get => _hotkeyPreset;
        set
        {
            _hotkeyPreset = value;
            _lastCleanModTs = 0;
            _modUsedWithOtherKey = false;
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => EventConsumerLoopAsync(_cts.Token));

        _bridge.KeyPressed += OnKeyPress;
        _bridge.KeyReleased += OnKeyRelease;
        _bridge.StartHook();
    }

    public void Stop()
    {
        _bridge.KeyPressed -= OnKeyPress;
        _bridge.KeyReleased -= OnKeyRelease;
        _bridge.StopHook();

        _cts?.Cancel();
        _eventChannel.Writer.TryComplete();
    }

    public void NotifyActiveAppChanged(string? processName)
    {
        _currentProcessName = processName;
        EnqueueReset();
    }

    public void ForceConvertWordOrSelection()
    {
        _ = Task.Run(ExecuteForceConversionAsync);
    }

    private void EnqueueReset()
    {
        _eventChannel.Writer.TryWrite(
            new KeyStrokeEvent(null, 0, Environment.TickCount64, ResetOnly: true));
    }

    private void ClearAllBuffers()
    {
        _buffer.Clear();
        _lastWordBuffer.Clear();
        _lastConversion = null;
        _suppressAutoForCurrentWord = false;
    }

    private void OnKeyPress(int vkCode, char? ch)
    {
        long now = Environment.TickCount64;

        if (_isInjecting || (now - Volatile.Read(ref _lastInjectionTs)) < InjectionCooldownMs)
            return;

        HotkeyPreset preset = _hotkeyPreset;

        if (SingleTapKeys.TryGetValue(preset, out int singleTarget) && vkCode == singleTarget)
        {
            ForceConvertWordOrSelection();
            return;
        }

        if (DoubleTapKeySets.TryGetValue(preset, out var targetModSet) && targetModSet.Contains(vkCode))
        {
            bool hasOtherMods = false;
            foreach (int mod in _pressedModifiers)
            {
                if (!targetModSet.Contains(mod))
                {
                    hasOtherMods = true;
                    break;
                }
            }

            if (!hasOtherMods)
                _modUsedWithOtherKey = false;

            if (VirtualKeys.ModifierKeys.Contains(vkCode))
                _pressedModifiers.Add(vkCode);

            return;
        }

        _modUsedWithOtherKey = true;

        if (VirtualKeys.ShiftKeys.Contains(vkCode))
            return;

        if (VirtualKeys.ModifierKeys.Contains(vkCode))
        {
            _pressedModifiers.Add(vkCode);
            return;
        }

        if (_pressedModifiers.Count > 0)
        {
            _eventChannel.Writer.TryWrite(new KeyStrokeEvent(null, vkCode, now, ResetOnly: true));
            return;
        }

        _eventChannel.Writer.TryWrite(new KeyStrokeEvent(ch, vkCode, now));
    }

    private void OnKeyRelease(int vkCode)
    {
        _pressedModifiers.Remove(vkCode);

        HotkeyPreset preset = _hotkeyPreset;
        if (DoubleTapKeySets.TryGetValue(preset, out var targetModSet) &&
            targetModSet.Contains(vkCode) &&
            !_isInjecting)
        {
            long now = Environment.TickCount64;
            if (!_modUsedWithOtherKey && _pressedModifiers.Count == 0)
            {
                if ((now - _lastCleanModTs) <= DoubleTapWindowMs)
                {
                    _lastCleanModTs = 0;
                    ForceConvertWordOrSelection();
                }
                else
                {
                    _lastCleanModTs = now;
                }
            }
            else
            {
                _lastCleanModTs = 0;
            }
        }
    }

    private async Task EventConsumerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (KeyStrokeEvent ev in _eventChannel.Reader.ReadAllAsync(ct))
            {
                await _actionLock.WaitAsync(ct);
                try
                {
                    if (!_enabled || ev.ResetOnly)
                    {
                        if (_buffer.Count > 0)
                            TraceLogger.Log("RESET", ev.ResetOnly ? "Скидання (модифікатор/гаряча клавіша)" : "Рушій на паузі");
                        ClearAllBuffers();
                        continue;
                    }

                    if (!string.IsNullOrEmpty(_currentProcessName) &&
                        _bridge.ExcludedProcessNames.Contains(_currentProcessName))
                    {
                        TraceLogger.Log("SKIP", $"Процес у списку виключень: {_currentProcessName}");
                        ClearAllBuffers();
                        continue;
                    }

                    if (ev.VirtualKeyCode == VirtualKeys.Backspace)
                    {
                        if (_buffer.Count > 0)
                        {
                            _buffer.RemoveAt(_buffer.Count - 1);
                            TraceLogger.Log("KEY", $"Backspace -> буфер: '{new string(_buffer.ToArray())}'");
                        }
                        else if (_lastWordBuffer.Count > 0)
                        {
                            _buffer.AddRange(_lastWordBuffer);
                            _lastWordBuffer.Clear();
                            TraceLogger.Log("KEY", $"Backspace -> відновлено попереднє слово: '{new string(_buffer.ToArray())}'");
                        }
                        continue;
                    }

                    if (VirtualKeys.WordBreakKeys.Contains(ev.VirtualKeyCode))
                    {
                        if (_buffer.Count > 0)
                        {
                            TraceLogger.Log("BREAK", $"Роздільник (VK=0x{ev.VirtualKeyCode:X2}) -> слово зафіксовано: '{new string(_buffer.ToArray())}'");
                            _lastWordBuffer.Clear();
                            _lastWordBuffer.AddRange(_buffer);
                            _buffer.Clear();
                        }
                        _suppressAutoForCurrentWord = false;
                        continue;
                    }

                    if (ev.Char is not char c)
                        continue;

                    if (char.IsWhiteSpace(c))
                    {
                        if (_buffer.Count > 0)
                        {
                            _lastWordBuffer.Clear();
                            _lastWordBuffer.AddRange(_buffer);
                            _buffer.Clear();
                        }
                        _suppressAutoForCurrentWord = false;
                        continue;
                    }

                    if (_buffer.Count == 0)
                    {
                        _lastWordBuffer.Clear();
                        _lastConversion = null;
                    }

                    _buffer.Add(c);
                    TraceLogger.Log("BUF", $"Символ '{c}' -> буфер[{_buffer.Count}]: '{new string(_buffer.ToArray())}'");

                    if (!_suppressAutoForCurrentWord && EvaluationLengths.Contains(_buffer.Count))
                    {
                        await ProcessAutoEvaluationAsync();
                    }
                }
                finally
                {
                    _actionLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Коректне завершення фонового циклу
        }
    }

    private async Task ProcessAutoEvaluationAsync()
    {
        if (_buffer.Count == 0)
            return;

        if (_bridge.ShouldBypassAutoSwitch())
        {
            TraceLogger.Log("SKIP", "Блокування автозаміни (поле пароля або захищений контекст)");
            ClearAllBuffers();
            return;
        }

        string currentToken = new(_buffer.ToArray());
        var decision = _detector.EvaluateSwitch(currentToken);
        if (decision is null)
            return;

        await ReplaceTextAndSwitchAsync(
            original: currentToken,
            converted: decision.ConvertedText,
            sourceLang: decision.SourceLang,
            targetLang: decision.TargetLang,
            mode: "auto");
    }

    private async Task ExecuteForceConversionAsync()
    {
        TraceLogger.Log("FORCE", $"Запуск ручної конвертації (пресет: {_hotkeyPreset})");
        if (_isInjecting)
            return;

        await _actionLock.WaitAsync();
        try
        {
            if (_isInjecting)
                return;

            string? selectedText = await _bridge.CaptureSelectedTextAsync();
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                TraceLogger.Log("SELECT", $"Знайдено виділений текст ({selectedText.Length} симв.): '{selectedText}'");
                var forcedSel = _detector.ForceTransliterate(selectedText);
                if (forcedSel is not null)
                {
                    await ReplaceSelectedTextAndSwitchAsync(
                        original: selectedText,
                        converted: forcedSel.ConvertedText,
                        targetLang: forcedSel.TargetLang);
                    return;
                }
            }

            List<char> targetBuffer = _buffer.Count > 0 ? _buffer : _lastWordBuffer;
            if (targetBuffer.Count == 0)
                return;

            string currentText = new(targetBuffer.ToArray());

            if (_lastConversion is { } rec &&
                currentText.StartsWith(rec.ConvertedToken, StringComparison.Ordinal))
            {
                string tail = currentText[rec.ConvertedToken.Length..];
                string revertedTail = string.Empty;

                if (tail.Length > 0)
                {
                    var tailRes = _detector.ForceTransliterate(tail, preferredSource: rec.TargetLang);
                    revertedTail = tailRes?.ConvertedText ?? tail;
                }

                string restoredText = rec.OriginalToken + revertedTail;
                TraceLogger.Log("REVERT", $"Відкат автозаміни: '{currentText}' -> '{restoredText}'");
                _lastConversion = null;
                _suppressAutoForCurrentWord = true;

                await ReplaceTextAndSwitchAsync(
                    original: currentText,
                    converted: restoredText,
                    sourceLang: rec.TargetLang,
                    targetLang: rec.SourceLang,
                    mode: "manual");
                return;
            }

            var forced = _detector.ForceTransliterate(currentText);
            if (forced is null)
                return;

            _lastConversion = null;
            _suppressAutoForCurrentWord = true;

            await ReplaceTextAndSwitchAsync(
                original: currentText,
                converted: forced.ConvertedText,
                sourceLang: forced.SourceLang,
                targetLang: forced.TargetLang,
                mode: "manual");
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task ReplaceSelectedTextAndSwitchAsync(
        string original,
        string converted,
        LangCode targetLang)
    {
        _isInjecting = true;
        try
        {
            await _bridge.SwitchToLanguageAsync(targetLang);
            await Task.Delay(LayoutSwitchDelayMs);

            _bridge.SendUnicodeText(converted);
            ClearAllBuffers();

            _onSwitchCallback(targetLang, original, converted, "selection");
        }
        finally
        {
            Volatile.Write(ref _lastInjectionTs, Environment.TickCount64);
            _isInjecting = false;
        }
    }

    private async Task ReplaceTextAndSwitchAsync(
        string original,
        string converted,
        LangCode sourceLang,
        LangCode targetLang,
        string mode)
    {
        _isInjecting = true;
        try
        {
            bool hadTrailingBreak = _buffer.Count == 0 && _lastWordBuffer.Count > 0;
            int deleteCount = original.Length + (hadTrailingBreak ? 1 : 0);

            for (int i = 0; i < deleteCount; i++)
            {
                _bridge.SendBackspace();
                await Task.Delay(BackspaceDelayMs);
            }

            await _bridge.SwitchToLanguageAsync(targetLang);
            await Task.Delay(LayoutSwitchDelayMs);

            string textToInject = hadTrailingBreak ? $"{converted} " : converted;
            TraceLogger.Log("SWITCH", $"[{mode}] Видаляємо {deleteCount} симв., перемикаємо на {targetLang}, вставляємо '{textToInject}'");
            _bridge.SendUnicodeText(textToInject);

            if (hadTrailingBreak)
            {
                _lastWordBuffer.Clear();
                _lastWordBuffer.AddRange(converted);
                _buffer.Clear();
            }
            else
            {
                _buffer.Clear();
                _buffer.AddRange(converted);
                _lastWordBuffer.Clear();
            }

            if (mode == "auto")
            {
                _lastConversion = new LastConversionRecord(
                    OriginalToken: original,
                    ConvertedToken: converted,
                    SourceLang: sourceLang,
                    TargetLang: targetLang,
                    TimestampMs: Environment.TickCount64);
            }

            _onSwitchCallback(targetLang, original, converted, mode);
        }
        finally
        {
            Volatile.Write(ref _lastInjectionTs, Environment.TickCount64);
            _isInjecting = false;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _actionLock.Dispose();
    }
}
