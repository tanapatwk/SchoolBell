using Microsoft.EntityFrameworkCore;
using Quartz;
using SchoolBell.Data;
using SchoolBell.Services;

namespace SchoolBell.Jobs;

public class BellJob : IJob
{
    private readonly AppDbContext _db;
    private readonly AudioService _audio;
    private readonly ILogger<BellJob> _logger;

    public BellJob(AppDbContext db, AudioService audio, ILogger<BellJob> logger)
    {
        _db = db;
        _audio = audio;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var today = DateTime.Now.DayOfWeek;

        var schedules = await _db.Schedules
            .Include(s => s.AudioFile)
            .Where(s => s.IsEnabled)
            .ToListAsync();

        foreach (var schedule in schedules)
        {
            // เช็คว่าเวลาตรงกันไหม (ภายใน 1 นาที)
            var diff = Math.Abs((now - schedule.Time).TotalSeconds);
            if (diff > 60) continue;

            // เช็ควันที่ตรงกันไหม
            var dayMatch = today switch
            {
                DayOfWeek.Monday    => schedule.Monday,
                DayOfWeek.Tuesday   => schedule.Tuesday,
                DayOfWeek.Wednesday => schedule.Wednesday,
                DayOfWeek.Thursday  => schedule.Thursday,
                DayOfWeek.Friday    => schedule.Friday,
                DayOfWeek.Saturday  => schedule.Saturday,
                DayOfWeek.Sunday    => schedule.Sunday,
                _ => false
            };

            if (!dayMatch) continue;

            _logger.LogInformation("Triggering bell: {Name} at {Time}", schedule.Name, schedule.Time);

            if (schedule.AudioFile != null)
                await _audio.PlayAsync(schedule.AudioFile.FileName);
        }
    }
}

