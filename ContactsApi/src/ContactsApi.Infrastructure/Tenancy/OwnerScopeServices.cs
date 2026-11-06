/**
 * @fileoverview Registration of the owner scope and its interceptor, and the worker
 * helpers that open one DI scope per Owner (AD-28, AD-49 rule 4)
 * @module ContactsApi.Infrastructure.Tenancy
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContactsApi.Infrastructure.Tenancy;

/// <summary>DI registration and per-Owner scopes</summary>
public static class OwnerScopeServices
{
    /// <summary>Registers <see cref="OwnerScope"/> and <see cref="OwnerRlsInterceptor"/> (both scoped) and the options</summary>
    public static IServiceCollection AddOwnerScoping(this IServiceCollection services, RowLevelSecurityOptions options)
    {
        services.AddSingleton(options);
        services.AddScoped<OwnerScope>();
        services.AddScoped<OwnerRlsInterceptor>();
        return services;
    }

    /// <summary>Adds the scope's interceptor to a DbContext (every DbContext, AD-49 rule 4)</summary>
    public static DbContextOptionsBuilder UseOwnerRls(this DbContextOptionsBuilder builder, IServiceProvider services) =>
        builder.AddInterceptors(services.GetRequiredService<OwnerRlsInterceptor>());

    /// <summary>
    /// A fresh DI scope whose Owner is <paramref name="owner"/>: one worker unit of work
    /// (AD-28: the Owner comes from the item, never from a previous item)
    /// </summary>
    public static IServiceScope CreateOwnerScope(this IServiceScopeFactory scopes, string owner)
    {
        var scope = scopes.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<OwnerScope>().Set(owner);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A fresh DI scope that declares unscoped work (the sys_* discovery functions, the
    /// readiness check); the declaration lasts as long as the scope
    /// </summary>
    public static IServiceScope CreateUnscopedScope(this IServiceScopeFactory scopes)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<OwnerScope>().AllowUnscoped();
        return scope;
    }
}
