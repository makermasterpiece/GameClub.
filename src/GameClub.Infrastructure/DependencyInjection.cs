using GameClub.Infrastructure.Persistence;
using GameClub.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("GameClubDb")
            ?? throw new InvalidOperationException(
                "Connection string 'GameClubDb' is not configured. " +
                "Set ConnectionStrings__GameClubDb in the environment.");

        services.AddDbContext<GameClubDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IClubData, ClubData>();

        return services;
    }
}
