using Dalamud.Plugin;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Reflection;
using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Artisan.IPC
{
    /// <summary>
    /// 可從任何執行緒呼叫的「已載入插件實例」查詢。
    /// 框架執行緒上照舊走 <see cref="DalamudReflector.TryGetDalamudPlugin(string, out IDalamudPlugin, bool, bool)"/>；
    /// 其他執行緒走同一套反射但不寫 ECommons 的 pluginCache（裸 Dictionary，框架執行緒同時在寫，找到插件就寫，與 ignoreCache 無關）。
    /// </summary>
    internal static class PluginLookup
    {
        internal static bool TryGetLoaded(string internalName, [NotNullWhen(true)] out IDalamudPlugin? instance)
        {
            if (Svc.Framework is { IsInFrameworkUpdateThread: true })
                return DalamudReflector.TryGetDalamudPlugin(internalName, out instance, false, true);
            return TryGetWithoutCache(internalName, out instance);
        }

        // 查找段與 ECommons 的 TryGetDalamudPlugin 逐步相同（InstalledPlugins 是 PluginManager 在鎖內拍的快照）。
        // 刻意少掉兩個寫入：pluginCache，以及「找到但沒載入」那一行 InternalLog（也是不上鎖的環形緩衝）。
        private static bool TryGetWithoutCache(string internalName, [NotNullWhen(true)] out IDalamudPlugin? instance)
        {
            try
            {
                var pluginManager = DalamudReflector.GetPluginManager();
                var installedPlugins = (IList)pluginManager.GetType().GetProperty("InstalledPlugins")!.GetValue(pluginManager)!;
                foreach (var t in installedPlugins)
                {
                    if ((string?)t.GetType().GetProperty("InternalName")!.GetValue(t) != internalName)
                        continue;
                    var type = t.GetType().Name == "LocalDevPlugin" ? t.GetType().BaseType! : t.GetType();
                    var plugin = (IDalamudPlugin?)type.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(t);
                    if (plugin != null)
                    {
                        instance = plugin;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                PluginLog.Error($"Can't find {internalName} plugin: " + e.Message);
                PluginLog.Error(e.StackTrace ?? "");
            }

            instance = null;
            return false;
        }
    }
}
