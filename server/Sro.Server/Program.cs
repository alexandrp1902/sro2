using Microsoft.Extensions.FileProviders;
using Sro.Server.Accounts;
using Sro.Server.Game;
using Sro.Server.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<BalanceStore>();
// Аккаунты пилотов — JSON-файлы в папке данных (по умолчанию data/accounts в корне репозитория, вне git).
builder.Services.AddSingleton(sp => new AccountStore(
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["DataDir"]!, "accounts")),
    sp.GetRequiredService<ILogger<AccountStore>>()));
builder.Services.AddSingleton<GalaxyHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GalaxyHost>());

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// Сервер сам отдаёт собранный клиент (client/dist): по Wi-Fi и через tunnel игра открывается одной ссылкой.
var clientDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, app.Configuration["ClientDist"]!));
if (Directory.Exists(clientDist))
{
    var files = new PhysicalFileProvider(clientDist);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.Logger.LogWarning("Client build not found at {Path}; run 'npm run build' in client/ to serve the game from this server", clientDist);
}

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Map("/ws", async (HttpContext context, GalaxyHost room, AccountStore accounts, ILogger<WebSocketConnection> log) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await new WebSocketConnection(socket, log).RunAsync(room, accounts, context.RequestAborted);
});

app.Run();
