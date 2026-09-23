using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Foreign Keys=True: explicit, defensive setting, not strictly required for this exact
// package set. Verified empirically (SqliteConnection opened with no "Foreign Keys=" keyword
// at all, then `PRAGMA foreign_keys;` queried): the native e_sqlite3 build pulled in by
// SQLitePCLRaw.bundle_e_sqlite3 2.1.12 (Microsoft.Data.Sqlite's dependency) is compiled with
// SQLITE_DEFAULT_FOREIGN_KEYS=1, so FK enforcement is already on by default with this
// package set even without the keyword. Also confirmed by decompiling
// Microsoft.Data.Sqlite/Microsoft.EntityFrameworkCore.Sqlite.Core 10.0.12: neither issues the
// PRAGMA itself unless the "Foreign Keys" connection-string keyword is present (Microsoft
// .Data.Sqlite's internal default for that keyword is null/unset — it does not force "true").
// Kept explicit anyway: it's cheap, and it guards against any native SQLite build in this
// dependency chain that doesn't happen to default FK enforcement on (the compile-time default
// varies across SQLite builds/distributions).
// EnsureCreated()/seed data/endpoints are out of scope for this iteration.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=smartcraft.db;Foreign Keys=True";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
