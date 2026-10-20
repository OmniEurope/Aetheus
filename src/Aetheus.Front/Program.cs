// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
FrontHostSetup.ConfigureServices(builder.Services, builder.Configuration, builder.HostEnvironment, builder.Logging);

var host = builder.Build();
await FrontHostSetup.ApplyStoredCultureAsync(host.Services.GetRequiredService<IJSRuntime>());

// Paint-yield: hand control back to the browser so the boot splash animation paints one frame
// before the (framework-internal) route scan + first render monopolise the main thread.
await Task.Delay(1);
await host.RunAsync();
