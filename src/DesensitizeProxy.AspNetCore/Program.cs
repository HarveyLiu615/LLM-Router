using DesensitizeProxy.AspNetCore.Extensions;
using DesensitizeProxy.AspNetCore.Health;
using DesensitizeProxy.AspNetCore.Middleware;
using DesensitizeProxy.Core.Models;
using DesensitizeProxy.Core.Runtime;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Services.AddDesensitizeProxy(builder.Configuration);

var app = builder.Build();
var config = app.Services.GetRequiredService<IOptionsMonitor<PrivacyConfig>>().CurrentValue;
var directories = app.Services.GetRequiredService<RuntimeDirectoryResolver>();
Directory.CreateDirectory(directories.DataDirectory);
Directory.CreateDirectory(directories.LogDirectory);

app.MapPrivacyProxyHealth();
app.UseMiddleware<DesensitizeProxyMiddleware>();

app.Urls.Clear();
app.Urls.Add($"http://{config.Proxy.BindAddress}:{config.Proxy.Port}");

app.Run();
