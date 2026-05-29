using SchoolBell.Data;
using SchoolBell.Models;
using Microsoft.EntityFrameworkCore;

namespace SchoolBell.Services;

public class AudioFileService
{
    private const long MaxFileSizeBytes = 25 * 1024 * 1024;
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
        if (file.Length <= 0)
            throw new InvalidOperationException("ไฟล์เสียงว่างเปล่า");

        if (file.Length > MaxFileSizeBytes)
            throw new InvalidOperationException("ไฟล์เสียงต้องมีขนาดไม่เกิน 25 MB");

        var ext = Path.GetExtension(file.FileName).ToLower();
        if (ext != ".mp3" && ext != ".wav")
            throw new InvalidOperationException("รองรับเฉพาะ MP3 และ WAV เท่านั้น");

        if (!await HasValidAudioHeaderAsync(file, ext))
            throw new InvalidOperationException("ไฟล์เสียงไม่ถูกต้องหรือชนิดไฟล์ไม่ตรงกับนามสกุล");

        var originalName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalName))
            originalName = $"audio{ext}";

        var duplicateExists = await _db.AudioFiles.AnyAsync(audio =>
            audio.OriginalName.ToLower() == originalName.ToLower()
            && audio.FileSize == file.Length);
        if (duplicateExists)
            throw new InvalidOperationException("ไฟล์เสียงนี้ถูกอัปโหลดไว้แล้ว");

        var fileName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(_uploadPath, fileName);

        await using var stream = File.Create(filePath);
        await file.CopyToAsync(stream);

        var audioFile = new AudioFile
        {
            FileName = fileName,
            OriginalName = originalName,
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

        var usedBy = await GetUsedBySchedulesAsync(id);
        if (usedBy.Count > 0)
            throw new InvalidOperationException(
                "ไม่สามารถลบได้ เพราะไฟล์นี้ถูกใช้งานในตารางกริ่ง: " + string.Join(", ", usedBy));

        _db.AudioFiles.Remove(audioFile);
        var changed = await _db.SaveChangesAsync() > 0;
        if (!changed) return false;

        var filePath = Path.Combine(_uploadPath, audioFile.FileName);
        if (File.Exists(filePath)) File.Delete(filePath);

        return true;
    }

    private static async Task<bool> HasValidAudioHeaderAsync(IFormFile file, string ext)
    {
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header);

        if (ext == ".wav")
        {
            return read >= 12
                && header[0] == 'R'
                && header[1] == 'I'
                && header[2] == 'F'
                && header[3] == 'F'
                && header[8] == 'W'
                && header[9] == 'A'
                && header[10] == 'V'
                && header[11] == 'E';
        }

        if (ext == ".mp3")
        {
            var hasId3Tag = read >= 3 && header[0] == 'I' && header[1] == 'D' && header[2] == '3';
            var hasMp3FrameSync = read >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
            return hasId3Tag || hasMp3FrameSync;
        }

        return false;
    }
}
