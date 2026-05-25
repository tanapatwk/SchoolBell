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
        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync();
        return schedule;
    }

    public async Task<bool> UpdateAsync(Schedule schedule)
    {
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
}

