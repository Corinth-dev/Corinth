var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "CCF sample API (phase 0 placeholder)");
app.Run();
