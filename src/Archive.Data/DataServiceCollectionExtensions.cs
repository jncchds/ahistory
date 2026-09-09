using Archive.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Archive.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the save: options, the connection/migration owner, and the EF context factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options are validated in PostConfigure, so a missing or unusable DatabasePath throws while
    /// the host is starting rather than surfacing later as an empty window.
    /// </para>
    /// <para>
    /// A context <em>factory</em>, not a scoped context: a desktop app has no request scope, view
    /// models are long-lived, and two of them touching one DbContext concurrently is a crash
    /// waiting for a busy afternoon.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddArchiveData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ArchiveOptions>(configuration.GetSection(ArchiveOptions.SectionName));
        services.PostConfigure<ArchiveOptions>(o => o.Validate());

        services.AddSingleton(sp => new Database(sp.GetRequiredService<IOptions<ArchiveOptions>>().Value));
        services.AddSingleton<PragmaConnectionInterceptor>();

        services.AddDbContextFactory<ArchiveDbContext>((sp, builder) =>
        {
            builder
                .UseSqlite(sp.GetRequiredService<Database>().ConnectionString)
                .AddInterceptors(sp.GetRequiredService<PragmaConnectionInterceptor>())
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        return services;
    }
}
