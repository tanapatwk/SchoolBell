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
    opt.Cookie.HttpOnly = true;
    opt.Cookie.IsEssential = true;
});

// Antiforgery
builder.Services.AddAntiforgery();

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

var adminPassword = builder.Configuration["AdminPassword"] ?? "admin1234";

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
});

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
    return await svc.DeleteAsync(id) ? Results.Ok() : Results.NotFound();
});

// --- Schedules API ---
app.MapGet("/api/schedules", async (ScheduleService svc) =>
    Results.Ok(await svc.GetAllAsync()));

app.MapGet("/api/schedules/{id}", async (int id, ScheduleService svc) =>
    await svc.GetByIdAsync(id) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapPost("/api/schedules", async (HttpContext ctx, Schedule schedule, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    return Results.Ok(await svc.CreateAsync(schedule));
});

app.MapPut("/api/schedules/{id}", async (HttpContext ctx, int id, Schedule schedule, ScheduleService svc) =>
{
    if (!IsAdmin(ctx)) return Results.Forbid();
    schedule.Id = id;
    return await svc.UpdateAsync(schedule) ? Results.Ok() : Results.NotFound();
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
});

// --- Stop API (Guest ทำได้) ---
app.MapPost("/api/stop", (AudioService audio) =>
{
    audio.Stop();
    return Results.Ok(new { message = "Stopped" });
});

app.Run();

record LoginRequest(string Password);
