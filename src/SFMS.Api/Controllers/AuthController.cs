using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SFMS.Domain;
using SFMS.Infrastructure;
using SFMS.Infrastructure.Auth;
using SFMS.Infrastructure.Data;
using SFMS.Shared;

namespace SFMS.Api.Controllers;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResult(
    string Token, DateTimeOffset ExpiresAt, string WsTicket, int UserId, string Username,
    string DisplayName, string Role, int RoleLevel);

public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

public sealed record UserInput(string Username, string Password, string DisplayName, string Role, bool Enabled);

public sealed record UserDto(int Id, string Username, string DisplayName, string Role, bool Enabled,
    int FailedCount, DateTimeOffset? LockedUntil, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

/// <summary>登录 / 令牌 / 用户管理（计划书 3.8 身份与权限）。</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IDbContextFactory<SfmsDbContext> factory,
    JwtTokenService jwt,
    WsTicketService tickets,
    AuditService audit,
    ILogger<AuthController> logger) : ControllerBase
{
    private const int MaxFailed = 5;
    private static readonly TimeSpan LockWindow = TimeSpan.FromMinutes(5);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        string ip = HttpContext.Ip();
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
            return BadRequest(ApiResult<object>.Fail("请输入用户名与口令"));

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username, ct);

        // 统一错误文案，避免暴露账号是否存在
        const string badCredential = "用户名或口令错误";

        if (user is null)
        {
            await audit.WriteAsync("auth.login", "user", req.Username, null, null, "Failed", badCredential,
                username: req.Username, ip: ip);
            return Unauthorized(ApiResult<object>.Fail(badCredential));
        }

        if (!user.Enabled)
            return Unauthorized(ApiResult<object>.Fail("账号已停用，请联系管理员"));

        if (user.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            int seconds = (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds);
            return StatusCode(StatusCodes.Status423Locked,
                ApiResult<object>.Fail($"连续登录失败次数过多，请 {seconds} 秒后重试"));
        }

        if (!PasswordHasher.Verify(req.Password, user.PasswordHash))
        {
            user.FailedCount++;
            bool justLocked = false;
            if (user.FailedCount >= MaxFailed)
            {
                user.LockedUntil = DateTimeOffset.UtcNow.Add(LockWindow);
                user.FailedCount = 0;
                justLocked = true;
                logger.LogWarning("账号 {User} 连续失败 {N} 次，锁定至 {Until}", user.Username, MaxFailed, user.LockedUntil);
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("auth.login", "user", user.Username, null, null, "Failed", badCredential,
                userId: user.Id, username: user.Username, ip: ip);

            // 触发锁定的这一次就返回 423，避免第 5 次仍回 401、第 6 次才提示锁定
            if (justLocked)
                return StatusCode(StatusCodes.Status423Locked,
                    ApiResult<object>.Fail($"连续登录失败 {MaxFailed} 次，账号已锁定 {(int)LockWindow.TotalMinutes} 分钟"));

            return Unauthorized(ApiResult<object>.Fail(badCredential));
        }

        var role = ParseRole(user.RoleCode);
        user.FailedCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var issued = jwt.Create(user.Id, user.Username, user.DisplayName, role);
        string ticket = tickets.Issue(user.Id, 60);
        await audit.WriteAsync("auth.login", "user", user.Username, null, new { role = role.ToString() },
            userId: user.Id, username: user.Username, ip: ip);

        return Ok(ApiResult<LoginResult>.Success(new LoginResult(issued.Token, issued.ExpiresAt, ticket,
            user.Id, user.Username, user.DisplayName, role.ToString(), (int)role)));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<ApiResult<object>>> Me(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == User.UserId(), ct);
        if (user is null) return NotFound(ApiResult<object>.Fail("用户不存在"));
        return Ok(ApiResult<object>.Success(new
        {
            user.Id,
            user.Username,
            user.DisplayName,
            role = ParseRole(user.RoleCode).ToString(),
            roleLevel = (int)ParseRole(user.RoleCode),
            user.LastLoginAt
        }));
    }

    /// <summary>WebSocket 一次性票据：ws 握手不能自定义请求头，改用短票据鉴权。</summary>
    [HttpPost("ws-ticket")]
    [Authorize]
    public ActionResult<ApiResult<object>> WsTicket()
    {
        string ticket = tickets.Issue(User.UserId(), 60);
        return Ok(ApiResult<object>.Success(new { ticket, expiresIn = 60 }));
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<ApiResult<object>>> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(ApiResult<object>.Fail("新口令至少 6 位"));

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == User.UserId(), ct);
        if (user is null) return NotFound();
        if (!PasswordHasher.Verify(req.OldPassword, user.PasswordHash))
            return BadRequest(ApiResult<object>.Fail("原口令错误"));

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("auth.change-password", "user", user.Id.ToString(), null, null,
            userId: user.Id, username: user.Username, ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok = true }));
    }

    // ---------------- 用户管理（仅管理员）
    [HttpGet("users")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<UserDto>>>> Users(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(ct);
        return Ok(ApiResult<IReadOnlyList<UserDto>>.Success(list.Select(u => new UserDto(u.Id, u.Username,
            u.DisplayName, ParseRole(u.RoleCode).ToString(), u.Enabled, u.FailedCount, u.LockedUntil,
            u.CreatedAt, u.LastLoginAt)).ToList()));
    }

    [HttpPost("users")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<ApiResult<object>>> CreateUser([FromBody] UserInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Username)) return BadRequest(ApiResult<object>.Fail("用户名不能为空"));
        var password = input.Password ?? "";
        if (password.Length < 6) return BadRequest(ApiResult<object>.Fail("口令至少 6 位"));

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(u => u.Username == input.Username, ct))
            return BadRequest(ApiResult<object>.Fail("用户名已存在"));

        var user = new AppUser
        {
            Username = input.Username.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? input.Username : input.DisplayName,
            RoleCode = ParseRole(input.Role).ToString(),
            Enabled = input.Enabled
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("user.create", "user", user.Id.ToString(), null,
            new { user.Username, user.DisplayName, user.RoleCode }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { user.Id }));
    }

    [HttpPut("users/{id:int}")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<ApiResult<object>>> UpdateUser(int id, [FromBody] UserInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        var before = new { user.Username, user.DisplayName, user.RoleCode, user.Enabled };
        user.DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? user.Username : input.DisplayName;
        user.RoleCode = ParseRole(input.Role).ToString();
        user.Enabled = input.Enabled;
        if (!string.IsNullOrWhiteSpace(input.Password))
        {
            if (input.Password.Length < 6) return BadRequest(ApiResult<object>.Fail("口令至少 6 位"));
            user.PasswordHash = PasswordHasher.Hash(input.Password);
            user.LockedUntil = null;
            user.FailedCount = 0;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("user.update", "user", id.ToString(), before,
            new { user.DisplayName, user.RoleCode, user.Enabled }, userId: User.UserId(),
            username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { user.Id }));
    }

    [HttpDelete("users/{id:int}")]
    [Authorize(Policy = "UserAdmin")]
    public async Task<ActionResult<ApiResult<object>>> DeleteUser(int id, CancellationToken ct)
    {
        if (id == User.UserId()) return BadRequest(ApiResult<object>.Fail("不能删除当前登录账号"));
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        if (user.Username == "admin") return BadRequest(ApiResult<object>.Fail("内置管理员不可删除"));

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("user.delete", "user", id.ToString(), new { user.Username }, null,
            userId: User.UserId(), username: User.Username(), ip: HttpContext.Ip());
        return Ok(ApiResult<object>.Success(new { ok = true }));
    }

    internal static RoleLevel ParseRole(string? roleCode) =>
        Enum.TryParse<RoleLevel>(roleCode, true, out var r) ? r : RoleLevel.Observer;
}
