using Cluster.Models.Options;
using Cluster.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Grpc.AspNetCore.Server;

namespace Cluster;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        IServiceCollection services = builder.Services;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(endpointOptions => endpointOptions.Protocols = HttpProtocols.Http2);
        });

        services
            .AddOptions<LibvirtOptions>()
            .BindConfiguration(LibvirtOptions.SectionName)
            .ValidateDataAnnotations();
        services
            .AddOptions<RpcOptions>()
            .BindConfiguration(RpcOptions.SectionName)
            .ValidateOnStart();
        services
            .AddOptions<ProvisioningOptions>()
            .BindConfiguration(ProvisioningOptions.SectionName);
        services
            .AddOptions<VncOptions>()
            .BindConfiguration(VncOptions.SectionName);
        // Singleton
        services
            .AddSingleton<IVirshClient, VirshClient>()
            .AddSingleton<IHostNetworkClient, HostNetworkClient>()
            .AddSingleton<IVirtualMachineProvisioner, VirtualMachineProvisioner>()
            .AddSingleton<IVncConsoleService, VncConsoleService>()
            .AddHostedService<VncConsoleCleanupService>()
            .AddSingleton<ClusterAuthenticationInterceptor>()
            .AddSingleton<ClusterExceptionInterceptor>()
            .AddGrpc(options =>
            {
                options.Interceptors.Add<ClusterAuthenticationInterceptor>();
                options.Interceptors.Add<ClusterExceptionInterceptor>();
            });
        services.AddOpenApi();

        WebApplication app = builder.Build();
        app.MapOpenApi("/doc/get");
        app.MapGroup("/rpc").MapGrpcService<ClusterAgentService>();
        app.MapGet("/rpc", () => Results.Ok(new { service = "KvmControl Cluster", protocol = "gRPC" }));

        await app.RunAsync();
    }
}
