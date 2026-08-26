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

        // A single shared WaveOutEvent used to be created once and Init()ed again for
        // every sound. NAudio refuses that while the device is still playing:
        //     Can't re-initialize during playback
        //       at NAudio.Wave.WaveOutEvent.Init(IWaveProvider)
        // which is exactly what a second notification arriving during the first one
        // produced (reported on TC 2026-07-29).
        //
        // Two more leaks lived in the same method and go away with it:
        //   - the Mp3FileReader was never disposed - one file handle per sound; and
        //   - a PlaybackStopped handler was added on EVERY call and never removed, so
        //     the handler list grew for the whole session, and the "restore the
        //     previous volume" logic it carried ended up fighting itself (every
        //     handler restored whatever the volume happened to be when IT was added).
        // A fresh device per sound, torn down explicitly, has none of those problems.
        private static WaveOutEvent? _device;
        private static Mp3FileReader? _reader;

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

                        // Latest notification wins: stop whatever is still playing
                        // instead of either throwing or overlapping.
                        TearDown();

                        var reader = new Mp3FileReader(path);
                        var device = new WaveOutEvent { Volume = P.Config.SoundVolume };
                        device.Init(reader);
                        device.Play();

                        _reader = reader;
                        _device = device;
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                }
            });
        }

        /// <summary>Stops and releases any current playback. Safe to call repeatedly.</summary>
        public static void Dispose()
        {
            lock (_lockObj)
                TearDown();
        }

        // Caller must hold _lockObj.
        private static void TearDown()
        {
            try
            {
                _device?.Stop();
            }
            catch (Exception ex)
            {
                // Stopping a device that already finished on its own is not interesting.
                Svc.Log.Debug($"SoundPlayer: stopping previous playback failed: {ex.Message}");
            }

            _device?.Dispose();
            _reader?.Dispose();
            _device = null;
            _reader = null;
        }
    }
}
