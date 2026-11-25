using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pablo_Switcher.Services;

public static class TraceLogger
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    public static bool IsEnabled { get; private set; }

    public static void Initialize(bool enableConsole)
    {
        if (!enableConsole || IsEnabled)
            return;

        if (AllocConsole())
        {
            Console.OutputEncoding = Encoding.UTF8;
            var writer = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8)
            {
                AutoFlush = true
            };
            Console.SetOut(writer);
            IsEnabled = true;

            Log("INIT", "Трасування активовано. Очікування вводу...");
        }
    }

    public static void Log(string stage, string message)
    {
        if (!IsEnabled)
            return;

        string ts = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"[{ts}] [{stage,-6}] {message}");
    }

    public static void Shutdown()
    {
        if (IsEnabled)
        {
            IsEnabled = false;
            FreeConsole();
        }
    }
}
