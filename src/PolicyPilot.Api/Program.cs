using System.Diagnostics;
using System.Text.Json;
using PolicyPilot.Core;
using PolicyPilot.Core.Llm;
using PolicyPilot.Core.Rag;
using PolicyPilot.Core.Security;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var provider = cfg["Llm:Provider"] ?? "mock";
var opts = new ProviderOptions
{
    BaseUrl = cfg["Llm:BaseUrl"] ?? "", ApiKey = cfg["Llm:ApiKey"] ?? "",
    ChatModel = cfg["Llm:ChatModel"] ?? "", EmbeddingModel = cfg["Llm:EmbeddingModel"] ?? "",
};
var docs = cfg["Data:DocsFolder"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data", "docs");
var app0 = await PolicyPilotApp.CreateAsync(provider, opts, docs);

var secret = cfg["Jwt:Secret"] ?? "dev-only-secret-change-me-0123456789abcdef";
var jwt = new JwtService(secret);
var users = new UserStore();
users.Add("customer", cfg["Users:CustomerPassword"] ?? "customer123", "customer");
users.Add("officer", cfg["Users:OfficerPassword"] ?? "officer123", "officer");
users.Add("admin", cfg["Users:AdminPassword"] ?? "admin123", "admin");
var limiter = new RateLimiter(int.Parse(cfg["RateLimit:PerMinute"] ?? "30"), TimeSpan.FromMinutes(1));

builder.Services.AddSingleton(app0);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:4200"]).AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseCors();

UserContext Who(HttpContext c)
{
    var h = c.Request.Headers.Authorization.ToString();
    var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : c.Request.Query["access_token"].ToString();
    return jwt.Validate(token) ?? UserContext.Anonymous;   // anonymous = lowest privilege ("customer")
}
UserContext? Strict(HttpContext c)
{
    var h = c.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? jwt.Validate(h[7..]) : null;
}
string ClientKey(HttpContext c, UserContext u) => u.UserId == "anonymous" ? c.Connection.RemoteIpAddress?.ToString() ?? "ip" : u.UserId;

// Request-scoped correlation id + access log
app.Use(async (ctx, next) =>
{
    var id = ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N")[..12];
    ctx.Response.Headers["X-Correlation-Id"] = id;
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    var sw = Stopwatch.StartNew();
    await next();
    app.Logger.LogInformation("{Method} {Path} -> {Status} in {Ms}ms cid={Cid}", ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds, id);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", provider = app0.Llm.Name, documents = app0.Kb.Documents.Count, chunks = app0.Kb.ChunkCount }));

app.MapPost("/api/auth/login", (LoginRequest r) =>
{
    var u = users.Authenticate(r.Username ?? "", r.Password ?? "");
    return u is null ? Results.Unauthorized() : Results.Ok(new { token = jwt.Issue(u), role = u.Role, username = u.Username });
});

app.MapPost("/api/chat", async (HttpContext ctx, ChatRequest r, CancellationToken ct) =>
{
    var user = Who(ctx);
    if (!limiter.Allow(ClientKey(ctx, user))) return Results.StatusCode(429);
    if (string.IsNullOrWhiteSpace(r.Question) || r.Question.Length > 1000) return Results.BadRequest(new { error = "question required (max 1000 chars)" });
    var sw = Stopwatch.StartNew();
    var ans = await app0.Rag.AnswerAsync(r.Question, user, r.History, ct);
    app0.Telemetry.RecordRequest(sw.Elapsed.TotalMilliseconds, ans.Answered);
    if (ans.Flags.Any(f => f.Contains("injection"))) app0.Telemetry.RecordGuardrail();
    return Results.Ok(ans);
});

// Server-sent events: first a "meta" event with citations, then token events, then "done".
app.MapPost("/api/chat/stream", async (HttpContext ctx, ChatRequest r, CancellationToken ct) =>
{
    var user = Who(ctx);
    if (!limiter.Allow(ClientKey(ctx, user))) { ctx.Response.StatusCode = 429; return; }
    if (string.IsNullOrWhiteSpace(r.Question) || r.Question.Length > 1000) { ctx.Response.StatusCode = 400; return; }
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    async Task Send(string ev, object data) { await ctx.Response.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))}\n\n", ct); await ctx.Response.Body.FlushAsync(ct); }

    var sw = Stopwatch.StartNew();
    var ans = await app0.Rag.AnswerAsync(r.Question, user, r.History, ct);
    app0.Telemetry.RecordRequest(sw.Elapsed.TotalMilliseconds, ans.Answered);
    await Send("meta", new { citations = ans.Citations, confidence = ans.Confidence, answered = ans.Answered, flags = ans.Flags, timings = ans.Timings });
    foreach (var piece in System.Text.RegularExpressions.Regex.Split(ans.Answer, @"(?<=\s)").Where(p => p.Length > 0))
    {
        await Send("token", new { text = piece });
        await Task.Delay(12, ct);
    }
    await Send("done", new { });
});

app.MapPost("/api/agent", async (HttpContext ctx, AgentRequest r, CancellationToken ct) =>
{
    var user = Who(ctx);
    if (!limiter.Allow(ClientKey(ctx, user))) return Results.StatusCode(429);
    if (string.IsNullOrWhiteSpace(r.Question) || r.Question.Length > 1000) return Results.BadRequest(new { error = "question required" });
    return Results.Ok(await app0.Agent.RunAsync(r.Question, user, ct));
});

app.MapGet("/api/documents", (HttpContext ctx) =>
{
    var user = Who(ctx);
    return Results.Ok(app0.Kb.Documents.Where(d => d.AllowedRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase))
        .Select(d => new { d.Id, d.Title, d.Category, d.AllowedRoles }));
});

app.MapPost("/api/documents", async (HttpContext ctx, CancellationToken ct) =>
{
    var user = Strict(ctx);
    if (user is null) return Results.Unauthorized();
    if (user.Role != "admin") return Results.StatusCode(403);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "multipart form required" });
    var form = await ctx.Request.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0 || file.Length > 2_000_000) return Results.BadRequest(new { error = "file required (max 2MB)" });
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".md" or ".txt" or ".docx")) return Results.BadRequest(new { error = "only .md, .txt, .docx" });

    string text;
    await using (var s = file.OpenReadStream())
    {
        if (ext == ".docx") { using var ms = new MemoryStream(); await s.CopyToAsync(ms, ct); ms.Position = 0; text = KnowledgeBase.ExtractDocx(ms); }
        else { using var sr = new StreamReader(s); text = await sr.ReadToEndAsync(ct); }
    }
    var id = Path.GetFileNameWithoutExtension(file.FileName).ToLowerInvariant().Replace(' ', '-');
    var (meta, body) = KnowledgeBase.ParseFrontMatter(id, text, file.FileName);
    var roles = form["roles"].ToString();
    if (!string.IsNullOrWhiteSpace(roles)) meta = meta with { AllowedRoles = roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) };
    var chunks = await app0.Kb.IngestAsync(meta, body, ct);
    return Results.Ok(new { meta.Id, meta.Title, chunks, roles = meta.AllowedRoles });
});

app.MapDelete("/api/documents/{id}", (HttpContext ctx, string id) =>
{
    var user = Strict(ctx);
    if (user is null) return Results.Unauthorized();
    if (user.Role != "admin") return Results.StatusCode(403);
    return app0.Kb.Remove(id) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/metrics", (HttpContext ctx) =>
{
    var user = Strict(ctx);
    return user is null ? Results.Unauthorized() : user.Role == "customer" ? Results.StatusCode(403) : Results.Ok(app0.Telemetry.Snapshot());
});

app.Run();

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChatRequest(string? Question, List<ChatTurn>? History);
public sealed record AgentRequest(string? Question);
public partial class Program;
