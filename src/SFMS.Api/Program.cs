using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using SFMS.Api.Auth;
using SFMS.Api.Realtime;
using SFMS.Collector;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Infrastructure.Excel;
using SFMS.Infrastructure.Realtime;
using SFMS.Shared;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Sfms")
    ?? "Host=127.0.0.1;Port=5433;Database=sfms;Username=postgres;Password=postgres";

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton(sp =>
{
    var opt = new JwtOptions();
    builder.Configuration.GetSection("Jwt").Bind(opt);
    return opt;
});

builder.Services.AddDbContextFactory<SfmsDbContext>(o => o.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure(2)));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<WsTicketService>();
builder.Services.AddScoped<AuditService>();
// SettingsService 只依赖 DbContextFactory，注册为单例供采集运行时（单例）读取配置版本号
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddScoped<PointImportService>();
builder.Services.AddScoped<DbBootstrapper>();
builder.Services.AddSingleton<HistoryRepository>();

builder.Services.AddSingleton<IRealtimeStore, InMemoryRealtimeStore>();
builder.Services.AddSingleton<AlarmEngine>();
builder.Services.AddSingleton<HistoryWriter>();
builder.Services.AddSingleton<CollectorRuntime>();
builder.Services.AddScoped<WriteService>();
builder.Services.AddHostedService<CollectorService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = new JwtOptions();
        builder.Configuration.GetSection("Jwt").Bind(jwt);
        options.TokenValidationParameters = new JwtTokenService(jwt).ValidationParameters;
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                string? accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/ws"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("TagWrite", p => p.Requirements.Add(new RoleLevelRequirement(RoleLevel.Operator)));
    options.AddPolicy("ConfigEdit", p => p.Requirements.Add(new RoleLevelRequirement(RoleLevel.Engineer)));
    options.AddPolicy("UserAdmin", p => p.Requirements.Add(new RoleLevelRequirement(RoleLevel.Admin)));
});
builder.Services.AddSingleton<IAuthorizationHandler, RoleLevelHandler>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SFMS API", Version = "v1", Description = "厂区设备检测系统接口" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "输入登录接口返回的 token"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// ---------------- 启动引导：迁移 + 时序表 + 种子数据
using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<DbBootstrapper>();
    await bootstrapper.InitializeAsync();
}

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SFMS API v1"));

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(ApiResult<object>.Fail(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(ApiResult<object>.Fail(ex.Message));
    }
    catch (ArgumentException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(ApiResult<object>.Fail(ex.Message));
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Map("/ws/realtime", RealtimeWebSocketHandler.HandleAsync);

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));
app.MapGet("/health/ready", async (IDbContextFactory<SfmsDbContext> factory, CollectorRuntime runtime) =>
{
    bool dbOk;
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        dbOk = await db.Database.CanConnectAsync();
    }
    catch (Exception)
    {
        dbOk = false;
    }
    var payload = new
    {
        ready = dbOk,
        database = dbOk ? "up" : "down",
        collector = runtime.Started ? "running" : "starting",
        sessions = runtime.SessionCount,
        tags = runtime.TagCount
    };
    return dbOk ? Results.Ok(payload) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapFallbackToFile("index.html");

app.Run();
