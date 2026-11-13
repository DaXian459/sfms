using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SFMS.Infrastructure.Auth;
using SFMS.Shared;

namespace SFMS.Api.Auth;

public sealed record RoleLevelRequirement(RoleLevel Level) : IAuthorizationRequirement;

/// <summary>层级角色判定：Admin ≥ Engineer ≥ Operator ≥ Observer，避免每个接口写四个角色。</summary>
public sealed class RoleLevelHandler : AuthorizationHandler<RoleLevelRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleLevelRequirement requirement)
    {
        var role = context.User.FindFirstValue(ClaimTypes.Role);
        var level = context.User.RoleLevel();
        if (level >= requirement.Level)
            context.Succeed(requirement);
        else if (!string.IsNullOrEmpty(role))
            context.Fail(new AuthorizationFailureReason(this,
                $"需要 {requirement.Level} 及以上权限，当前角色 {role}"));
        return Task.CompletedTask;
    }
}
