using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Control.Areas.Admin;
using Control.Model.Response;
using Control.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;

namespace Control;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        IServiceCollection services = builder.Services;

        services
            .AddOptions<ClustersOptions>()
            .BindConfiguration(ClustersOptions.SectionName);
        services
            .AddOptions<ManagementApiOptions>()
            .BindConfiguration(ManagementApiOptions.SectionName);
        services
            .AddOptions<CacheOptions>()
            .BindConfiguration(CacheOptions.SectionName);

        string? redisConnection = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            ConfigurationOptions redisOptions = ConfigurationOptions.Parse(redisConnection);
            redisOptions.AbortOnConnectFail = false;
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
            services.AddStackExchangeRedisCache(options =>
            {
                options.ConfigurationOptions = redisOptions;
            });
        }
        else
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("A Redis connection is required outside Development.");
            }
            services.AddDistributedMemoryCache();
        }

        services
            .AddSingleton<ControlInstance>()
            .AddSingleton<IClusterClientFactory, ClusterClientFactory>()
            .AddSingleton<VncConsoleTicketStore>()
            .AddSingleton<VncConsoleProxy>()
            .AddSingleton<OperationCache>()
            .AddSingleton<OperationQueue>();
        services
            .AddHostedService<VncConsoleTicketCleanupService>()
            .AddHostedService<ControlInstanceHeartbeatService>()
            .AddHostedService<OperationWorker>();
        services.AddControllers(options =>
            {
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            })
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    Dictionary<string, string[]> errors = context.ModelState
                        .Where(entry => entry.Value is { Errors.Count: > 0 })
                        .ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value!.Errors.Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? Resources.Localization.API.invalid_value : error.ErrorMessage).ToArray());
                    return Response.Fail(Resources.Localization.API.request_error, new ValidationErrorResponse(errors), ResponseCode.RequestError, StatusCodes.Status400BadRequest);
                };
            })
            .AddJsonOptions(options =>
            {
                JsonSerializerOptions jsonOptions = options.JsonSerializerOptions;
                jsonOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                jsonOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                jsonOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                jsonOptions.PropertyNameCaseInsensitive = true;
            });
        services.AddOpenApi();

        WebApplication app = builder.Build();
        string[] supportedCultures = ["zh-CN", "en-US"];
        app.UseRequestLocalization(new RequestLocalizationOptions()
            .SetDefaultCulture("zh-CN")
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures));
        app.UseMiddleware<ApiResponseMiddleware>();
        app.MapOpenApi("/doc/get");
        app.UseHttpsRedirection();
        app.UseWebSockets();
        app.UseStaticFiles();
        app.UseMiddleware<ManagementApiAuthenticationMiddleware>();
        app.MapControllers();
        app.Map("/api/v1/consoles/{ticket}", async (HttpContext context, string ticket, VncConsoleProxy proxy) => await proxy.ProxyAsync(context, ticket));

        await app.RunAsync();
    }
}
