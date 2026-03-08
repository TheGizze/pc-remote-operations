var builder = WebApplication.CreateBuilder(args);

// Enables proper Windows Service lifecycle (start/stop signals from SCM)
builder.Host.UseWindowsService();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Listen on all network interfaces so the API is reachable from the local network
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

app.MapOpenApi();

app.UseAuthorization();
app.MapControllers();

app.Run();
