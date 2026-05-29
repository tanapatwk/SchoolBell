using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Quartz;
using SchoolBell.Data;
using SchoolBell.Jobs;
using SchoolBell.Models;
using SchoolBell.Services;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=schoolbell.db"));

// Services
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<AudioFileService>();
builder.Services.AddSingleton<AudioService>();

// Session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(opt =>
{
    opt.IdleTimeout = TimeSpan.FromHours(8);
    opt.Cookie.Name = "SchoolBell.Session";
    opt.Cookie.HttpOnly = true;
    opt.Cookie.IsEssential = true;
    opt.Cookie.SameSite = SameSiteMode.Strict;
    opt.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Antiforgery
builder.Services.AddAntiforgery();

// Rate limiting
builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opt.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetClientKey(ctx),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    opt.AddPolicy("playback", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            GetClientKey(ctx),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(30),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// Quartz Scheduler
builder.Services.AddQuartz(q =>
{
    var jobKey = new JobKey("BellJob");
    q.AddJob<BellJob>(opts => opts.WithIdentity(jobKey));
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("BellTrigger")
        .WithCronSchedule("0 * * * * ?"));
});
builder.Services.AddQuartzHostedService(opt => opt.WaitForJobsToComplete = true);

var app = builder.Build();

// Auto migrate database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseSession();
app.UseRateLimiter();

var adminPassword = builder.Configuration["AdminPassword"];
if (string.IsNullOrWhiteSpace(adminPassword))
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("AdminPassword must be configured in production.");

    adminPassword = "admin1234";
}

if (!app.Environment.IsDevelopment() && adminPassword == "admin1234")
    throw new InvalidOperationException("Default AdminPassword is not allowed in production.");

// Helper ตรวจสอบว่า login แล้วหรือยัง
bool IsAdmin(HttpContext ctx) =>
    ctx.Session.GetString("role") == "admin";

// --- Auth API ---
app.MapPost("/api/login", (HttpContext ctx, LoginRequest req) =>
{
    if (req.Password == adminPassword)
    {
        ctx.Session.SetString("role", "admin");
        return Results.Ok(new { success = true });
    }
    return Results.Ok(new { success = false, message = "รหัสผ่านไม่ถูกต้อง" });
}).RequireRateLimiting("login");

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    ctx.Session.Clear();
    return Results.Ok();
});

app.MapGet("/api/me", (HttpContext ctx) =>
    Results.Ok(new { isAdmin = IsAdmin(ctx) }));

// --- Audio Files API ---
app.MapGet("/api/audiofiles", async (AudioFileService svc) =>
    Results.Ok(await svc.GetAllAsync()));

app.MapPost("/api/audiofiles", async (HttpContext ctx, IFormFile file, AudioFileService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    try
    {
        var result = await svc.SaveAsync(file);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).DisableAntiforgery();

app.MapGet("/api/audiofiles/{id}/usedby", async (int id, AudioFileService svc) =>
{
    var schedules = await svc.GetUsedBySchedulesAsync(id);
    return Results.Ok(schedules);
});

app.MapDelete("/api/audiofiles/{id}", async (HttpContext ctx, int id, AudioFileService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    try
    {
        return await svc.DeleteAsync(id) ? Results.Ok() : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

// --- Schedules API ---
app.MapGet("/api/schedules", async (ScheduleService svc) =>
    Results.Ok(await svc.GetAllAsync()));

app.MapGet("/api/schedules/{id}", async (int id, ScheduleService svc) =>
    await svc.GetByIdAsync(id) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapPost("/api/schedules", async (HttpContext ctx, Schedule schedule, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    try
    {
        return Results.Ok(await svc.CreateAsync(schedule));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapPut("/api/schedules/{id}", async (HttpContext ctx, int id, Schedule schedule, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    schedule.Id = id;
    try
    {
        return await svc.UpdateAsync(schedule) ? Results.Ok() : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

app.MapDelete("/api/schedules/{id}", async (HttpContext ctx, int id, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    return await svc.DeleteAsync(id) ? Results.Ok() : Results.NotFound();
});

// --- Play Now API (Guest ทำได้) ---
app.MapPost("/api/play/{audioFileId}", async (int audioFileId, AppDbContext db, AudioService audio) =>
{
    var file = await db.AudioFiles.FindAsync(audioFileId);
    if (file is null) return Results.NotFound();
    _ = audio.PlayAsync(file.FileName);
    return Results.Ok(new { message = $"Playing: {file.OriginalName}" });
}).RequireRateLimiting("playback");

// --- Status API ---
app.MapGet("/api/status", async (AudioService audio, AppDbContext db) =>
{
    var now = DateTime.Now;
    var currentFileName = audio.CurrentFileName;
    var playbackStartedAt = audio.PlaybackStartedAt;
    var currentAudio = currentFileName is null
        ? null
        : await db.AudioFiles.FirstOrDefaultAsync(a => a.FileName == currentFileName);
    var nextSchedule = await GetNextScheduleAsync(db, now);

    return Results.Ok(new
    {
        serverTime = now,
        isPlaying = audio.IsPlaying,
        currentFileName,
        currentAudioName = currentAudio?.OriginalName,
        playbackStartedAt,
        playbackElapsedSeconds = playbackStartedAt.HasValue
            ? Math.Max(0, (int)(now - playbackStartedAt.Value).TotalSeconds)
            : 0,
        nextBell = nextSchedule is null ? null : new
        {
            name = nextSchedule.Schedule.Name,
            time = nextSchedule.At,
            audioName = nextSchedule.Schedule.AudioFile?.OriginalName
        }
    });
});

// --- Stop API (Guest ทำได้) ---
app.MapPost("/api/stop", (AudioService audio) =>
{
    audio.Stop();
    return Results.Ok(new { message = "Stopped" });
}).RequireRateLimiting("playback");

static string GetClientKey(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress?.ToString()
    ?? ctx.Session.Id
    ?? "unknown";

static async Task<NextSchedule?> GetNextScheduleAsync(AppDbContext db, DateTime now)
{
    var schedules = await db.Schedules
        .Include(s => s.AudioFile)
        .Where(s => s.IsEnabled)
        .ToListAsync();

    return schedules
        .Select(schedule => new NextSchedule(schedule, GetNextRunAt(schedule, now)))
        .Where(next => next.At.HasValue)
        .Select(next => new NextSchedule(next.Schedule, next.At!.Value))
        .OrderBy(next => next.At)
        .FirstOrDefault();
}

static DateTime? GetNextRunAt(Schedule schedule, DateTime now)
{
    for (var offset = 0; offset < 7; offset++)
    {
        var day = now.Date.AddDays(offset);
        if (!RunsOnDay(schedule, day.DayOfWeek)) continue;

        var runAt = day.Add(schedule.Time.ToTimeSpan());
        if (runAt > now) return runAt;
    }

    return null;
}

static bool RunsOnDay(Schedule schedule, DayOfWeek day) =>
    day switch
    {
        DayOfWeek.Monday => schedule.Monday,
        DayOfWeek.Tuesday => schedule.Tuesday,
        DayOfWeek.Wednesday => schedule.Wednesday,
        DayOfWeek.Thursday => schedule.Thursday,
        DayOfWeek.Friday => schedule.Friday,
        DayOfWeek.Saturday => schedule.Saturday,
        DayOfWeek.Sunday => schedule.Sunday,
        _ => false
    };

app.Run();

record LoginRequest(string Password);
record NextSchedule(Schedule Schedule, DateTime? At);
