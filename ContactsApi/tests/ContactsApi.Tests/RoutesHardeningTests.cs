using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContactsApi.Tests;

/// <summary>The review fixes of R1 in the service, against PostgreSQL (ATLAS_TEST_DB)</summary>
[Collection("routes-db")]
public class RoutesHardeningDbTests
{
    private static RoutesDbContext Db(IServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();

    private static async Task AssessAsync(IServiceProvider sp, string owner)
    {
        using var scope = sp.CreateScope();
        while ((await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default)).More)
        {
        }
    }

    private static async Task<MatchBatchResult> MatchAsync(IServiceProvider sp, string owner)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, 25, () => false, default);
    }

    [Fact]
    public async Task A_match_is_stored_with_its_list_and_density_lines_and_old_rows_are_filled()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "lines-" + Guid.NewGuid();
        backend.Add(owner, 2, points: 60);
        await AssessAsync(sp, owner);
        Assert.Equal(2, (await MatchAsync(sp, owner)).Tally.Matched);

        var db = Db(sp);
        var rows = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        Assert.All(rows, r => Assert.Equal(RouteLines.Derive(r.Polyline!), (r.ListPolyline!, r.DensityPolyline!)));

        // Rows stored before the columns existed are filled by the service, a page at a time
        await db.MatchedRoutes.Where(m => m.OwnerId == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ListPolyline, (string?)null).SetProperty(m => m.DensityPolyline, (string?)null));
        using (var scope = sp.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MatchRunner>();
            while (await runner.FillLinesAsync(1, default) > 0)
            {
            }
        }

        var filled = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        Assert.All(filled, r => Assert.Equal(RouteLines.Derive(r.Polyline!), (r.ListPolyline!, r.DensityPolyline!)));
    }
}
