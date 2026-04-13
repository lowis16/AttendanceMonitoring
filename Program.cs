using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Attendance Monitoring API",
        Version = "v1",
        Description = "In-memory backend for the student attendance system."
    });
});
builder.Services.AddSingleton<InMemoryStore>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Attendance API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/index.html"));

// ════════════════════════════════════════════════════════════
//  ADMIN ENDPOINTS
// ════════════════════════════════════════════════════════════

app.MapPost("/api/admin/register", (RegisterAdminRequest req, InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName))
        return Results.BadRequest(new { success = false, message = "Full name is required." });

    if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3)
        return Results.BadRequest(new { success = false, message = "Username must be at least 3 characters." });

    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
        return Results.BadRequest(new { success = false, message = "Password must be at least 6 characters." });

    if (store.Admins.ContainsKey(req.Username))
        return Results.BadRequest(new { success = false, message = "Username is already taken." });

    var admin = new Admin
    {
        FullName = req.FullName.Trim(),
        Username = req.Username.Trim(),
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
    };

    store.Admins[admin.Username] = admin;
    return Results.Ok(new { success = true, message = "Admin registered successfully." });
})
.WithName("RegisterAdmin")
.WithTags("Admin");

app.MapPost("/api/admin/login", (AdminLoginRequest req, InMemoryStore store) =>
{
    if (!store.Admins.TryGetValue(req.Username, out var admin))
        return Results.Unauthorized();

    if (!BCrypt.Net.BCrypt.Verify(req.Password, admin.PasswordHash))
        return Results.Unauthorized();

    return Results.Ok(new
    {
        success = true,
        message = "Login successful.",
        adminId = admin.Id,
        fullName = admin.FullName
    });
})
.WithName("AdminLogin")
.WithTags("Admin");

app.MapGet("/api/admin/list", (InMemoryStore store) =>
{
    var admins = store.Admins.Values.Select(a => new
    {
        a.Id,
        a.FullName,
        a.Username,
        a.RegisteredAt
    });
    return Results.Ok(admins);
})
.WithName("ListAdmins")
.WithTags("Admin");

// ════════════════════════════════════════════════════════════
//  PORTAL ENDPOINTS
// ════════════════════════════════════════════════════════════

app.MapPost("/api/attendance/portal/open", (string adminUsername, InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(adminUsername))
        return Results.BadRequest(new { success = false, message = "adminUsername is required." });

    if (store.Portal.IsOpen)
        return Results.Conflict(new { success = false, message = $"Portal already open (opened by {store.Portal.OpenedBy})." });

    store.Portal.IsOpen = true;
    store.Portal.OpenedBy = adminUsername;
    store.Portal.OpenedAt = DateTime.UtcNow;
    store.Portal.DateKey = DateTime.Now.ToString("yyyy-MM-dd");

    return Results.Ok(new { success = true, message = $"Portal opened for {store.Portal.DateKey}." });
})
.WithName("OpenPortal")
.WithTags("Portal");

app.MapPost("/api/attendance/portal/close", (InMemoryStore store) =>
{
    if (!store.Portal.IsOpen)
        return Results.Conflict(new { success = false, message = "Portal is already closed." });

    store.Portal.IsOpen = false;
    return Results.Ok(new { success = true, message = "Portal closed." });
})
.WithName("ClosePortal")
.WithTags("Portal");

app.MapGet("/api/attendance/portal/status", (InMemoryStore store) =>
    Results.Ok(store.Portal))
.WithName("PortalStatus")
.WithTags("Portal");

// ════════════════════════════════════════════════════════════
//  ATTENDANCE ENDPOINTS
// ════════════════════════════════════════════════════════════

app.MapPost("/api/attendance/mark", (StudentLoginRequest req, InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName))
        return Results.BadRequest(new { success = false, message = "Full name is required.", record = (object?)null });

    if (string.IsNullOrWhiteSpace(req.Section))
        return Results.BadRequest(new { success = false, message = "Section is required.", record = (object?)null });

    if (!store.Portal.IsOpen)
        return Results.BadRequest(new { success = false, message = "The attendance portal is not open. Ask your teacher to open it.", record = (object?)null });

    var today = store.Portal.DateKey!;
    var key = $"{today}_{req.FullName.Trim().ToLower()}";

    if (store.Attendance.TryGetValue(key, out var existing))
        return Results.Ok(new { success = true, message = "You are already marked present for today!", record = existing });

    var record = new AttendanceRecord
    {
        StudentName = req.FullName.Trim(),
        Section = req.Section.Trim()
    };
    store.Attendance[key] = record;

    return Results.Ok(new { success = true, message = "You are present for today's class!", record });
})
.WithName("MarkPresent")
.WithTags("Attendance");

app.MapGet("/api/attendance/today", (InMemoryStore store, string? date) =>
{
    var dateKey = date ?? DateTime.Now.ToString("yyyy-MM-dd");
    var records = store.Attendance.Values
        .Where(r => r.Date == dateKey)
        .OrderBy(r => r.MarkedAt)
        .ToList();

    return Results.Ok(new { date = dateKey, totalPresent = records.Count, records });
})
.WithName("TodayAttendance")
.WithTags("Attendance");

app.MapGet("/api/attendance/all", (InMemoryStore store) =>
{
    var grouped = store.Attendance.Values
        .GroupBy(r => r.Date)
        .OrderByDescending(g => g.Key)
        .Select(g => new { date = g.Key, totalPresent = g.Count(), records = g.OrderBy(r => r.MarkedAt) });

    return Results.Ok(grouped);
})
.WithName("AllAttendance")
.WithTags("Attendance");

app.Run();

// ════════════════════════════════════════════════════════════
//  MODELS
// ════════════════════════════════════════════════════════════

class InMemoryStore
{
    public ConcurrentDictionary<string, Admin> Admins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, AttendanceRecord> Attendance { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Portal Portal { get; } = new();
}

class Admin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

class AttendanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudentName { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;  // ← added
    public DateTime MarkedAt { get; set; } = DateTime.UtcNow;
    public string Date => MarkedAt.ToLocalTime().ToString("yyyy-MM-dd");
}

class Portal
{
    public bool IsOpen { get; set; } = false;
    public string OpenedBy { get; set; } = string.Empty;
    public DateTime? OpenedAt { get; set; }
    public string? DateKey { get; set; }
}

record RegisterAdminRequest(string FullName, string Username, string Password);
record AdminLoginRequest(string Username, string Password);
record StudentLoginRequest(string FullName, string Section);  // ← added Section