using Microsoft.EntityFrameworkCore;
using SchoolBell.Data;
using SchoolBell.Models;

namespace SchoolBell.Services;

public class ScheduleService
{
    private readonly AppDbContext _db;

    public ScheduleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Schedule>> GetAllAsync() =>
        await _db.Schedules.Include(s => s.AudioFile).ToListAsync();

    public async Task<Schedule?> GetByIdAsync(int id) =>
        await _db.Schedules.Include(s => s.AudioFile).FirstOrDefaultAsync(s => s.Id == id);

    public async Task<Schedule> CreateAsync(Schedule schedule)
    {
        await ValidateAsync(schedule);
        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync();
        return schedule;
    }

    public async Task<bool> UpdateAsync(Schedule schedule)
    {
        if (!await _db.Schedules.AnyAsync(s => s.Id == schedule.Id))
            return false;

        await ValidateAsync(schedule);
        _db.Schedules.Update(schedule);
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var schedule = await _db.Schedules.FindAsync(id);
        if (schedule is null) return false;
        _db.Schedules.Remove(schedule);
        return await _db.SaveChangesAsync() > 0;
    }

    private async Task ValidateAsync(Schedule schedule)
    {
        schedule.Name = schedule.Name.Trim();

        if (string.IsNullOrWhiteSpace(schedule.Name))
            throw new InvalidOperationException("กรุณาใส่ชื่อตาราง");

        if (schedule.Name.Length > 100)
            throw new InvalidOperationException("ชื่อตารางต้องไม่เกิน 100 ตัวอักษร");

        var hasDay = schedule.Monday
            || schedule.Tuesday
            || schedule.Wednesday
            || schedule.Thursday
            || schedule.Friday
            || schedule.Saturday
            || schedule.Sunday;
        if (!hasDay)
            throw new InvalidOperationException("กรุณาเลือกอย่างน้อยหนึ่งวัน");

        var audioExists = await _db.AudioFiles.AnyAsync(a => a.Id == schedule.AudioFileId);
        if (!audioExists)
            throw new InvalidOperationException("ไม่พบไฟล์เสียงที่เลือก");

        if (schedule.IsEnabled)
        {
            var schedulesAtSameTime = await _db.Schedules
                .Where(s => s.Id != schedule.Id && s.IsEnabled && s.Time == schedule.Time)
                .ToListAsync();
            var duplicate = schedulesAtSameTime.Any(existing => HasOverlappingDay(existing, schedule));
            if (duplicate)
                throw new InvalidOperationException("มีตารางกริ่งที่เปิดใช้งานในเวลาและวันเดียวกันอยู่แล้ว");
        }
    }

    private static bool HasOverlappingDay(Schedule left, Schedule right) =>
        (left.Monday && right.Monday)
        || (left.Tuesday && right.Tuesday)
        || (left.Wednesday && right.Wednesday)
        || (left.Thursday && right.Thursday)
        || (left.Friday && right.Friday)
        || (left.Saturday && right.Saturday)
        || (left.Sunday && right.Sunday);
}
