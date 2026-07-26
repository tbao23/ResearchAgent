using DocumentChatBot;
using DocumentChatBot.Web.Components;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Build the corpus index and connect to Foundry Local before serving requests
// ---------------------------------------------------------------------------
var engine = await CorpusChatEngine.CreateFromConfigurationAsync(builder.Configuration, CancellationToken.None);
if (engine is null)
    return; // CreateFromConfigurationAsync already logged the specific reason

builder.Services.AddSingleton(engine);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

// Corpus documents are private runtime data, not client assets, so they're served
// straight from disk here rather than through the wwwroot/static-web-assets pipeline.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(engine.CorpusDirectory),
    RequestPath = "/corpus",
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
