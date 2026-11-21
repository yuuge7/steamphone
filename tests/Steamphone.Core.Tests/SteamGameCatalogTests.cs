using System.Net;
using System.Text;
using Steamphone.Core;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class SteamGameCatalogTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "steamphone-titles-" + Guid.NewGuid().ToString("N"));
    private AppDatabase database = null!;
    private const string Dota = "{\"570\":{\"success\":true,\"data\":{\"steam_appid\":570,\"name\":\"Dota 2\"}}}";
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        database = new(Path.Combine(directory, "test.db3"));
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { await database.CloseAsync(); Directory.Delete(directory, true); }

    [Fact]
    public async Task ConcurrentLookupsShareOneRequestAndNamesPersistAcrossCatalogInstances()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("store.steampowered.com", request.RequestUri!.Host);
            Assert.Contains("appids=570", request.RequestUri.Query);
            await Task.Delay(30, token);
            return Json(Dota);
        }));
        var catalog = new SteamGameCatalog(http, database);
        var names = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.GetNameAsync(570)));
        Assert.All(names, name => Assert.Equal("Dota 2", name));
        Assert.Equal(1, calls);
        using var offline = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        var reopened = new SteamGameCatalog(offline, database);
        Assert.Equal("Dota 2", await reopened.GetNameAsync(570));
        Assert.Equal("Dota 2", (await reopened.GetCachedNamesAsync([570, 730]))[570]);
        Assert.False((await reopened.GetCachedNamesAsync([730])).ContainsKey(570));
    }

    [Theory]
    [InlineData("{\"570\":{\"success\":false}}")]
    [InlineData("{\"730\":{\"success\":true,\"data\":{\"steam_appid\":730,\"name\":\"Other game\"}}}")]
    [InlineData("{\"570\":{\"success\":true,\"data\":{\"steam_appid\":730,\"name\":\"Other game\"}}}")]
    [InlineData("{\"570\":{\"success\":true,\"data\":{\"steam_appid\":\"570\",\"name\":\"Other game\"}}}")]
    [InlineData("{\"570\":{\"success\":true,\"data\":{\"steam_appid\":570,\"name\":\" \"}}}")]
    [InlineData("not JSON")]
    public async Task UnavailableOrMalformedTitlesDoNotInventNamesOrRepeatRequests(string body)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(body)); }));
        var catalog = new SteamGameCatalog(http, database);
        Assert.Null(await catalog.GetNameAsync(570));
        Assert.Null(await catalog.GetNameAsync(570));
        Assert.Null(await catalog.GetNameAsync(0));
        Assert.Empty(await database.GetSteamGameTitlesAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NetworkFailurePreservesThePreviouslyCachedTitle()
    {
        await database.SaveSteamGameTitleAsync(new() { AppId = 570, Name = "Dota 2", UpdatedUtcTicks = DateTime.UtcNow.AddDays(-8).Ticks });
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        Assert.Equal("Dota 2", await new SteamGameCatalog(http, database).GetNameAsync(570));
    }

    [Fact]
    public async Task LeavingDuringLookupCancelsItAndALaterVisitCanRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Json(Dota);
        }));
        var catalog = new SteamGameCatalog(http, database);
        using var visible = new CancellationTokenSource();
        var lookup = catalog.GetNameAsync(570, visible.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        visible.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal("Dota 2", await catalog.GetNameAsync(570));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LocalOnlyModeUsesCachedNamesWithoutSendingNetworkRequests()
    {
        await database.SaveSettingsAsync(new() { Visibility = VisibilityMode.LocalOnly });
        await database.SaveSteamGameTitleAsync(new() { AppId = 570, Name = "Dota 2", UpdatedUtcTicks = 1 });
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Dota)); }));
        var catalog = new SteamGameCatalog(http, database);
        Assert.Equal("Dota 2", await catalog.GetNameAsync(570));
        Assert.Null(await catalog.GetNameAsync(730));
        Assert.Equal(0, calls);
        Assert.Equal(VisibilityMode.LocalOnly, (await database.GetSettingsAsync()).Visibility);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => respond(request, cancellation);
    }
}
