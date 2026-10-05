using Cluster.Models.Options;
using Cluster.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Cluster.Interface;

namespace Cluster;

public static class Program
{
    public static async Task Main(string[] args)
    {
        const string applicationVersion = "kvm-cluster@v1.0.0-rc1";
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        IServiceCollection services = builder.Services;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(endpointOptions => endpointOptions.Protocols = HttpProtocols.Http2);
        });
        builder.WebHost.UseSentry(options =>
        {
            options.Release = applicationVersion;
            options.Dsn = "https://99d741d8dda945e9ed3f0a4d226576ad@sentry.silveridc.cn/19";
            options.TracesSampleRate = 1D;
            options.SendDefaultPii = true;
            //options.Debug = true;
            options.AutoSessionTracking = true;
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
        services
            .AddOptions<NetworkOptions>()
            .BindConfiguration(NetworkOptions.SectionName);
        // Singleton
        services
            .AddSingleton<IVirshClient, VirshClient>()
            .AddSingleton<IHostNetworkClient, HostNetworkClient>()
            .AddSingleton<IVirtualMachineLockManager, VirtualMachineLockManager>()
            .AddSingleton<IVirtualMachineProvisioner, VirtualMachineProvisioner>()
            .AddSingleton<IVirtualMachineConfigurationManager, VirtualMachineConfigurationManager>()
            .AddSingleton<IVncConsoleService, VncConsoleService>()
            .AddSingleton<INodeMetricsCollector, NodeMetricsCollector>()
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
