using Microsoft.AspNetCore.HttpLogging;

var builder = WebApplication.CreateBuilder(args);

// Enables proper Windows Service lifecycle (start/stop signals from SCM)
builder.Host.UseWindowsService();

builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod
                    | HttpLoggingFields.RequestPath
                    | HttpLoggingFields.ResponseStatusCode
                    | HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHostedService<remote_operations.WebSocketClientService>();

// Listen on all network interfaces so the API is reachable from the local network
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

app.MapOpenApi();

app.UseHttpLogging();
app.UseAuthorization();
app.MapControllers();

app.Run();
