namespace SchoolBell.Services;

public class AudioService
{
    private readonly ILogger<AudioService> _logger;
    private readonly string _uploadPath;
    private System.Diagnostics.Process? _currentProcess;
    private readonly object _lock = new();
    private string? _currentFileName;

    public AudioService(ILogger<AudioService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _uploadPath = Path.Combine(env.WebRootPath, "uploads");
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _currentProcess is { HasExited: false };
        }
    }

    public string? CurrentFileName
    {
        get { lock (_lock) return _currentFileName; }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill();
                _logger.LogInformation("Playback stopped");
            }
            _currentProcess = null;
            _currentFileName = null;
        }
    }

    public async Task PlayAsync(string fileName)
    {
        var filePath = Path.Combine(_uploadPath, fileName);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Audio file not found: {FilePath}", filePath);
            return;
        }

        Stop();

        var ext = Path.GetExtension(fileName).ToLower();
        var (command, args) = ext switch
        {
            ".mp3" => ("mpg123", $"-q \"{filePath}\""),
            ".wav" => ("aplay", $"-q \"{filePath}\""),
            _ => throw new NotSupportedException($"Unsupported format: {ext}")
        };

        _logger.LogInformation("Playing: {FileName}", fileName);

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        lock (_lock)
        {
            process.Start();
            _currentProcess = process;
            _currentFileName = fileName;
        }

        await process.WaitForExitAsync();

        lock (_lock)
        {
            if (_currentProcess == process)
            {
                _currentProcess = null;
                _currentFileName = null;
            }
        }

        _logger.LogInformation("Finished playing: {FileName}", fileName);
    }
}
