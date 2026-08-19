using System.Globalization;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.WebUI.Components;
using CommunicationDebuggingTools.WebUI.Services;

var builder = WebApplication.CreateBuilder(args);

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IAppLogger>(_ => new MemoryAppLogger(500));
builder.Services.AddSingleton<WebUiSettingsStore>();
builder.Services.AddScoped<EngineGateway>();

var app = builder.Build();

if (!app.Environment.IsDevelopment()) {
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
