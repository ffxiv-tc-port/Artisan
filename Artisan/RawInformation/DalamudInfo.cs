using ECommons.DalamudServices;
using ECommons.Reflection;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Artisan.RawInformation
{
    internal static class DalamudInfo
    {
        public static bool StagingChecked = false;
        public static bool IsStaging = false;
        private static bool checkStarted = false;

        // IsOnStaging() is polled every Draw() frame; the actual check does blocking network/file I/O,
        // so it must never run on the calling (UI) thread. Kick it off once in the background and
        // report "not staging" until the background check lands.
        public static bool IsOnStaging()
        {
            if (StagingChecked)
            {
                return IsStaging;
            }

            if (!checkStarted)
            {
                checkStarted = true;
                Task.Run(CheckStaging);
            }

            return false;
        }

        private static void CheckStaging()
        {
            if (DalamudReflector.TryGetDalamudStartInfo(out var startinfo, Svc.PluginInterface))
            {
                try
                {
                    HttpClient client = new HttpClient();
                    var dalDeclarative = "https://raw.githubusercontent.com/goatcorp/dalamud-declarative/refs/heads/main/config.yaml";
                    using (var stream = client.GetStreamAsync(dalDeclarative).Result)
                    using (var reader = new StreamReader(stream))
                    {
                        for (int i = 0; i <= 4; i++)
                        {
                            var line = reader.ReadLine().Trim();
                            if (i != 4) continue;
                            var version = line.Split(":").Last().Trim().Replace("'", "");
                            if (version != startinfo.GameVersion.ToString())
                            {
                                StagingChecked = true;
                                IsStaging = false;
                                return;
                            }
                        }
                    }
                }
                catch
                {
                    // Something has gone wrong with checking the Dalamud github file, just allow plugin load anyway
                    StagingChecked = true;
                    IsStaging = false;
                    return;
                }

                if (File.Exists(startinfo.ConfigurationPath))
                {
                    try
                    {
                        var file = File.ReadAllText(startinfo.ConfigurationPath);
                        var ob = JsonConvert.DeserializeObject<dynamic>(file);
                        string type = ob.DalamudBetaKind;
                        if (type is not null && !string.IsNullOrEmpty(type) && type != "release")
                        {
                            StagingChecked = true;
                            IsStaging = true;
                            return;
                        }
                        else
                        {
                            StagingChecked = true;
                            IsStaging = false;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Svc.Chat.PrintError($"Unable to detrermine Dalamud staging due to file being config being unreadable.");
                        StagingChecked = true;
                        IsStaging = false;
                        return;
                    }
                }
                else
                {
                    StagingChecked = true;
                    IsStaging = false;
                    return;
                }
            }
        }
    }
}
