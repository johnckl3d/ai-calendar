using CpmDemoApp.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.AspNetCore.Extensions; // Add this using directive
using Microsoft.ApplicationInsights.Extensibility;
using Azure;
using Azure.Search.Documents;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.Configure<NotificationMessagesClientOptions>(builder.Configuration.GetSection("NotificationMessagesClientOptions"));
builder.Services.Configure<OpenAIClientOptions>(builder.Configuration.GetSection("OpenAIClientOptions"));
builder.Services.Configure<TenantOptions>(builder.Configuration.GetSection("TenantOptions"));

builder.Services.AddControllersWithViews();

string applicationInsightsConnectionString = builder.Configuration["ApplicationInsightsOptions:InstrumentationKey"];

// Add Application Insights telemetry
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // Replace with your connection string from Azure Application Insights
    options.ConnectionString = applicationInsightsConnectionString;
});

var app = builder.Build();

// Disable Browser Link (prevents script injection attempts)
app.Use(async (context, next) =>
{
    context.Response.Headers["X-VisualStudioProxying"] = "false";
    await next();
});

// Track a startup trace to Application Insights
using (var scope = app.Services.CreateScope())
{
    var telemetryClient = scope.ServiceProvider.GetRequiredService<TelemetryClient>();
    telemetryClient.TrackTrace("Program.cs: Application starting up");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
//testing github push with private key
