using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;


namespace audiobookzone.Controllers
{
    [AllowAnonymous]
    [Route("api/audio")]
    public class AudioController : Controller
    {
        private class ConversionProgress
        {
            public int StartSegment { get; set; }
            public int HighestCompletedSegment { get; set; }
            public HashSet<int> CompletedSegments { get; set; } = new HashSet<int>();
            public Task? ConversionTask { get; set; }
            public CancellationTokenSource? CancellationToken { get; set; }
        }

        private readonly ILogger<AudioController> _logger;
        private readonly string _audioFolder = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "media");
        private readonly string _streamsFolder = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "streams");
        private readonly string _ffmpegPath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg", "ffmpeg.exe");
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _segmentLocks = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _conversionManagementLocks = new();
        private static readonly ConcurrentDictionary<string, ConversionProgress> _conversionProgress = new();
        private const int SEGMENT_DURATION = 10; // seconds

        public AudioController(ILogger<AudioController> logger)
        {
            _logger = logger;
        }

        // http://localhost:5000/api/audio/book1/playlist.m3u8
        [HttpGet("{audioId}/playlist.m3u8")]
        public async Task<IActionResult> GetPlaylist(string audioId)
        {
            _logger.LogInformation("Playlist requested for audioId: {AudioId}", audioId);

            // Check if user is authenticated
            //var username = HttpContext.Session.GetString("Username");
            //if (string.IsNullOrEmpty(username))
            //{
            //    return Unauthorized();
            //}

            var streamDir = System.IO.Path.Combine(_streamsFolder, audioId);
            var playlistPath = System.IO.Path.Combine(streamDir, "playlist.m3u8");

            if (!System.IO.File.Exists(playlistPath))
            {
                _logger.LogInformation("Generating initial playlist for audioId: {AudioId}", audioId);
                // Generate initial playlist with full duration
                await GenerateInitialPlaylist(audioId);
            }

            if (System.IO.File.Exists(playlistPath))
            {
                return new PhysicalFileResult(playlistPath, "application/vnd.apple.mpegurl");
            }

            return StatusCode(503, "Could not generate playlist. Please try again.");
        }

        [HttpGet("{audioId}/{segment}")]
        public async Task<IActionResult> GetSegment(string audioId, string segment)
        {
            // Check if user is authenticated
            //var username = HttpContext.Session.GetString("Username");
            //if (string.IsNullOrEmpty(username))
            //{
            //    return Unauthorized();
            //}

            var streamDir = System.IO.Path.Combine(_streamsFolder, audioId);
            var segmentPath = System.IO.Path.Combine(streamDir, segment);

            // Extract segment number from filename (e.g., stream003.ts -> 3)
            var segmentMatch = System.Text.RegularExpressions.Regex.Match(segment, @"stream(\d+)\.ts");
            if (!segmentMatch.Success)
            {
                _logger.LogWarning("Invalid segment request: {Segment} for audioId: {AudioId}", segment, audioId);
                return NotFound("Invalid segment name");
            }

            int segmentNumber = int.Parse(segmentMatch.Groups[1].Value);
            _logger.LogInformation("Segment {Segment} (#{SegmentNumber}) for audioId: {AudioId}, verifying availability...", 
                segment, segmentNumber, audioId);


            if (_conversionProgress.TryGetValue(audioId, out var check_progress))
            {
                if (check_progress.CompletedSegments.Contains(segmentNumber))
                {
                    _logger.LogInformation("Segment {SegmentNumber} is already marked as completed so returning it for audioId: {AudioId}", segmentNumber, audioId);
                    return new PhysicalFileResult(segmentPath, "video/MP2T");
                }
            }

            // Check if there's already a conversion running that will generate this segment
            if (_conversionProgress.TryGetValue(audioId, out var progress))
            {
                //if (segmentNumber >= progress.StartSegment)
                if (segmentNumber >= progress.StartSegment && segmentNumber <= progress.HighestCompletedSegment + 20) // Allow some lookahead
                {
                    _logger.LogInformation("Segment {SegmentNumber} will be generated by current conversion (started:{StartSegment}, current:{HighestCompletedSegment}), waiting...", 
                        segmentNumber, progress.StartSegment, progress.HighestCompletedSegment);
                    
                    // Wait for the segment to be generated (up to 30 seconds)
                    for (int i = 0; i < 300 && !IsSegmentReady(audioId, segmentNumber); i++)
                    {
                        await Task.Delay(100);
                    }
                    
                    if (IsSegmentReady(audioId, segmentNumber))
                    {
                        _logger.LogInformation("Segment {SegmentNumber} for audioId: {AudioId} is being returned", segmentNumber, audioId);
                        return new PhysicalFileResult(segmentPath, "video/MP2T");
                    }
                    else
                    {
                        _logger.LogError("Timeout waiting for segment {SegmentNumber} for audioId: {AudioId} during existing conversion", 
                            segmentNumber, audioId);
                        return StatusCode(503, "Segment generation timeout");
                    }
                }
            }

            // Synchronize conversion management to prevent race conditions
            var conversionLock = _conversionManagementLocks.GetOrAdd(audioId, _ => new SemaphoreSlim(1, 1));
            await conversionLock.WaitAsync();
            try
            {
                var existing_segments = new HashSet<int>();
                // Cancel current conversion and start from requested segment
                if (_conversionProgress.TryGetValue(audioId, out var existingProgress) && existingProgress.CancellationToken != null)
                {
                    _logger.LogInformation("Cancelling current conversion for audioId: {AudioId} (started:{StartSegment}, current:{HighestCompletedSegment}), will restart from segment {SegmentNumber}", 
                        audioId, existingProgress.StartSegment, existingProgress.HighestCompletedSegment, segmentNumber);
                    
                    existingProgress.CancellationToken.Cancel();
                    existing_segments = existingProgress.CompletedSegments;
                    // Wait for the conversion task to fully complete
                    while (existingProgress.CancellationToken != null)
                    {
                        _logger.LogInformation("Waiting for existing conversion task to exit for audioId: {AudioId}", audioId);
                        await Task.Delay(1000);
                    }
                    _logger.LogInformation("Existing conversion task has exited for audioId: {AudioId}", audioId);
                }

                // Start new conversion from this segment forward
                var newCts = new CancellationTokenSource();
                var newProgress = new ConversionProgress 
                { 
                    StartSegment = segmentNumber, 
                    HighestCompletedSegment = -1,
                    CancellationToken = newCts,
                    CompletedSegments = existing_segments // carry over existing segments if there are any
                };
                _conversionProgress[audioId] = newProgress;
                newProgress.ConversionTask = Task.Run(() => GenerateSegmentsFromPoint(audioId, segmentNumber, newCts.Token));
            }
            finally
            {
                conversionLock.Release();
            }

            // Wait for this specific segment to be generated
            for (int i = 0; i < 300 && !IsSegmentReady(audioId, segmentNumber); i++)
            {
                await Task.Delay(100);
                
                // Check if conversion was cancelled
                if (_conversionProgress.TryGetValue(audioId, out var existingProgress) && 
                    existingProgress.CancellationToken != null &&
                    existingProgress.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Conversion was cancelled while waiting for segment {SegmentNumber}", segmentNumber);
                    return StatusCode(503, "Segment generation was interrupted");
                }
            }

            if (IsSegmentReady(audioId, segmentNumber))
            {
                _logger.LogInformation("Segment {SegmentNumber} for audioId: {AudioId} is being returned", segmentNumber, audioId);
                return new PhysicalFileResult(segmentPath, "video/MP2T");
            }

            _logger.LogError("Timeout waiting for segment {SegmentNumber} for audioId: {AudioId}", segmentNumber, audioId);
            return StatusCode(503, "Segment generation timeout");
        }

        private async Task GenerateInitialPlaylist(string audioId)
        {
            var audioPath = System.IO.Path.Combine(_audioFolder, $"{audioId}.mp3");
            if (!System.IO.File.Exists(audioPath))
            {
                throw new System.IO.FileNotFoundException($"Audio file not found: {audioPath}");
            }

            // Get audio duration using ffprobe
            var duration = await GetAudioDuration(audioPath);
            var totalSegments = (int)Math.Ceiling(duration / SEGMENT_DURATION);

            var streamDir = System.IO.Path.Combine(_streamsFolder, audioId);
            System.IO.Directory.CreateDirectory(streamDir);

            var playlistPath = System.IO.Path.Combine(streamDir, "playlist.m3u8");

            // Create playlist manually with all segments
            var playlistContent = new System.Text.StringBuilder();
            playlistContent.AppendLine("#EXTM3U");
            playlistContent.AppendLine("#EXT-X-VERSION:3");
            playlistContent.AppendLine($"#EXT-X-TARGETDURATION:{SEGMENT_DURATION}");
            playlistContent.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
            playlistContent.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

            for (int i = 0; i < totalSegments; i++)
            {
                var isLastSegment = (i == totalSegments - 1);
                var segmentDuration = isLastSegment 
                    ? duration - (i * SEGMENT_DURATION) 
                    : SEGMENT_DURATION;

                playlistContent.AppendLine($"#EXTINF:{segmentDuration:F3},");
                playlistContent.AppendLine($"stream{i:D3}.ts");
            }

            playlistContent.AppendLine("#EXT-X-ENDLIST");

            await System.IO.File.WriteAllTextAsync(playlistPath, playlistContent.ToString());
            _logger.LogInformation("Generated playlist for audioId: {AudioId} with {TotalSegments} segments (duration: {Duration}s)", audioId, totalSegments, duration);
        }

        private async Task<double> GetAudioDuration(string audioPath)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg", "ffprobe.exe"),
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{audioPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float, 
                System.Globalization.CultureInfo.InvariantCulture, out var duration))
            {
                return duration;
            }

            return 0;
        }

        private bool IsSegmentReady(string audioId, int segmentNumber)
        {
            // FFmpeg progress tracking, what has ffmpeg finished converting
            if (_conversionProgress.TryGetValue(audioId, out var progress))
            {
                if (segmentNumber <= progress.HighestCompletedSegment)
                {
                    _logger.LogInformation("Segment {SegmentNumber} is ready (FFmpeg completed up to {CompletedSegment})", 
                        segmentNumber, progress.HighestCompletedSegment);
                    return true;
                }
            }

            return false;
        }

        private async Task GenerateSegmentsFromPoint(string audioId, int startSegment, CancellationToken cancellationToken)
        {
            try
            {
                var audioPath = System.IO.Path.Combine(_audioFolder, $"{audioId}.mp3");
                if (!System.IO.File.Exists(audioPath))
                {
                    _logger.LogError("Audio file not found: {AudioPath}", audioPath);
                    return;
                }

                var duration = await GetAudioDuration(audioPath);
                var startTime = startSegment * SEGMENT_DURATION;
                
                if (startTime >= duration)
                {
                    _logger.LogWarning("Start segment {StartSegment} is beyond audio duration ({Duration}s)", startSegment, duration);
                    return;
                }

                var streamDir = System.IO.Path.Combine(_streamsFolder, audioId);
                System.IO.Directory.CreateDirectory(streamDir);

                var segmentPattern = System.IO.Path.Combine(streamDir, $"stream%03d.ts");
                var playlistPath = System.IO.Path.Combine(streamDir, "playlist.m3u8");

                _logger.LogInformation("Starting FFmpeg conversion for audioId: {AudioId} from segment {StartSegment} (time: {StartTime}s) to end", 
                    audioId, startSegment, startTime);

                // Use FFmpeg to generate segments from startTime onwards
                // This maintains proper frame alignment across segments
                var ffmpeg_args_list = new List<string>
                {
                    "-ss", startTime.ToString(),
                    "-i", $"\"{audioPath}\"",
                    "-map", "0:a",
                    "-f", "segment",
                    "-segment_time", SEGMENT_DURATION.ToString(),
                    "-segment_start_number", startSegment.ToString(),
                    "-segment_format", "mpegts",
                    "-segment_format_options", "mpegts_flags=+initial_discontinuity",
                    "-c:a", "aac",
                    "-b:a", "128k",
                    "-ar", "44100",
                    "-ac", "2",
                    $"\"{segmentPattern}\""
                };
                string ffmpeg_args = string.Join(" ", ffmpeg_args_list);
                _logger.LogInformation("FFmpeg arguments: {Args}", ffmpeg_args);
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = ffmpeg_args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                // Monitor FFmpeg output for progress
                var errorTask = Task.Run(async () =>
                {
                    var reader = process.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        
                        // Parse FFmpeg progress: "time=00:01:23.45"
                        if (line.Contains("time="))
                        {
                            var timeMatch = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
                            if (timeMatch.Success)
                            {
                                var hours = int.Parse(timeMatch.Groups[1].Value);
                                var minutes = int.Parse(timeMatch.Groups[2].Value);
                                var seconds = double.Parse(timeMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                                var currentTime = hours * 3600 + minutes * 60 + seconds;
                                
                                // Calculate which segment FFmpeg has completed
                                // We consider a segment complete when FFmpeg is 2 segments ahead to be safe
                                var currentSegment = (int)(currentTime / SEGMENT_DURATION);
                                var completedSegment = Math.Max(0, currentSegment - 2) + startSegment;
                                
                                var progress = _conversionProgress.GetOrAdd(audioId, new ConversionProgress { StartSegment = startSegment, HighestCompletedSegment = -1 });
                                progress.HighestCompletedSegment = Math.Max(progress.HighestCompletedSegment, completedSegment);
                                
                                // Track each completed segment in the set
                                for (int i = progress.StartSegment; i <= completedSegment; i++)
                                {
                                    progress.CompletedSegments.Add(i);
                                }
                                



                                _logger.LogInformation("FFmpeg progress: {Time}s, completed segment: {CompletedSegment}", currentTime, completedSegment);
                            }
                        }
                        
                        // Log errors
                        if (line.Contains("Error") || line.Contains("error"))
                        {
                            _logger.LogWarning("FFmpeg: {Output}", line);
                        }
                    }
                });

                // Wait for process or cancellation
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Cancelling FFmpeg process for audioId: {AudioId} at segment {StartSegment}", audioId, startSegment);
                        try
                        {
                            _logger.LogInformation("Sending ffmpeg quit command for audioId: {AudioId}", audioId);
                            await process.StandardInput.WriteAsync('q');
                            await process.StandardInput.FlushAsync();
                            _logger.LogInformation("Quit command sent for audioId: {AudioId}", audioId);
                        }
                        catch { }
                        
                        // Wait up to 2 seconds for graceful exit
                        _logger.LogInformation("Waiting for graceful FFmpeg exit for audioId: {AudioId}", audioId);
                        for (int i = 0; i < 20 && !process.HasExited; i++)
                        {
                            await Task.Delay(100);
                        }
                        
                        // If still running, force kill it
                        if (!process.HasExited)
                        {
                            _logger.LogWarning("FFmpeg did not exit gracefully, force killing process for audioId: {AudioId}", audioId);
                            try
                            {
                                process.Kill(true); // true = kill entire process tree
                                await process.WaitForExitAsync();
                                _logger.LogInformation("FFmpeg process forcibly terminated for audioId: {AudioId}", audioId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error killing FFmpeg process for audioId: {AudioId}", audioId);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("FFmpeg exited gracefully for audioId: {AudioId}", audioId);
                        }
                        
                        break; // Exit the wait loop after handling cancellation
                    }
                    await Task.Delay(1000);
                }

                await errorTask;

                if (!cancellationToken.IsCancellationRequested && process.ExitCode == 0)
                {
                    // FFmpeg completed successfully - mark all segments as complete
                    var totalSegments = (int)Math.Ceiling(duration / SEGMENT_DURATION);
                    var finalSegment = totalSegments - 1;
                    
                    if (_conversionProgress.TryGetValue(audioId, out var progress))
                    {
                        progress.HighestCompletedSegment = finalSegment;
                        // Add all segments from start to final to the completed set
                        for (int i = startSegment; i <= finalSegment; i++)
                        {
                            progress.CompletedSegments.Add(i);
                        }
                    }
                    
                    _logger.LogInformation("Completed FFmpeg conversion for audioId: {AudioId} from segment {StartSegment}, all segments up to {FinalSegment} are ready", 
                        audioId, startSegment, finalSegment);
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError("FFmpeg process failed with exit code {ExitCode} for audioId: {AudioId}", process.ExitCode, audioId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GenerateSegmentsFromPoint for audioId: {AudioId}, segment: {StartSegment}", audioId, startSegment);
            }
            finally
            {
                // Clean up task and cancellation token, but keep progress data for future reference
                if (_conversionProgress.TryGetValue(audioId, out var progress))
                {
                    progress.ConversionTask = null;
                    progress.CancellationToken?.Dispose();
                    progress.CancellationToken = null;
                }
            }
        }

        // Optional: Background conversion for pre-caching segments
        [HttpPost("{audioId}/preload")]
        public async Task<IActionResult> StartBackgroundConversion(string audioId)
        {
            //var username = HttpContext.Session.GetString("Username");
            //if (string.IsNullOrEmpty(username))
            //{
            //    return Unauthorized();
            //}

            var conversionLock = _conversionManagementLocks.GetOrAdd(audioId, _ => new SemaphoreSlim(1, 1));
            await conversionLock.WaitAsync();
            try
            {
                if (!_conversionProgress.TryGetValue(audioId, out var progress) || progress.ConversionTask == null)
                {
                    _logger.LogInformation("Starting background conversion for audioId: {AudioId}", audioId);
                    var cts = new CancellationTokenSource();
                    var newProgress = new ConversionProgress
                    {
                        StartSegment = 0,
                        HighestCompletedSegment = -1,
                        CancellationToken = cts
                    };
                    _conversionProgress[audioId] = newProgress;
                    newProgress.ConversionTask = Task.Run(() => GenerateAllSegmentsBackground(audioId, cts.Token));
                }
                else
                {
                    _logger.LogInformation("Background conversion already running for audioId: {AudioId}", audioId);
                }
            }
            finally
            {
                conversionLock.Release();
            }

            return Ok(new { message = "Background conversion started" });
        }

        private async Task GenerateAllSegmentsBackground(string audioId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting full background conversion for audioId: {AudioId} from beginning", audioId);
            await GenerateSegmentsFromPoint(audioId, 0, cancellationToken);
        }
    }
}
