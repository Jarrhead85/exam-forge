using ExamForge.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Same-origin: cookies (ef.student) уходят автоматически; antiforgery-заголовок добавляет ExamApiClient.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ExamApiClient>();

await builder.Build().RunAsync();
