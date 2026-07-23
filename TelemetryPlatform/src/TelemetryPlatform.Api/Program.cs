using Serilog;
using TelemetryPlatform.Api;
using TelemetryPlatform.Application.Interfaces;
using TelemetryPlatform.Application.Services;
using TelemetryPlatform.Domain.Interfaces;
using TelemetryPlatform.Infrastructure.BackgroundServices;
using TelemetryPlatform.Infrastructure.Channels;
using TelemetryPlatform.Infrastructure.SignalR;
using Npgsql;

// ── Bootstrap Serilog early so startup errors are captured ────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting TelemetryPlatform API...");

    var builder = WebApplication.CreateBuilder(args);

    // Explicitly load user secrets since environment might not be set to Development
    builder.Configuration.AddUserSecrets<Program>();

    // ── Serilog ────────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, config) =>
    {
        config
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "logs/telemetry-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);
    });

    // ── Configuration bindings ────────────────────────────────────────────────
    builder.Services.Configure<TelemetryChannelOptions>(
        builder.Configuration.GetSection(TelemetryChannelOptions.SectionName));

    builder.Services.Configure<WorkerOptions>(
        builder.Configuration.GetSection(WorkerOptions.SectionName));

    builder.Services.Configure<AnomalyDetectionOptions>(
        builder.Configuration.GetSection(AnomalyDetectionOptions.SectionName));

    // ── Domain / Application services ────────────────────────────────────────
    builder.Services.AddSingleton<TelemetryChannelSingleton>();
    builder.Services.AddSingleton<ITelemetryChannel>(sp =>
        sp.GetRequiredService<TelemetryChannelSingleton>());

    builder.Services.AddSingleton<IAnomalyDetector, AnomalyDetectionService>();
    builder.Services.AddSingleton<AnomalyDetectionService>();

    // ── In-process simulation (controlled via /api/simulation endpoints) ──────
    builder.Services.AddSingleton<TelemetryPlatform.Infrastructure.Simulation.SimulationService>();

    // IAlertBroadcaster is implemented by TelemetryHub (Hub-scoped) but we need
    // a reference from the singleton processing service.  Use IHubContext<TelemetryHub>
    // inside TelemetryProcessingService instead — registered here as a forwarding shim.
    builder.Services.AddSingleton<TelemetryProcessingService>();

    // ── Infrastructure: SignalR ────────────────────────────────────────────────
    builder.Services.AddSignalR(options =>
    {
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
        options.MaximumReceiveMessageSize = 32 * 1024; // 32 KB per message
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

    // TelemetryHub also implements IAlertBroadcaster via IHubContext injection.
    // We register a factory that resolves IHubContext<TelemetryHub> from DI.
    builder.Services.AddSingleton<IAlertBroadcaster, HubContextAlertBroadcaster>();

    // ── Infrastructure: Persistence (Phase 3) ───────────────────────────────
    string? pgConnString = builder.Configuration.GetConnectionString("TelemetryDb");
    if (string.IsNullOrEmpty(pgConnString))
    {
        throw new InvalidOperationException("Connection string 'TelemetryDb' not found. Ensure user-secrets or appsettings are configured.");
    }
    
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(pgConnString);
    builder.Services.AddSingleton(dataSourceBuilder.Build());
    builder.Services.AddSingleton<ITelemetryRepository, TelemetryPlatform.Infrastructure.Persistence.Postgres.PostgresTelemetryRepository>();
    
    // ── Background workers ────────────────────────────────────────────────────
    builder.Services.AddHostedService<TelemetryPlatform.Infrastructure.Persistence.Postgres.DatabaseInitializer>();
    builder.Services.AddSingleton<TelemetryPlatform.Infrastructure.Persistence.BulkCopyWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryPlatform.Infrastructure.Persistence.BulkCopyWriter>());
    builder.Services.AddHostedService<TelemetryProcessingWorker>();

    // ── MVC / Swagger ─────────────────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "TelemetryPlatform API", Version = "v1" });
    });

    // ── CORS ──────────────────────────────────────────────────────────────────
    // Dev: localhost:5173 / localhost:3000 always allowed.
    // Prod: AllowedOrigins:Frontend is injected via Railway environment variable.
    var allowedOrigins = new[]
    {
        "http://localhost:5173",
        "http://localhost:3000",
        builder.Configuration["AllowedOrigins:Frontend"] ?? "",
    }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("DevDashboard", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // Required for SignalR WebSockets
        });
    });

    // ── Build ─────────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("DevDashboard");

    // TODO(hardening): app.UseAuthentication(); app.UseAuthorization();

    app.MapControllers();
    app.MapHub<TelemetryHub>("/hubs/telemetry");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "TelemetryPlatform API crashed during startup.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
