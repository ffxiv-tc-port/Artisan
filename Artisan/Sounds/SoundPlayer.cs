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

        // A fresh device per sound, torn down explicitly: NAudio refuses to Init() a WaveOutEvent
        // again while it is still playing ("Can't re-initialize during playback"), which is what a
        // second notification arriving during the first one would hit with a single shared device.
        private static WaveOutEvent? _device;
        private static Mp3FileReader? _reader;

        // Set by Dispose() under _lockObj. A request queued just before unload can reach the
        // thread pool after Dispose(); without this it would open a device nobody tears down.
        private static bool _disposed;

        public static void PlaySound()
        {
            // Reading/decoding the mp3 and initializing the playback device are blocking calls;
            // this is called from Framework.Update-driven code (e.g. Endurance completion), so
            // run it off the main thread. NAudio's WaveOutEvent isn't tied to the render thread
            // the way Dalamud's D3D11 texture APIs are, so backgrounding it entirely is safe.
            Task.Run(() =>
            {
                // Nothing is logged while _lockObj is held: Svc.Log goes through Dalamud's
                // Serilog sink, which does file I/O and takes locks of its own, and Dispose() takes the same lock.
                // Lines are recorded where they occur and emitted once the lock is released; the emit sits in a
                // finally so that an exception on the way out still writes whatever had already been produced.
                string? pendingTearDownMessage = null;
                Exception? pendingError = null;

                try
                {
                    lock (_lockObj)
                    {
                        try
                        {
                            if (_disposed) return;

                            string sound = "Time Up";
                            string path = Path.Combine(Svc.PluginInterface.AssemblyLocation.Directory.FullName, "Sounds", $"{sound}.mp3");
                            if (!File.Exists(path)) return;

                            // Latest notification wins: stop whatever is still playing
                            // instead of either throwing or overlapping.
                            TearDown(out pendingTearDownMessage);

                            var reader = new Mp3FileReader(path);
                            var device = new WaveOutEvent { Volume = P.Config.SoundVolume };
                            device.Init(reader);
                            device.Play();

                            _reader = reader;
                            _device = device;
                        }
                        catch (Exception ex)
                        {
                            pendingError = ex;
                        }
                    }
                }
                finally
                {
                    if (pendingTearDownMessage is not null)
                        Svc.Log.Debug(pendingTearDownMessage);

                    if (pendingError is not null)
                        pendingError.Log();
                }
            });
        }

        /// <summary>Stops and releases any current playback. Safe to call repeatedly.</summary>
        public static void Dispose()
        {
            string? pendingTearDownMessage = null;

            try
            {
                lock (_lockObj)
                {
                    _disposed = true;
                    TearDown(out pendingTearDownMessage);
                }
            }
            finally
            {
                if (pendingTearDownMessage is not null)
                    Svc.Log.Debug(pendingTearDownMessage);
            }
        }

        // Caller must hold _lockObj. The one line this can produce is handed back instead of
        // written: every caller holds the lock, and Svc.Log does file I/O behind locks of its
        // own. Keeping the write out of this method also means nothing reachable from inside
        // _lockObj can log, not even by accident.
        private static void TearDown(out string? pendingMessage)
        {
            pendingMessage = null;

            try
            {
                _device?.Stop();
            }
            catch (Exception ex)
            {
                // Stopping a device that already finished on its own is not interesting.
                pendingMessage = $"SoundPlayer: stopping previous playback failed: {ex.Message}";
            }

            _device?.Dispose();
            _reader?.Dispose();
            _device = null;
            _reader = null;
        }
    }
}
