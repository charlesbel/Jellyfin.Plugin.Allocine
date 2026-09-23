using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineControllerSecurityTests
{
    [Fact]
    public void RatingsEndpointRequiresAuthentication()
    {
        MethodInfo method = typeof(AllocineController).GetMethod(nameof(AllocineController.GetRatings))!;

        Assert.NotNull(method.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void RatingsEndpointAcceptsOnlyCanonicalizableJellyfinIdentity()
    {
        MethodInfo method = typeof(AllocineController).GetMethod(nameof(AllocineController.GetRatings))!;
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("itemId", parameters[0].Name);
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [Fact]
    public void RatingsEndpointAcceptsRequestCancellation()
    {
        MethodInfo method = typeof(AllocineController).GetMethod(nameof(AllocineController.GetRatings))!;

        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void CurrentUserComesOnlyFromTheJellyfinUserIdClaim()
    {
        Guid userId = Guid.NewGuid();
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("Jellyfin-UserId", userId.ToString("N")) },
            "Jellyfin"));
        var apiKeyWithoutUser = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Administrator") },
            "Jellyfin"));

        Assert.Equal(userId, AllocineController.GetCurrentUserId(authenticated));
        Assert.Null(AllocineController.GetCurrentUserId(apiKeyWithoutUser));
    }
}
