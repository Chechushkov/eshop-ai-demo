using eShop.Basket.API.Grpc;
using eShop.WebApp.Services.OrderStatus.IntegrationEvents;
using eShop.WebAppComponents.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.JsonWebTokens;

public static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        builder.AddAuthenticationServices();

        builder.AddRabbitMqEventBus("EventBus")
               .AddEventBusSubscriptions();

        builder.Services.AddHttpForwarderWithServiceDiscovery();

        // Application services
        builder.Services.AddScoped<BasketState>();
        builder.Services.AddScoped<LogOutService>();
        builder.Services.AddSingleton<BasketService>();
        builder.Services.AddSingleton<OrderStatusNotificationService>();
        builder.Services.AddSingleton<IProductImageUrlProvider, ProductImageUrlProvider>();
        builder.AddAIServices();
        builder.AddSupportServices();

        // HTTP and GRPC client registrations
        builder.Services.AddGrpcClient<Basket.BasketClient>(o => o.Address = new("http://basket-api"))
            .AddAuthToken();

        builder.Services.AddHttpClient<CatalogService>(o => o.BaseAddress = new("https+http://catalog-api"))
            .AddApiVersion(2.0)
            .AddAuthToken();

        builder.Services.AddHttpClient<OrderingService>(o => o.BaseAddress = new("https+http://ordering-api"))
            .AddApiVersion(1.0)
            .AddAuthToken();
    }

    public static void AddEventBusSubscriptions(this IEventBusBuilder eventBus)
    {
        eventBus.AddSubscription<OrderStatusChangedToAwaitingValidationIntegrationEvent, OrderStatusChangedToAwaitingValidationIntegrationEventHandler>();
        eventBus.AddSubscription<OrderStatusChangedToPaidIntegrationEvent, OrderStatusChangedToPaidIntegrationEventHandler>();
        eventBus.AddSubscription<OrderStatusChangedToStockConfirmedIntegrationEvent, OrderStatusChangedToStockConfirmedIntegrationEventHandler>();
        eventBus.AddSubscription<OrderStatusChangedToShippedIntegrationEvent, OrderStatusChangedToShippedIntegrationEventHandler>();
        eventBus.AddSubscription<OrderStatusChangedToCancelledIntegrationEvent, OrderStatusChangedToCancelledIntegrationEventHandler>();
        eventBus.AddSubscription<OrderStatusChangedToSubmittedIntegrationEvent, OrderStatusChangedToSubmittedIntegrationEventHandler>();
    }

    public static void AddAuthenticationServices(this IHostApplicationBuilder builder)
{
    var configuration = builder.Configuration;
    var services = builder.Services;

    JsonWebTokenHandler.DefaultInboundClaimTypeMap.Remove("sub");

    var identityUrl = configuration.GetRequiredValue("IdentityUrl");
    var publicOrigin = configuration.GetRequiredValue("CallBackUrl").TrimEnd('/');
    var sessionCookieLifetime =
        configuration.GetValue("SessionCookieLifetimeMinutes", 60);

    services.AddAuthorization();

    services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionCookieLifetime))
    .AddOpenIdConnect(options =>
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Authority = identityUrl;

        options.ClientId = "webapp";
        options.ClientSecret = "secret";
        options.ResponseType = "code";
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.SignedOutRedirectUri = publicOrigin;

        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.RequireHttpsMetadata = false;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri =
                $"{publicOrigin}/signin-oidc";

            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("eShop.OIDC");

            logger.LogInformation(
                "OIDC redirect_uri={RedirectUri}; PAR={Par}",
                context.ProtocolMessage.RedirectUri,
                context.Options.PushedAuthorizationBehavior);

            return Task.CompletedTask;
        };

        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.PostLogoutRedirectUri =
                $"{publicOrigin}/signout-callback-oidc";

            return Task.CompletedTask;
        };

        if (builder.Environment.IsDevelopment()
            && Uri.TryCreate(publicOrigin, UriKind.Absolute, out var callback)
            && callback.IsLoopback
            && callback.Scheme == Uri.UriSchemeHttp)
        {
            options.ResponseMode = "query";

            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy =
                CookieSecurePolicy.SameAsRequest;

            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy =
                CookieSecurePolicy.SameAsRequest;
        }

        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("orders");
        options.Scope.Add("basket");
    });

    services.AddScoped<AuthenticationStateProvider,
        ServerAuthenticationStateProvider>();
    services.AddCascadingAuthenticationState();
}

    private static void AddAIServices(this IHostApplicationBuilder builder)
    {
        ChatClientBuilder? chatClientBuilder = null;
        if (builder.Configuration["OllamaEnabled"] is string ollamaEnabled && bool.Parse(ollamaEnabled))
        {
            chatClientBuilder = builder.AddOllamaApiClient("chat")
                .AddChatClient();
        }
        else if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("chatModel")))
        {
            chatClientBuilder = builder.AddOpenAIClientFromConfiguration("chatModel")
                .AddChatClient();
        }

        chatClientBuilder?.UseFunctionInvocation();
    }

    public static async Task<string?> GetBuyerIdAsync(this AuthenticationStateProvider authenticationStateProvider)
    {
        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        return user.FindFirst("sub")?.Value;
    }

    public static async Task<string?> GetUserNameAsync(this AuthenticationStateProvider authenticationStateProvider)
    {
        var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        return user.FindFirst("name")?.Value;
    }
}
