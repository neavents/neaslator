using Microsoft.EntityFrameworkCore;
using Neaslator.Infrastructure.Messaging;

namespace Neaslator.Persistence;

public static class NeaslatorDbContextRegistration
{
    public static IServiceCollection AddNeaslatorDbContext(this IServiceCollection services, string? connectionString) =>
        services.AddDbContext<NeaslatorDbContext>(options => options
            .UseNpgsql(connectionString)
            .AddInterceptors(new InboxLockReadInterceptor()));
}
