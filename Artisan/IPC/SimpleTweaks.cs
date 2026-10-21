using ECommons.Logging;
using ECommons.Reflection;
using System;

namespace Artisan.IPC
{
    internal static unsafe class SimpleTweaks
    {
        // 反射查一次 SimpleTweaks 要配置 1～2 KB，下面兩個每幀入口因此最多每秒真的查一次。
        // 時間戳各由各自的呼叫執行緒單寫（DisableImprovedLogTweak＝框架更新、IsFocusTweakEnabled＝配方筆記疊加層繪製），
        // 不加鎖 ⇒ 不要從 IPC 端點或背景執行緒呼叫。
        private const long RecheckIntervalMs = 1000;
        private static long nextImprovedLogCheck;
        private static long nextFocusTweakCheck;
        private static bool focusTweakEnabled;

        internal static bool IsEnabled()
        {
            return DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out _, false, true);
        }

        internal static bool IsFocusTweakEnabled()
        {
            var now = Environment.TickCount64;
            if (now < nextFocusTweakCheck)
                return focusTweakEnabled;
            // 先推時間戳再查：查詢擲例外時也不會變成每幀重試。
            nextFocusTweakCheck = now + RecheckIntervalMs;
            focusTweakEnabled = QueryFocusTweakEnabled();
            return focusTweakEnabled;
        }

        private static bool QueryFocusTweakEnabled()
        {
            if (!IsEnabled()) return false;
            if (DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out var tweaks, false, true) && tweaks != null)
            {
                var plugin = tweaks.GetFoP("Plugin");
                if (plugin == null) return false;

                var baseTweak = plugin?.Call("GetTweakById", ["UiAdjustments@AutoFocusRecipeSearch", plugin.GetFoP("Tweaks")]);
                if (baseTweak == null) return false;
                bool enabled = (bool)baseTweak.GetFoP("Enabled");
                return enabled;
            }

            return false;
        }

        internal static bool IsImprovedLogEnabled()
        {
            var baseTweak = FindImprovedLogTweak();
            return baseTweak != null && (bool)baseTweak.GetFoP("Enabled");
        }

        // 使用者在 SimpleTweaks 重新打開這個 tweak 後，最多一秒內會被關掉；
        // 聊天視窗那一句仍然只在真的關掉它的那一次說。
        internal static void DisableImprovedLogTweak()
        {
            var now = Environment.TickCount64;
            if (now < nextImprovedLogCheck)
                return;
            nextImprovedLogCheck = now + RecheckIntervalMs;

            var baseTweak = FindImprovedLogTweak();
            if (baseTweak == null || !(bool)baseTweak.GetFoP("Enabled")) return;
            baseTweak.SetFoP("Enabled", false);

            DuoLog.Information($"Improved Crafting Log Tweak is disabled whilst using Artisan.");
        }

        // 上一次 GetTweakById 解析到的 tweak 與當時的 SimpleTweaks 實例；只在框架執行緒讀寫。
        // 真的 Tweaks 每次都把全部 tweak 依名稱排序一遍（約 4 KB），所以同一個實例、tweak 沒被處置就沿用；
        // SimpleTweaks 換實例或 tweak 已處置（讀不到 IsDisposed 也算）就照舊重新解析。查不到（null）不沿用。
        private static object? improvedLogOwner;
        private static object? improvedLogTweak;

        private static object? FindImprovedLogTweak()
        {
            // 查不到就放掉快取，不讓已停用或移除的 SimpleTweaks 實例與 tweak 一直被留著。
            if (!DalamudReflector.TryGetDalamudPlugin("SimpleTweaksPlugin", out var tweaks, false, true) || tweaks == null)
            {
                improvedLogOwner = improvedLogTweak = null;
                return null;
            }
            if (ReferenceEquals(tweaks, improvedLogOwner) && improvedLogTweak != null && improvedLogTweak.GetFoP("IsDisposed") is false)
                return improvedLogTweak;

            var plugin = tweaks.GetFoP("Plugin");
            if (plugin == null)
            {
                improvedLogOwner = improvedLogTweak = null;
                return null;
            }

            var baseTweak = plugin.Call("GetTweakById", ["ImprovedCraftingLog", plugin.GetFoP("Tweaks")]);
            improvedLogOwner = tweaks;
            improvedLogTweak = baseTweak;
            return baseTweak;
        }
    }
}
