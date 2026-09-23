using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Foreign Keys=True: SQLite disables FK enforcement per connection unless asked.
// EnsureCreated()/seed data/endpoints are out of scope for this iteration.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=smartcraft.db;Foreign Keys=True";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
