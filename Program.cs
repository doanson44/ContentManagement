using ContentManagement.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<ContentManagement.Components.App>().AddInteractiveServerRenderMode();
app.Run();
