using AdBlocker.Service;

// Runs as a Windows service when started by the SCM, or as a console app when run by hand (diagnostics).
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AdBlocker");
builder.Services.AddSingleton<AdEngine>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
