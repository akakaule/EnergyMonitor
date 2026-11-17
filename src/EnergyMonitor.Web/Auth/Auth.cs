using System.Security.Claims;
using System.Text.Encodings.Web;
using EnergyMonitor.Core.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Auth;

public sealed class AuthOptions
{
    public const string ModeOidc = "Oidc";
    public const string ModeDevelopment = "Development";

    /// <summary>
    /// No sign-in: every request acts as the administrator. Only for a private installation that nobody else
    /// can reach, such as a machine published to a Tailscale tailnet on its loopback interface.
    /// </summary>
    public const string ModeNone = "None";

    /// <summary>
    /// <c>Oidc</c> (default), <c>None</c> or <c>Development</c>. Development is refused outside the Development environment.
    /// </summary>
    public string Mode { get; set; } = ModeOidc;

    /// <summary>OIDC authority, e.g. https://accounts.google.com or https://login.microsoftonline.com/{tenant}/v2.0.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>The household's users. Anyone not listed is refused, whatever the identity provider says.</summary>
    public List<AllowedUser> Users { get; set; } = [];

    /// <summary>Development mode only: the user every request is signed in as, unless a request names another.</summary>
    public string? DevUser { get; set; }
}

public sealed class AllowedUser
{
    /// <summary>
    /// The user's object id (<c>oid</c>), <c>sub</c>, or an e-mail address the provider reports as verified.
    /// Not the sign-in name (<c>preferred_username</c>): providers let it change, and where users can sign
    /// themselves up they can choose it, so it could match someone else's entry.
    /// </summary>
    public string Subject { get; set; } = "";

    public string Role { get; set; } = UserRoles.Viewer;
}

/// <summary>
/// Resolves a signed-in principal to a role through the allowlist. This runs on the server for every
/// request; nothing in the cookie or token can grant a role by itself.
/// </summary>
public sealed class Allowlist(IOptionsMonitor<AuthOptions> options)
{
    // Identifiers the provider assigns and never reuses. preferred_username is deliberately not one of them.
    private static readonly string[] SubjectClaims = ["oid", "sub"];

    public User? Resolve(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (string.Equals(options.CurrentValue.Mode, AuthOptions.ModeNone, StringComparison.OrdinalIgnoreCase))
        {
            return new User(LocalAuthHandler.Subject, LocalAuthHandler.DisplayName, UserRoles.Admin);
        }

        var subjects = principal.Claims
            .Where(c => SubjectClaims.Contains(c.Type) && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value)
            .ToList();

        // An e-mail address counts only when the provider states it is verified (Google does for its own
        // accounts). Providers that let users or admins set an unverified address never match by e-mail.
        if (principal.FindFirst("email")?.Value is { Length: > 0 } email
            && string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            subjects.Add(email);
        }

        var match = options.CurrentValue.Users.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(u.Subject) && subjects.Contains(u.Subject, StringComparer.OrdinalIgnoreCase));
        if (match is null)
        {
            return null;
        }

        var role = string.Equals(match.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase) ? UserRoles.Admin : UserRoles.Viewer;
        return new User(match.Subject, match.Subject, role);
    }
}

public sealed class RoleRequirement(bool adminOnly) : IAuthorizationRequirement
{
    public bool AdminOnly { get; } = adminOnly;
}

public sealed class RoleHandler(Allowlist allowlist) : AuthorizationHandler<RoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        var user = allowlist.Resolve(context.User);
        if (user is not null && (!requirement.AdminOnly || user.Role == UserRoles.Admin))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Local development sign-in: no identity provider, the user comes from configuration or from the
/// <c>X-Dev-User</c> request header. Registered only in the Development environment.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "Dev";
    public const string Header = "X-Dev-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers.TryGetValue(Header, out var header) ? header.ToString() : auth.CurrentValue.DevUser;
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim("sub", user), new Claim("name", user)], SchemeName, "name", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Sign-in for <see cref="AuthOptions.ModeNone"/>: every request is the local administrator.</summary>
public sealed class LocalAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "Local";
    public const string Subject = "local";
    public const string DisplayName = "Lokal";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", Subject), new Claim("name", DisplayName)], SchemeName, "name", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class AuthSetup
{
    public const string Viewer = "viewer";
    public const string Admin = "admin";
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "EnergyMonitor";

    public static void AddEnergyMonitorAuth(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
        builder.Services.AddSingleton<Allowlist>();
        builder.Services.AddSingleton<IAuthorizationHandler, RoleHandler>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Viewer, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(adminOnly: false)))
            .AddPolicy(Admin, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(adminOnly: true)));

        var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        if (IsNone(builder.Configuration))
        {
            builder.Services.AddAuthentication(LocalAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, LocalAuthHandler>(LocalAuthHandler.SchemeName, null);
            return;
        }

        if (string.Equals(auth.Mode, AuthOptions.ModeDevelopment, StringComparison.OrdinalIgnoreCase))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("Auth:Mode=Development is only allowed in the Development environment.");
            }

            builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
            return;
        }

        if (string.IsNullOrWhiteSpace(auth.Authority) || string.IsNullOrWhiteSpace(auth.ClientId))
        {
            throw new InvalidOperationException("Auth:Authority and Auth:ClientId must be configured (or Auth:Mode=Development for local development).");
        }

        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = "em_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
            })
            .AddOpenIdConnect(o =>
            {
                o.Authority = auth.Authority;
                o.ClientId = auth.ClientId;
                o.ClientSecret = auth.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = "name";
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
            });
    }

    /// <summary>True when the app runs without sign-in (<see cref="AuthOptions.ModeNone"/>).</summary>
    public static bool IsNone(IConfiguration configuration) =>
        string.Equals(configuration["Auth:Mode"], AuthOptions.ModeNone, StringComparison.OrdinalIgnoreCase);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var none = IsNone(app.Configuration);
        if (none)
        {
            app.Logger.LogWarning("Auth:Mode=None: there is no sign-in and every request acts as the administrator. Keep the app reachable only on a private network.");
        }

        var dev = none || string.Equals(app.Configuration["Auth:Mode"], AuthOptions.ModeDevelopment, StringComparison.OrdinalIgnoreCase);

        app.MapGet("/auth/login", (string? returnUrl) =>
        {
            // Only local paths: never redirect to another site after sign-in. IsLocalUrl also rejects the
            // forms browsers turn into another host, such as "/\t/evil.example" (tabs and newlines are stripped).
            var target = RedirectHttpResult.IsLocalUrl(returnUrl) ? returnUrl! : "/";
            return dev ? Results.Redirect(target) : Results.Challenge(new AuthenticationProperties { RedirectUri = target });
        }).AllowAnonymous();

        app.MapPost("/auth/logout", () => dev
            ? Results.Ok()
            : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme]))
            .AllowAnonymous();
    }

    /// <summary>
    /// API calls answer 401/403 instead of redirecting to a sign-in page, and state-changing calls must
    /// carry a header a cross-site form cannot set.
    /// </summary>
    public static void UseApiGuards(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/api")
                && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
                && request.Headers[CsrfHeader] != CsrfValue)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "missing_request_header" });
                return;
            }

            await next();
        });
    }

    public static void ConfigureApiChallenges(this IServiceCollection services) =>
        services
            .PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.Events.OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden);
            })
            .PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var redirect = o.Events.OnRedirectToIdentityProvider;
                o.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }

                    return redirect(context);
                };
            });

    private static Task Status(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = status;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }
}
