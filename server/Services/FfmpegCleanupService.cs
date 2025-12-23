using System.Collections.Concurrent;
using System.Diagnostics;

namespace audiobookzone.Services
{
    public class FfmpegCleanupService : IHostedService
    {
        private readonly ILogger<FfmpegCleanupService> _logger;
        public static readonly ConcurrentBag<Process> ActiveProcesses = new();

        public FfmpegCleanupService(ILogger<FfmpegCleanupService> logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("FFmpeg Cleanup Service started");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("FFmpeg Cleanup Service stopping - cleaning up {Count} active FFmpeg processes", ActiveProcesses.Count);

            var cleanupTasks = new List<Task>();

            while (ActiveProcesses.TryTake(out var process))
            {
                if (process != null && !process.HasExited)
                {
                    cleanupTasks.Add(CleanupProcess(process));
                }
            }

            await Task.WhenAll(cleanupTasks);
            _logger.LogInformation("FFmpeg Cleanup Service stopped - all processes cleaned up");
        }

        private async Task CleanupProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                _logger.LogInformation("Attempting graceful shutdown of FFmpeg process {ProcessId}", process.Id);
                
                // Try graceful shutdown first
                try
                {
                    await process.StandardInput.WriteAsync('q');
                    await process.StandardInput.FlushAsync();
                }
                catch { }

                // Wait up to 2 seconds for graceful exit
                for (int i = 0; i < 20 && !process.HasExited; i++)
                {
                    await Task.Delay(100);
                }

                // Force kill if still running
                if (!process.HasExited)
                {
                    _logger.LogWarning("FFmpeg process {ProcessId} did not exit gracefully, force killing", process.Id);
                    process.Kill(true); // true = kill entire process tree
                    await process.WaitForExitAsync();
                    _logger.LogInformation("FFmpeg process {ProcessId} forcibly terminated", process.Id);
                }
                else
                {
                    _logger.LogInformation("FFmpeg process {ProcessId} exited gracefully", process.Id);
                }

                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up FFmpeg process");
            }
        }
    }
}
