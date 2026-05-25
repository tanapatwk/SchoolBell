using SchoolBell.Data;
using SchoolBell.Models;
using Microsoft.EntityFrameworkCore;

namespace SchoolBell.Services;

public class AudioFileService
{
    private readonly AppDbContext _db;
    private readonly string _uploadPath;

    public AudioFileService(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _uploadPath = Path.Combine(env.WebRootPath, "uploads");
        Directory.CreateDirectory(_uploadPath);
    }

    public async Task<List<AudioFile>> GetAllAsync() =>
        await _db.AudioFiles.ToListAsync();

    public async Task<AudioFile> SaveAsync(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLower();
        if (ext != ".mp3" && ext != ".wav")
            throw new InvalidOperationException("รองรับเฉพาะ MP3 และ WAV เท่านั้น");

        var fileName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(_uploadPath, fileName);

        await using var stream = File.Create(filePath);
        await file.CopyToAsync(stream);

        var audioFile = new AudioFile
        {
            FileName = fileName,
            OriginalName = file.FileName,
            FileSize = file.Length
        };

        _db.AudioFiles.Add(audioFile);
        await _db.SaveChangesAsync();
        return audioFile;
    }

    public async Task<List<string>> GetUsedBySchedulesAsync(int id)
    {
        return await _db.Schedules
            .Where(s => s.AudioFileId == id)
            .Select(s => s.Name)
            .ToListAsync();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var audioFile = await _db.AudioFiles.FindAsync(id);
        if (audioFile is null) return false;

        var filePath = Path.Combine(_uploadPath, audioFile.FileName);
        if (File.Exists(filePath)) File.Delete(filePath);

        _db.AudioFiles.Remove(audioFile);
        return await _db.SaveChangesAsync() > 0;
    }
}

