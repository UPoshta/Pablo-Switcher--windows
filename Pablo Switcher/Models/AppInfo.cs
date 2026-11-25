using System.Reflection;

namespace Pablo_Switcher.Models;

public static class AppInfo
{
    public const string AppName = "Pablo Switcher";
    public const string Version = "1.0.1";

    // Аналог BUNDLE_ID у Windows (використовується для Toast-сповіщень та ідентифікації в ОС)
    public const string AppUserModelId = "OurTube.AutoSwitcher";

    /// <summary>
    /// Повертає версію з метаданих збірки (.csproj) або резервну константу.
    /// </summary>
    public static string GetAssemblyVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : Version;
    }
}
