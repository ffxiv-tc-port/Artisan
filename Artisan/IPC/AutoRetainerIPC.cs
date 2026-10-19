using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.Reflection;
using System;

namespace Artisan.IPC;

internal static class AutoRetainerIPC
{
    /// <summary>
    /// True only when *we* were the ones who suppressed AutoRetainer, so <see cref="Unsuppress"/> restores
    /// exactly what we changed and never clears a suppression the user set for themselves.
    /// </summary>
    internal static bool ReEnable = false;

    private static bool Installed => DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out _, false, true);

    /// <summary>Whether AutoRetainer is currently suppressed. Returns null when it cannot be asked.</summary>
    private static bool? GetSuppressed()
    {
        if (!Installed) return null;
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
        }
        catch (Exception e)
        {
            PluginLog.Warning($"[Artisan] Could not read AutoRetainer.GetSuppressed: {e.Message}");
            return null;
        }
    }

    private static bool SetSuppressed(bool value)
    {
        if (!Installed) return false;
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(value);
            return true;
        }
        catch (Exception e)
        {
            PluginLog.Warning($"[Artisan] Could not call AutoRetainer.SetSuppressed({value}): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when AutoRetainer is loaded and NOT suppressed. "AutoRetainer.GetSuppressed" reports whether
    /// AutoRetainer is ALREADY suppressed, so <c>Suppress()</c> must only act (and arm <c>ReEnable</c>) when this
    /// is true - otherwise <c>Unsuppress()</c> would clear a suppression the user had set deliberately.
    /// </summary>
    internal static bool IsEnabled() => GetSuppressed() == false;

    internal static void Suppress()
    {
        var suppressed = GetSuppressed();
        if (suppressed is not false)
            return; // not installed, unreadable, or somebody else already suppressed it - leave it alone

        if (SetSuppressed(true))
        {
            ReEnable = true;
            PluginLog.Information("[Artisan] Suppressed AutoRetainer for the duration of the retainer restock.");
        }
    }

    internal static void Unsuppress()
    {
        if (!ReEnable)
            return;

        // The authoritative state is AutoRetainer's own suppression flag, not our local ReEnable: ReEnable
        // only records that "we still owe AutoRetainer a restore", so clear it only once the IPC call went
        // through. SetSuppressed returns false when AutoRetainer is missing or the call threw - the restore
        // did NOT land, so stay armed and let the next Unsuppress (the watchdog in RetainerInfo.Tick, or the
        // next restock's cleanup) retry.
        if (SetSuppressed(false))
        {
            ReEnable = false;
            PluginLog.Information("[Artisan] Restored AutoRetainer after the retainer restock.");
        }
        else
        {
            PluginLog.Information("[Artisan] Could not restore AutoRetainer yet (IPC unavailable); staying armed to retry so it is not left suppressed.");
        }
    }
}
