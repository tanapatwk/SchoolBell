namespace SchoolBell.Services;

public class AudioService
{
    private readonly ILogger<AudioService> _logger;
    private readonly string _uploadPath;
    private System.Diagnostics.Process? _currentProcess;
    private readonly object _lock = new();
    private string? _currentFileName;
    private DateTime? _playbackStartedAt;
    private string? _lastPlaybackError;
    private string? _lastPlaybackErrorFileName;
    private DateTime? _lastPlaybackErrorAt;

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

    public DateTime? PlaybackStartedAt
    {
        get { lock (_lock) return _playbackStartedAt; }
    }

    public string? LastPlaybackError
    {
        get { lock (_lock) return _lastPlaybackError; }
    }

    public string? LastPlaybackErrorFileName
    {
        get { lock (_lock) return _lastPlaybackErrorFileName; }
    }

    public DateTime? LastPlaybackErrorAt
    {
        get { lock (_lock) return _lastPlaybackErrorAt; }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopCurrentProcessLocked();
            _currentProcess = null;
            _currentFileName = null;
            _playbackStartedAt = null;
        }
    }

    public async Task PlayAsync(string fileName)
    {
        var filePath = Path.Combine(_uploadPath, fileName);

        if (!File.Exists(filePath))
        {
            RecordPlaybackError(fileName, $"Audio file not found: {filePath}");
            return;
        }

        var ext = Path.GetExtension(fileName).ToLower();
        var (command, args) = ext switch
        {
            ".mp3" => ("mpg123", $"-q \"{filePath}\""),
            ".wav" => ("aplay", $"-q \"{filePath}\""),
            _ => (null, null)
        };

        if (command is null || args is null)
        {
            RecordPlaybackError(fileName, $"Unsupported format: {ext}");
            return;
        }

        _logger.LogInformation("Playing: {FileName}", fileName);

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            lock (_lock)
            {
                StopCurrentProcessLocked();

                process.Start();
                _currentProcess = process;
                _currentFileName = fileName;
                _playbackStartedAt = DateTime.Now;
                ClearLastPlaybackErrorLocked();
            }

            var errorOutputTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var errorOutput = (await errorOutputTask).Trim();

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(errorOutput)
                    ? $"{command} exited with code {process.ExitCode}"
                    : $"{command} exited with code {process.ExitCode}: {errorOutput}";
                RecordPlaybackError(fileName, message);
                return;
            }

            _logger.LogInformation("Finished playing: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            RecordPlaybackError(fileName, $"Playback failed: {ex.Message}", ex);
        }
        finally
        {
            lock (_lock)
            {
                if (_currentProcess == process)
                {
                    _currentProcess = null;
                    _currentFileName = null;
                    _playbackStartedAt = null;
                }
            }

            process.Dispose();
        }
    }

    private void StopCurrentProcessLocked()
    {
        if (_currentProcess is not { HasExited: false }) return;

        try
        {
            _currentProcess.Kill(entireProcessTree: true);
            _logger.LogInformation("Playback stopped");
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill.
        }
    }

    private void RecordPlaybackError(string fileName, string message, Exception? exception = null)
    {
        message = NormalizeErrorMessage(message);

        lock (_lock)
        {
            _lastPlaybackError = message;
            _lastPlaybackErrorFileName = fileName;
            _lastPlaybackErrorAt = DateTime.Now;
        }

        if (exception is null)
            _logger.LogWarning("Playback failed for {FileName}: {Message}", fileName, message);
        else
            _logger.LogError(exception, "Playback failed for {FileName}: {Message}", fileName, message);
    }

    private void ClearLastPlaybackErrorLocked()
    {
        _lastPlaybackError = null;
        _lastPlaybackErrorFileName = null;
        _lastPlaybackErrorAt = null;
    }

    private static string NormalizeErrorMessage(string message)
    {
        var normalized = message
            .ReplaceLineEndings(" ")
            .Trim();

        return normalized.Length <= 300
            ? normalized
            : normalized[..300] + "...";
    }
}
