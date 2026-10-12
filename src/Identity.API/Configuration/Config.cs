namespace eShop.Identity.API.Configuration
{
    public class Config
    {
        // ApiResources define the apis in your system
        public static IEnumerable<ApiResource> GetApis()
{
    return new List<ApiResource>
    {
        new ApiResource("orders", "Orders Service")
        {
            Scopes = { "orders" }
        },
        new ApiResource("basket", "Basket Service")
        {
            Scopes = { "basket" }
        },
        new ApiResource("webhooks", "Webhooks registration Service")
        {
            Scopes = { "webhooks" }
        }
    };
}

        // ApiScope is used to protect the API 
        //The effect is the same as that of API resources in IdentityServer 3.x
        public static IEnumerable<ApiScope> GetApiScopes()
        {
            return new List<ApiScope>
            {
                new ApiScope("orders", "Orders Service"),
                new ApiScope("basket", "Basket Service"),
                new ApiScope("webhooks", "Webhooks registration Service"),
            };
        }

        // Identity resources are data like user ID, name, or email address of a user
        // see: http://docs.identityserver.io/en/release/configuration/resources.html
        public static IEnumerable<IdentityResource> GetResources()
        {
            return new List<IdentityResource>
            {
                new IdentityResources.OpenId(),
                new IdentityResources.Profile()
            };
        }

        // client want to access resources (aka scopes)
        public static IEnumerable<Client> GetClients(
            IConfiguration configuration, bool enableSupportEvaluation = false)
        {
            var clients = new List<Client>
            {
                new Client
                {
                    ClientId = "maui",
                    ClientName = "eShop MAUI OpenId Client",
                    AllowedGrantTypes = GrantTypes.Code,                    
                    //Used to retrieve the access token on the back channel.
                    ClientSecrets =
                    {
                        new Secret("secret".Sha256())
                    },
                    RedirectUris = { configuration["MauiCallback"] },
                    RequireConsent = false,
                    RequirePkce = true,
                    PostLogoutRedirectUris = { $"{configuration["MauiCallback"]}/Account/Redirecting" },
                    //AllowedCorsOrigins = { "http://eshopxamarin" },
                    AllowedScopes = new List<string>
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.OfflineAccess,
                        "orders",
                        "basket",
                        "mobileshoppingagg",
                        "webhooks"
                    },
                    //Allow requesting refresh tokens for long lived API access
                    AllowOfflineAccess = true,
                    AllowAccessTokensViaBrowser = true,
                    AlwaysIncludeUserClaimsInIdToken = true,
                    AccessTokenLifetime = 60*60*2, // 2 hours
                    IdentityTokenLifetime= 60*60*2 // 2 hours
                },
                new Client
                {
                    ClientId = "webapp",
                    ClientName = "WebApp Client",
                    ClientSecrets = new List<Secret>
                    {
                        new Secret("secret".Sha256())
                    },
                    ClientUri = $"{configuration["WebAppClient"]}",                             // public uri of the client
                    AllowedGrantTypes = GrantTypes.Code,
                    AllowAccessTokensViaBrowser = false,
                    RequireConsent = false,
                    AllowOfflineAccess = true,
                    AlwaysIncludeUserClaimsInIdToken = true,
                    RequirePkce = false,
                    RedirectUris = new List<string>
                    {
                        $"{configuration["WebAppClient"]}/signin-oidc"
                    },
                    PostLogoutRedirectUris = new List<string>
                    {
                        $"{configuration["WebAppClient"]}/signout-callback-oidc"
                    },
                    AllowedScopes = new List<string>
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.OfflineAccess,
                        "orders",
                        "basket",
                        "webshoppingagg",
                        "webhooks"
                    },
                    AccessTokenLifetime = 60*60*2, // 2 hours
                    IdentityTokenLifetime= 60*60*2 // 2 hours
                },
                new Client
                {
                    ClientId = "webhooksclient",
                    ClientName = "Webhooks Client",
                    ClientSecrets = new List<Secret>
                    {
                        new Secret("secret".Sha256())
                    },
                    ClientUri = $"{configuration["WebhooksWebClient"]}",                             // public uri of the client
                    AllowedGrantTypes = GrantTypes.Code,
                    AllowAccessTokensViaBrowser = false,
                    RequireConsent = false,
                    AllowOfflineAccess = true,
                    AlwaysIncludeUserClaimsInIdToken = true,
                    RedirectUris = new List<string>
                    {
                        $"{configuration["WebhooksWebClient"]}/signin-oidc"
                    },
                    PostLogoutRedirectUris = new List<string>
                    {
                        $"{configuration["WebhooksWebClient"]}/signout-callback-oidc"
                    },
                    AllowedScopes = new List<string>
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.OfflineAccess,
                        "webhooks"
                    },
                    AccessTokenLifetime = 60*60*2, // 2 hours
                    IdentityTokenLifetime= 60*60*2 // 2 hours
                },
                new Client
                {
                    ClientId = "basketswaggerui",
                    ClientName = "Basket Swagger UI",
                    AllowedGrantTypes = GrantTypes.Implicit,
                    AllowAccessTokensViaBrowser = true,

                    RedirectUris = { $"{configuration["BasketApiClient"]}/swagger/oauth2-redirect.html" },
                    PostLogoutRedirectUris = { $"{configuration["BasketApiClient"]}/swagger/" },

                    AllowedScopes =
                    {
                        "basket"
                    }
                },
                new Client
                {
                    ClientId = "orderingswaggerui",
                    ClientName = "Ordering Swagger UI",
                    AllowedGrantTypes = GrantTypes.Implicit,
                    AllowAccessTokensViaBrowser = true,

                    RedirectUris = { $"{configuration["OrderingApiClient"]}/swagger/oauth2-redirect.html" },
                    PostLogoutRedirectUris = { $"{configuration["OrderingApiClient"]}/swagger/" },

                    AllowedScopes =
                    {
                        "orders"
                    }
                },
                new Client
                {
                    ClientId = "webhooksswaggerui",
                    ClientName = "WebHooks Service Swagger UI",
                    AllowedGrantTypes = GrantTypes.Implicit,
                    AllowAccessTokensViaBrowser = true,

                    RedirectUris = { $"{configuration["WebhooksApiClient"]}/swagger/oauth2-redirect.html" },
                    PostLogoutRedirectUris = { $"{configuration["WebhooksApiClient"]}/swagger/" },

                    AllowedScopes =
                    {
                        "webhooks"
                    }
                }
            };

            // A public CLI client for the development evaluation runner.
            // The host enables it only when its environment is Development.
            if (enableSupportEvaluation)
            {
                clients.Add(new Client
                {
                    ClientId = "support-eval",
                    ClientName = "Support evaluation (development)",
                    AllowedGrantTypes = GrantTypes.Code,
                    RequireClientSecret = false,
                    RequirePkce = true,
                    AllowPlainTextPkce = false,
                    AllowAccessTokensViaBrowser = false,
                    RequireConsent = false,
                    RedirectUris = { "http://127.0.0.1:8765/callback" },
                    AllowedScopes = { "orders" },
                    AccessTokenLifetime = 3600
                });
            }

            return clients;
        }
    }
}
