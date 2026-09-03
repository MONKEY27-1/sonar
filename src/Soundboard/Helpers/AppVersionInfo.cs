using System.Reflection;

namespace Soundboard.Helpers;

public static class AppVersionInfo
{
    public static string Current => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
}
