using System;
using System.IO;
using Microsoft.Win32;

namespace Pablo_Switcher;

public static class AutostartService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Ім'я параметра в реєстрі (можна винести в константи версії/програми)
    private const string AppRegistryKeyName = "AutoSwitcher";

    /// <summary>
    /// Повертає шлях до .exe файлу або null, якщо програму запущено через хост dotnet.exe.
    /// </summary>
    public static string? GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return null;

        // Захист: ігноруємо запуск через `dotnet run`
        string fileName = Path.GetFileName(processPath);
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        return processPath;
    }

    /// <summary>
    /// Перевіряє, чи увімкнено автозапуск у реєстрі поточного користувача.
    /// </summary>
    public static bool IsAutostartEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        object? value = key?.GetValue(AppRegistryKeyName);
        return value is string str && !string.IsNullOrWhiteSpace(str);
    }

    /// <summary>
    /// Вмикає або вимикає автозапуск програми разом із Windows.
    /// </summary>
    public static bool SetAutostart(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null)
                return false;

            if (!enable)
            {
                key.DeleteValue(AppRegistryKeyName, throwOnMissingValue: false);
                return true;
            }

            string? execPath = GetExecutablePath();
            if (execPath is null)
                return false;

            // Лапки обов'язкові для коректної обробки пробілів у шляху
            key.SetValue(AppRegistryKeyName, $"\"{execPath}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            // Можливий UnauthorizedAccessException, якщо антивірус блокує зміну гілки Run
            return false;
        }
    }
}