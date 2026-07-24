using ECommons;
using ECommons.DalamudServices;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Artisan.Sounds
{
    public static class SoundPlayer
    {
        private static readonly object _lockObj = new();

        private static WaveOutEvent waveOut = new();

        public static void PlaySound()
        {
            // Reading/decoding the mp3 and initializing the playback device are blocking calls;
            // this is called from Framework.Update-driven code (e.g. Endurance completion), so
            // run it off the main thread. NAudio's WaveOutEvent isn't tied to the render thread
            // the way Dalamud's D3D11 texture APIs are, so backgrounding it entirely is safe.
            Task.Run(() =>
            {
                lock (_lockObj)
                {
                    try
                    {
                        string sound = "Time Up";
                        string path = Path.Combine(Svc.PluginInterface.AssemblyLocation.Directory.FullName, "Sounds", $"{sound}.mp3");
                        if (!File.Exists(path)) return;
                        var reader = new Mp3FileReader(path);

                        waveOut.Init(reader);
                        var previousVol = waveOut.Volume;
                        waveOut.Volume = P.Config.SoundVolume;
                        waveOut.Play();
                        waveOut.PlaybackStopped += (sender, args) => waveOut.Volume = previousVol;
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                }
            });
        }
    }
}
