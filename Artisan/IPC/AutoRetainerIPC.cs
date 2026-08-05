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
    /// Was <c>ReEnable = GetSuppressed()</c>, which is the wrong way round: "AutoRetainer.GetSuppressed"
    /// reports whether AutoRetainer is ALREADY suppressed, so the old <c>Suppress()</c> only fired when
    /// AutoRetainer was suppressed already (a no-op), and in the normal case did nothing at all - leaving
    /// AutoRetainer's scheduler live while Artisan drives the very same retainer windows. The mirror image
    /// was just as wrong: <c>Unsuppress()</c> then cleared a suppression the user had set deliberately.
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

        // Clear the flag first: if the IPC throws we must not stay armed forever, and the watchdog in
        // RetainerInfo.Tick would otherwise keep retrying every single frame.
        ReEnable = false;
        if (SetSuppressed(false))
            PluginLog.Information("[Artisan] Restored AutoRetainer after the retainer restock.");
    }
}
