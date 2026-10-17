using Microsoft.Extensions.DependencyInjection;

namespace FactoryStarter.Application;

public static class DependencyInjection
{
    // Register use-case handlers here.
    public static IServiceCollection AddApplication(this IServiceCollection services) => services;
}
