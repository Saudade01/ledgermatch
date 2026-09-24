using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mutabakat.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.MaxDepth = 16;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.AddDbContext<ReconciliationDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Database") ?? throw new InvalidOperationException("Set ConnectionStrings__Database.")));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    // Explicit bound also covers chunked requests and the in-process integration test host.
    const int limit = 2 * 1024 * 1024;
    if (HttpMethods.IsPost(context.Request.Method))
    {
        if (context.Request.ContentLength > limit)
        {
            await Results.Problem(statusCode: 413, title: "Request exceeds 2 MiB.").ExecuteAsync(context);
            return;
        }
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (bounded.Length + count > limit)
            {
                await Results.Problem(statusCode: 413, title: "Request exceeds 2 MiB.").ExecuteAsync(context);
                return;
            }
            await bounded.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        bounded.Position = 0;
        var original = context.Request.Body;
        context.Request.Body = bounded;
        try { await next(context); }
        finally { context.Request.Body = original; }
        return;
    }
    await next(context);
});
app.MapOpenApi();
app.MapDatasets();
app.MapReconciliations();
app.MapGet("/health", async (ReconciliationDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.Problem(statusCode: 503, title: "Database unavailable."));

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ReconciliationDb>();
    await db.Database.MigrateAsync();
}
await app.RunAsync();

public partial class Program;
