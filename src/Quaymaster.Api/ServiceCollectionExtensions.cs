using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.HttpOverrides;
using Quaymaster.Api.Http;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Security;
using Quaymaster.Application.Validation;

namespace Quaymaster.Api;

public static class ServiceCollectionExtensions
{
    /// <summary>Request-pipeline services: validators, origin policy, cookie helper, trusted-proxy handling.</summary>
    public static IServiceCollection AddQuaymasterApi(this IServiceCollection services, QmConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(ServiceLifetime.Singleton);
        services.AddSingleton(AllowedOrigins.From(config.PublicOrigins, config.Host, config.Port));
        services.AddSingleton<SessionCookie>();

        if (config.TrustProxy.Count > 0)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var entry in config.TrustProxy)
                {
                    var slash = entry.IndexOf('/', StringComparison.Ordinal);
                    if (slash < 0)
                    {
                        options.KnownProxies.Add(IPAddress.Parse(entry));
                    }
                    else
                    {
                        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
                    }
                }

                // Walk every hop from the right while it is a trusted proxy; the first untrusted one is the client.
                options.ForwardLimit = null;
            });
        }

        return services;
    }
}
