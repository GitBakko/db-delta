using DbDelta.App.State;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;

namespace DbDelta.App;

/// <summary>
/// WinForms host for the Blazor Hybrid UI. The form is a thin shell — all
/// rendering happens inside <see cref="BlazorWebView"/> via <see cref="App"/>.
/// </summary>
public partial class MainForm : Form
{
    public MainForm()
    {
        Text = "DbDelta";
        Width = 1280;
        Height = 800;

        ServiceCollection services = new();
        services.AddWindowsFormsBlazorWebView();
        services.AddSingleton<AppState>();

        BlazorWebView blazor = new()
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = services.BuildServiceProvider(),
        };
        blazor.RootComponents.Add<App>("#app");
        Controls.Add(blazor);
    }
}
