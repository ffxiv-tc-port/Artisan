using Artisan.GameInterop.CSExt;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OtterGui.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Artisan;
public static class Extensions
{
    public static async Task<IDalamudTextureWrap> TryLoadIconAsync(this TextureCache cache, uint iconid)
    {
        var icon = await cache.TextureProvider.GetFromGameIcon(new GameIconLookup(iconid)).RentAsync();
        return icon;
    }

    public static Job Add(this Job job, uint other)
    {
        return (Job)((uint)job + other);
    }
}