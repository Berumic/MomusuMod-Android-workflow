using System.IO;
using MelonLoader.Utils;

namespace MonsterMusumeTDMod.Android;

internal static class AndroidPaths
{
    public static string PluginPath => MelonEnvironment.UserDataDirectory;
    public static string ModDirectory => Path.Combine(PluginPath, "MonsterMusumeTDMod");
}
