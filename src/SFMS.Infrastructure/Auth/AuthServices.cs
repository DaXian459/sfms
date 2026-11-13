using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SFMS.Shared;

namespace SFMS.Infrastructure.Auth;

public sealed class JwtOptions
{
    public string Key { get; set; } = "sfms-dev-key-please-change-in-production-0123456789";
    public string Issuer { get; set; } = "sfms";
    public string Audience { get; set; } = "sfms-web";
    public int ExpireMinutes { get; set; } = 60;
    public int WsTicketSeconds { get; set; } = 60;
}

/// <summary>PBKDF2-SHA256 口令散列（迭代 12 万次，格式：iter.salt.hash）。</summary>
public static class PasswordHasher
{
    private const int Iterations = 120_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = (stored ?? "").Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int iter)) return false;
        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt, string Jti);

public sealed class JwtTokenService(JwtOptions options)
{
    public IssuedToken Create(int userId, string username, string displayName, RoleLevel role)
    {
        var jti = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(options.ExpireMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new("displayName", displayName),
            new(ClaimTypes.Role, role.ToString()),
            new("roleLevel", ((int)role).ToString()),
            new(JwtRegisteredClaimNames.Jti, jti)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims,
            notBefore: DateTime.UtcNow.AddSeconds(-5), expires: expires.UtcDateTime, signingCredentials: creds);
        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires, jti);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.Name
    };
}

/// <summary>WebSocket 不能带 Authorization 头，用一次性短票据换连接。</summary>
public sealed class WsTicketService
{
    private readonly Dictionary<string, (int UserId, DateTimeOffset Expires)> _tickets = new();
    private readonly object _lock = new();

    public string Issue(int userId, int seconds = 60)
    {
        string ticket = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            Prune();
            _tickets[ticket] = (userId, DateTimeOffset.UtcNow.AddSeconds(seconds));
        }
        return ticket;
    }

    public int? Consume(string ticket)
    {
        lock (_lock)
        {
            if (!_tickets.TryGetValue(ticket, out var v) || v.Expires < DateTimeOffset.UtcNow) return null;
            _tickets.Remove(ticket);
            return v.UserId;
        }
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _tickets.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList())
            _tickets.Remove(key);
    }
}

public static class ClaimsExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out int id) ? id : 0;

    public static string Username(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

    // 方法名与枚举同名，这里必须全限定，否则报 CS0119
    public static RoleLevel RoleLevel(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("roleLevel"), out int level)
            ? (SFMS.Shared.RoleLevel)level
            : SFMS.Shared.RoleLevel.Observer;
}
