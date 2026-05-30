using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Quartz;
using SchoolBell.Data;
using SchoolBell.Jobs;
using SchoolBell.Models;
using SchoolBell.Services;

var builder = WebApplication.CreateBuilder(args);
const string AppVersion = "0.1.0-beta";

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
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS AppSettings (
            Key TEXT NOT NULL CONSTRAINT PK_AppSettings PRIMARY KEY,
            Value TEXT NOT NULL
        )
        """);
}

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (!string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            return;

        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers.Pragma = "no-cache";
        ctx.Context.Response.Headers.Expires = "0";
    }
});
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

// --- App Settings API ---
app.MapGet("/api/settings", async (AppDbContext db) =>
    Results.Ok(new
    {
        appName = await GetSettingAsync(db, "AppName", "School Bell"),
        logoUrl = await GetSettingAsync(db, "LogoUrl", ""),
        version = AppVersion
    }));

app.MapPost("/api/settings/branding", async (HttpContext ctx, AppDbContext db, IWebHostEnvironment env) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();

    var form = await ctx.Request.ReadFormAsync();
    var appName = form["appName"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(appName))
        return Results.BadRequest("กรุณาใส่ชื่อระบบ");
    if (appName.Length > 80)
        return Results.BadRequest("ชื่อระบบต้องไม่เกิน 80 ตัวอักษร");

    await SetSettingAsync(db, "AppName", appName);

    try
    {
        var logo = form.Files.GetFile("logo");
        if (logo is { Length: > 0 })
        {
            var logoUrl = await SaveLogoAsync(logo, env);
            await SetSettingAsync(db, "LogoUrl", logoUrl);
        }
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        appName,
        logoUrl = await GetSettingAsync(db, "LogoUrl", ""),
        version = AppVersion
    });
}).DisableAntiforgery();

app.MapPost("/api/reset", async (HttpContext ctx, AppDbContext db, IWebHostEnvironment env, AudioService audio) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();

    try
    {
        audio.Stop();

        var audioFiles = await db.AudioFiles.ToListAsync();
        var uploadPath = Path.Combine(env.WebRootPath, "uploads");

        db.Schedules.RemoveRange(db.Schedules);
        await db.SaveChangesAsync();

        db.AudioFiles.RemoveRange(audioFiles);
        await db.SaveChangesAsync();

        foreach (var audioFile in audioFiles)
        {
            var filePath = Path.Combine(uploadPath, audioFile.FileName);
            if (File.Exists(filePath)) File.Delete(filePath);
        }

        return Results.Ok(new { message = "Reset completed" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Reset failed: {ex.Message}");
    }
}).DisableAntiforgery();

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

app.MapPost("/api/audiofiles/{id}/delete", async (HttpContext ctx, int id, AudioFileService svc) =>
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
}).DisableAntiforgery();

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

app.MapPost("/api/schedules/{id}/delete", async (HttpContext ctx, int id, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    return await svc.DeleteAsync(id) ? Results.Ok() : Results.NotFound();
}).DisableAntiforgery();

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
    var lastPlaybackErrorFileName = audio.LastPlaybackErrorFileName;
    var currentAudio = currentFileName is null
        ? null
        : await db.AudioFiles.FirstOrDefaultAsync(a => a.FileName == currentFileName);
    var lastPlaybackErrorAudio = lastPlaybackErrorFileName is null
        ? null
        : await db.AudioFiles.FirstOrDefaultAsync(a => a.FileName == lastPlaybackErrorFileName);
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
        lastPlaybackError = audio.LastPlaybackError,
        lastPlaybackErrorAt = audio.LastPlaybackErrorAt,
        lastPlaybackErrorFileName,
        lastPlaybackErrorAudioName = lastPlaybackErrorAudio?.OriginalName,
        nextBell = nextSchedule is null ? null : new
        {
            name = nextSchedule.Schedule.Name,
            time = nextSchedule.At,
            timeText = nextSchedule.Schedule.Time.ToString("HH:mm"),
            dateText = GetThaiDateText(nextSchedule.At!.Value),
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

static string GetThaiDateText(DateTime date)
{
    var dayNames = new[] { "อา.", "จ.", "อ.", "พ.", "พฤ.", "ศ.", "ส." };
    var monthNames = new[]
    {
        "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.",
        "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค."
    };

    return $"{dayNames[(int)date.DayOfWeek]} {date.Day:00} {monthNames[date.Month - 1]} {date.Year + 543}";
}

static async Task<string> GetSettingAsync(AppDbContext db, string key, string defaultValue)
{
    var setting = await db.AppSettings.FindAsync(key);
    return setting?.Value ?? defaultValue;
}

static async Task SetSettingAsync(AppDbContext db, string key, string value)
{
    var setting = await db.AppSettings.FindAsync(key);
    if (setting is null)
    {
        db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        return;
    }

    setting.Value = value;
}

static async Task<string> SaveLogoAsync(IFormFile logo, IWebHostEnvironment env)
{
    const long maxLogoSizeBytes = 2 * 1024 * 1024;
    if (logo.Length > maxLogoSizeBytes)
        throw new InvalidOperationException("โลโก้ต้องมีขนาดไม่เกิน 2 MB");

    var ext = Path.GetExtension(logo.FileName).ToLower();
    var allowedExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp" };
    if (!allowedExtensions.Contains(ext))
        throw new InvalidOperationException("รองรับโลโก้เฉพาะ PNG, JPG หรือ WebP");

    var uploadPath = Path.Combine(env.WebRootPath, "uploads");
    Directory.CreateDirectory(uploadPath);

    foreach (var oldLogo in Directory.GetFiles(uploadPath, "branding-logo.*"))
        File.Delete(oldLogo);

    var fileName = $"branding-logo{ext}";
    var filePath = Path.Combine(uploadPath, fileName);
    await using var stream = File.Create(filePath);
    await logo.CopyToAsync(stream);

    return $"/uploads/{fileName}?v={DateTimeOffset.Now.ToUnixTimeSeconds()}";
}

app.Run();

record LoginRequest(string Password);
record NextSchedule(Schedule Schedule, DateTime? At);
