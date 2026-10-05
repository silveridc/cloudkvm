using Control.Areas.Admin;
using Control.Interface;
using Control.Services;
using Microsoft.OpenApi;
using StackExchange.Redis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Control;

public static class Program
{
    public static async Task Main(string[] args)
    {
        const string applicationVersion = "kvm-control@v1.0.0-rc1";
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        IServiceCollection services = builder.Services;

        services.AddCors();

        services
            .AddOptions<ClustersOptions>()
            .BindConfiguration(ClustersOptions.SectionName);
        services
            .AddOptions<ManagementApiOptions>()
            .BindConfiguration(ManagementApiOptions.SectionName);
        services
            .AddOptions<CacheOptions>()
            .BindConfiguration(CacheOptions.SectionName);
        services
            .AddOptions<OperationOptions>()
            .BindConfiguration(OperationOptions.SectionName);
        services
            .AddOptions<ClusterMetricsOptions>()
            .BindConfiguration(ClusterMetricsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.WebHost.UseSentry(options =>
        {
            options.Release = applicationVersion;
            options.Dsn = "https://99d741d8dda945e9ed3f0a4d226576ad@sentry.silveridc.cn/19";
            options.TracesSampleRate = 1D;
            options.SendDefaultPii = true;
            //options.Debug = true;
            options.AutoSessionTracking = true;
        });

        string? redisConnectionString = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            ConfigurationOptions redisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConfiguration));
            services.AddStackExchangeRedisCache(options =>
            {
                options.ConfigurationOptions = redisConfiguration;
                options.InstanceName = applicationVersion;
            });
        }
        else
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("ConnectionStrings:Redis is required outside the Development environment.");
            }
            services.AddDistributedMemoryCache();
        }
        // Singleton
        services
            .AddSingleton<ControlInstance>()
            .AddSingleton<IClusterClientFactory, ClusterClientFactory>()
            .AddSingleton<VncConsoleTicketStore>()
            .AddSingleton<VncConsoleProxy>()
            .AddSingleton<OperationCache>()
            .AddSingleton<OperationQueue>()
            .AddSingleton<MetricsStore>();

        services
            .AddHostedService<VncConsoleTicketCleanupService>()
            .AddHostedService<ControlInstanceHeartbeatService>()
            .AddHostedService<MetricsPollingService>()
            .AddHostedService<OperationWorker>();

        // Register Swagger services
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Api v1",
                Version = "v1"
            });
        });

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

        WebApplication app = builder.Build();

        string[] supportedCultures = ["zh-CN", "en-US"];
        app.UseRequestLocalization(new RequestLocalizationOptions()
            .SetDefaultCulture("zh-CN")
            .AddSupportedCultures(supportedCultures)
            .AddSupportedUICultures(supportedCultures));
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Api v1");
        });
        app.UseCors();
        app.UseHttpsRedirection();
        app.UseWebSockets();
        app.UseStaticFiles();

        // Custom business middleware runs after Swagger
        app.UseMiddleware<ApiResponseMiddleware>();
        app.UseMiddleware<ManagementApiAuthenticationMiddleware>();

        app.MapControllers();
        app.Map("/api/v1/consoles", async (HttpContext context, VncConsoleProxy proxy) => await proxy.ProxyAsync(context));

        await app.RunAsync();
    }
}
