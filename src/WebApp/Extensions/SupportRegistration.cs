using eShop.ServiceDefaults;
using eShop.WebApp.Services;
using Microsoft.Extensions.Http.Resilience;

#pragma warning disable EXTEXP0001 // Disable retries for the long-running agent POST.

public static class SupportRegistration
{
    public static void AddSupportServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient<SupportService>(http =>
        {
            http.BaseAddress = new Uri("https+http://support-api");
            http.Timeout = TimeSpan.FromSeconds(300);
        }).AddAuthToken().RemoveAllResilienceHandlers();
    }
}
