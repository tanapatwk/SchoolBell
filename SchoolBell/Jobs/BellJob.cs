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
        var now = DateTime.Now;
        var nowTime = TimeOnly.FromDateTime(now);
        var today = now.DayOfWeek;

        var schedules = await _db.Schedules
            .Include(s => s.AudioFile)
            .Where(s => s.IsEnabled)
            .ToListAsync();

        foreach (var schedule in schedules)
        {
            // เช็คว่าเวลาตรงกันไหม (ภายใน 60 วินาที)
            var diff = Math.Abs((nowTime - schedule.Time).TotalSeconds);
            if (diff > 60) continue;

            // เช็คว่าวันตรงกันไหม
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

            // เช็คว่า trigger ไปแล้วในชั่วโมง:นาที:วันนี้หรือยัง
            if (schedule.LastTriggeredAt.HasValue)
            {
                var last = schedule.LastTriggeredAt.Value;
                if (last.Date == now.Date
                    && last.Hour == now.Hour
                    && last.Minute == now.Minute)
                {
                    _logger.LogDebug("Skipping {Name}: already triggered at {Last}", 
                        schedule.Name, last);
                    continue;
                }
            }

            _logger.LogInformation("Triggering bell: {Name} at {Time}", 
                schedule.Name, schedule.Time);

            // บันทึกเวลา trigger ก่อนเล่น เพื่อป้องกัน trigger ซ้ำ
            schedule.LastTriggeredAt = now;
            await _db.SaveChangesAsync();

            if (schedule.AudioFile != null)
                _ = _audio.PlayAsync(schedule.AudioFile.FileName);
        }
    }
}
