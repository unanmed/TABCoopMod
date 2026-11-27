using System;
using System.IO;
using System.Reflection;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Central version metadata shared across assembly attributes, networking handshakes,
    /// Steam lobby metadata, UI headers, and version-tagged log records.
    /// </summary>
    public static class ModInfo
    {
        public const string Version = "0.2.14";
        public const string Tag = "v" + Version;
        public const string AssemblyVersion = "0.2.14.0";

        public static string GetBuildTimestamp()
        {
            try
            {
                string loc = typeof(ModInfo).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    return File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { }
            return "unknown";
        }
    }
}
