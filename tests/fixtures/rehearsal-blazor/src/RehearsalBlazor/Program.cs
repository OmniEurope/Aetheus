// SPDX-License-Identifier: EUPL-1.2
using RehearsalBlazor;
using RehearsalBlazor.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapGet("/version", () => Results.Text(VersionInfo.Current));
app.MapRazorComponents<App>();
app.Run();
