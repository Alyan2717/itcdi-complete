using ITCDI.Application.Services;
using ITCDI.Core.Interfaces;
using ITCDI.Infrastructure.Data;
using ITCDI.Infrastructure.Http;
using ITCDI.Infrastructure.Jobs;
using ITCDI.Infrastructure.Services;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

// ── Read connection strings ───────────────────────────────────────────────────
var registryCs = builder.Configuration.GetConnectionString("ItcdiRegistry") ?? throw new InvalidOperationException(
        "Connection string 'ItcdiRegistry' is missing from appsettings.json");

//var targetCs = builder.Configuration.GetConnectionString("TargetDatabase") ?? throw new InvalidOperationException(
//        "Connection string 'TargetDatabase' is missing from appsettings.json");
//var targetCs = builder.Configuration.GetConnectionString("TargetWikidata") ?? throw new InvalidOperationException(
//        "Connection string 'TargetWikidata' is missing from appsettings.json");
//var targetCs = builder.Configuration.GetConnectionString("TargetTPCDI") ?? throw new InvalidOperationException(
//        "Connection string 'TargetTPCDI' is missing from appsettings.json");
//var targetCs = builder.Configuration.GetConnectionString("TargetMagellan") ?? throw new InvalidOperationException(
//        "Connection string 'TargetMagellan' is missing from appsettings.json");
var targetCs = builder.Configuration.GetConnectionString("TargetTPCH") ?? throw new InvalidOperationException(
        "Connection string 'TargetTPCH' is missing from appsettings.json");

var pythonUrl = builder.Configuration["Python:SchemaIntelligenceUrl"] ?? "http://localhost:5001";

// ── Infrastructure services ───────────────────────────────────────────────────

builder.Services.AddScoped<IDriftDetectionService>(sp =>
                    new DriftDetectionService(registryCs, sp.GetRequiredService<ILogger<DriftDetectionService>>()));

builder.Services.AddScoped<IMappingRegistry>(sp =>
                    new MappingRegistryService(registryCs, sp.GetRequiredService<ILogger<MappingRegistryService>>()));

builder.Services.AddScoped<ITransformationEngine, TransformationEngine>();

builder.Services.AddScoped<IConflictDetectionService>(sp =>
                    new ConflictDetectionService(registryCs, targetCs, sp.GetRequiredService<ILogger<ConflictDetectionService>>()));

builder.Services.AddScoped<ITargetDbWriter>(sp =>
                    new TargetDbWriter(targetCs,registryCs, sp.GetRequiredService<ILogger<TargetDbWriter>>()));

builder.Services.AddScoped<IIntegrationRunRepository>(sp =>
                    new IntegrationRunRepository(registryCs, sp.GetRequiredService<ILogger<IntegrationRunRepository>>()));

builder.Services.AddScoped<ITargetSchemaReader>(sp =>
                    new TargetSchemaReader(targetCs, builder.Configuration, sp.GetRequiredService<ILogger<TargetSchemaReader>>()));

builder.Services.AddTransient<IntegrationJob>();
// ── Python HTTP client ────────────────────────────────────────────────────────
builder.Services.AddHttpClient<ISchemaMatchingClient, SchemaMatchingClient>(
    client =>
    {
        client.BaseAddress = new Uri(pythonUrl);
        client.Timeout = TimeSpan.FromSeconds(180);
    });

// ── Application layer ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IntegrationOrchestrator>();

// ── Quartz scheduler ──────────────────────────────────────────────────────────
builder.Services.AddQuartz(q =>
{
    var jobKey = new JobKey("IntegrationJob");
    q.AddJob<IntegrationJob>(opts => opts.WithIdentity(jobKey));
    q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("IntegrationJob-trigger")
                .WithCronSchedule(builder.Configuration["Quartz:Cron"] ?? "0 0 2 * * ?"));
});
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// ── Web API ───────────────────────────────────────────────────────────────────
builder.Services.AddControllers().AddJsonOptions(opts =>
    {
        // Allow enums as strings in JSON — "LastWriteWins" instead of 0
        opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new()
    {
        Title = "ITCDI Integration API",
        Version = "v1",
        Description = "Incremental Target-Constrained Data Integration System"
    })
);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 500_000_000);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ITCDI v1"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
