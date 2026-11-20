using System.Text.Json.Serialization;
using JellyLite;
using JellyLite.Api;
using JellyLite.Auth;
using JellyLite.Import;
using JellyLite.Storage;
using JellyLite.Library;
using JellyLite.Media;

var builder = WebApplication.CreateBuilder(args);

var config = AppConfig.Load();
if (config.LoadedFrom == null && !builder.Environment.IsEnvironment("Testing"))
{
    config.WriteExample();
    Console.WriteLine($"No config found. Wrote an example to {config.LoadedFrom} - edit it and restart.");
}
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Prober>();
builder.Services.AddSingleton<SubtitleService>();
builder.Services.AddSingleton<RemoteImages>();
builder.Services.AddSingleton<ChapterImages>();
builder.Services.AddSingleton<Users>();
builder.Services.AddSingleton<QuickConnectStore>();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryService>());
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(JellyfinImport.HttpClientName);
builder.Services.AddSingleton<JellyfinImport>();
builder.Services.AddCors();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Jellyfin clients expect PascalCase
    o.SerializerOptions.PropertyNamingPolicy = null;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// The TV app runs from its own origin: allow cross-origin calls like Jellyfin does
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseMiddleware<AuthMiddleware>();

Admin.Map(app);
Endpoints.Map(app);

// Log calls we don't implement, so missing endpoints are easy to spot
app.MapFallback((HttpContext h, ILogger<Program> log) =>
{
    log.LogWarning("Not implemented: {Method} {Path}{Query}", h.Request.Method, h.Request.Path, h.Request.QueryString);
    return Results.NotFound();
});

app.Run();

public partial class Program;
