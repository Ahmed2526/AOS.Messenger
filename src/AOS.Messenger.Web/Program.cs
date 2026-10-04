using AOS.Messenger.Application;
using AOS.Messenger.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------------------------------
// Service registration
// --------------------------------------------------------------------------
builder.Services.AddControllersWithViews();

builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// Authentication and SignalR will be wired here in future phases.

// --------------------------------------------------------------------------
// HTTP pipeline
// --------------------------------------------------------------------------
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
