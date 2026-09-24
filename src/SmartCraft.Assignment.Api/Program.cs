using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Authorization;
using SmartCraft.Assignment.Api.Endpoints;
using SmartCraft.Assignment.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Foreign Keys=True: the bundled e_sqlite3 build already enforces FKs by default (verified),
// but that is a compile-time option that varies across SQLite builds, so keep it explicit.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=smartcraft.db;Foreign Keys=True";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddScoped<WorklogService>();
builder.Services.AddScoped<IInvoiceCalculator, ModernInvoiceCalculator>();
builder.Services.AddScoped<InvoiceService>();

// Invoice.CreatedAt uses TimeProvider rather than DateTimeOffset.UtcNow directly, so tests
// can substitute a fake clock.
builder.Services.AddSingleton(TimeProvider.System);

// JWT bearer (docs/04 "Authentication"): symmetric key from configuration so it can be
// overridden per environment/test. There is no default — a missing key fails fast at startup
// rather than silently accepting an insecure default. The dev key lives only in
// appsettings.Development.json; this POC has no token issuance path for any other environment
// (see Endpoints/DevTokenEndpoints.cs and README.md "Known limitations").
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key configuration is required (see appsettings.Development.json).");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "SmartCraft.Assignment";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "SmartCraft.Assignment";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim types exactly as issued ("sub", "permission") instead of ASP.NET's
        // default inbound remap to long ClaimTypes URIs — see ClaimsPrincipalExtensions.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
        };
    });

// One capability policy per permission (docs/04 "Authorization"), registered via a loop rather
// than copy-pasted per policy.
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in Permissions.All)
    {
        options.AddPolicy(permission, policy => policy.RequireClaim(Permissions.ClaimType, permission));
    }
});

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT from POST /dev/token (Development only). Paste just the token, without 'Bearer '.",
    });
    options.AddSecurityRequirement(document =>
    {
        var requirement = new OpenApiSecurityRequirement();
        requirement.Add(new OpenApiSecuritySchemeReference("Bearer", document, null), []);
        return requirement;
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SeedData.SeedAsync(db);
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "SmartCraft.Assignment API").AllowAnonymous();

app.MapProjectEndpoints();
app.MapWorklogEndpoints();
app.MapInvoiceEndpoints();
app.MapDevTokenEndpoints();

app.Run();

// WebApplicationFactory<Program> needs a public partial Program type to host the app in tests.
public partial class Program;
