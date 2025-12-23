using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace audiobookzone.Controllers
{
    public class PlayerController : Controller
    {
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<PlayerController> _logger;

        public PlayerController(IWebHostEnvironment environment, ILogger<PlayerController> logger)
        {
            _environment = environment;
            _logger = logger;
        }

        public IActionResult Index()
        {
            // Check if user is logged in
            var username = HttpContext.Session.GetString("Username");
            if (string.IsNullOrEmpty(username))
            {
                return RedirectToAction("Login", "Account");
            }

            ViewBag.Username = username;

            // Get list of audio files from media folder
            var audioFiles = GetAudioFiles();
            _logger.LogInformation("Total audio files found: {Count}", audioFiles.Count);
            ViewBag.AudioFiles = audioFiles;

            return View();
        }

        private List<AudioFileInfo> GetAudioFiles()
        {
            var audioFiles = new List<AudioFileInfo>();
            var mediaPath = Path.Combine(_environment.ContentRootPath, "media");

            _logger.LogInformation("Loading media files from path: {MediaPath}", mediaPath);
            
            HashSet<string> supportedExtensions = new HashSet<string> { ".mp3", ".m4a", ".m4b", ".wav", ".aac" };
            if (Directory.Exists(mediaPath))
            {
                var files = Directory.GetFiles(mediaPath);
                _logger.LogInformation("Found {FileCount} files in media directory.", files.Length);

                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file).ToLower();
                    if (!supportedExtensions.Contains(extension))
                    {
                        _logger.LogInformation("Skipping unsupported file: {FileName}", file);
                        continue;
                    }
                    _logger.LogInformation("Adding supported file: {FileName}", file);
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var fileInfo = new FileInfo(file);
                    audioFiles.Add(new AudioFileInfo
                    {
                        Id = fileName,
                        DisplayName = fileName.Replace("_", " ").Replace("-", " "),
                        FileName = Path.GetFileName(file),
                        FileSize = FormatFileSize(fileInfo.Length)
                    });
                }
            }
            else
            {
                _logger.LogWarning("Media directory does not exist: {MediaPath}", mediaPath);
            }

            return audioFiles.OrderBy(f => f.DisplayName).ToList();
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public IActionResult Play(string id)
        {
            // Check if user is logged in
            var username = HttpContext.Session.GetString("Username");
            if (string.IsNullOrEmpty(username))
            {
                return RedirectToAction("Login", "Account");
            }

            ViewBag.AudioId = id;
            ViewBag.Username = username;
            return View();
        }
    }

    public class AudioFileInfo
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? FileName { get; set; }
        public string? FileSize { get; set; }
    }
}