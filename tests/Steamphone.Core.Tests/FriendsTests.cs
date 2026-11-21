using System.Net;
using System.Net.Http.Headers;
using SteamKit2;
using Steamphone.Core;
using Xunit;

namespace Steamphone.Core.Tests;

public sealed class FriendsTests
{
    [Fact]
    public void PartialPersonaUpdatesPreserveMetadataAndExplicitUnknownLastSeenClearsIt()
    {
        var seen = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var friend = new SteamFriend(1, "Player", EPersonaState.Online, 570, "Dota 2")
            .MergeDetails(EClientPersonaStateFlag.Presence | EClientPersonaStateFlag.LastSeen,
                EPersonaStateFlag.ClientTypeMobile | EPersonaStateFlag.InJoinableGame, seen);
        var partial = friend.MergeDetails(EClientPersonaStateFlag.PlayerName, 0, DateTime.UnixEpoch);
        Assert.Equal(seen, partial.LastLogOffUtc);
        Assert.Equal("Pe mobil", partial.Device);
        Assert.True(partial.IsJoinable);
        Assert.Null(partial.MergeDetails(EClientPersonaStateFlag.LastSeen, 0, DateTime.UnixEpoch).LastLogOffUtc);
        Assert.Equal("", partial.MergeDetails(EClientPersonaStateFlag.Presence, 0, seen).Device);
    }

    [Theory]
    [InlineData(EPersonaState.Offline)]
    [InlineData(EPersonaState.Invisible)]
    public void HiddenFriendsNeverLeakStaleRichPresenceDeviceOrJoinability(EPersonaState state)
    {
        var friend = new SteamFriend(1, "Player", state, 570, "Dota 2")
        { RichActivity = "Ranked match", StateFlags = EPersonaStateFlag.ClientTypeMobile | EPersonaStateFlag.InJoinableGame };
        Assert.Equal("Offline", friend.Activity);
        Assert.Equal("", friend.Device);
        Assert.Equal("", friend.SecondaryActivity);
        Assert.False(friend.IsJoinable);
    }

    [Fact]
    public void LastSeenHandlesUnknownFutureAndElapsedTimeWithoutInventingActivity()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("", SteamFriend.FormatLastSeen(null, now));
        Assert.Equal("", SteamFriend.FormatLastSeen(DateTime.UnixEpoch, now));
        Assert.Equal("", SteamFriend.FormatLastSeen(now.AddMinutes(2), now));
        Assert.Contains("5 min", SteamFriend.FormatLastSeen(now.AddMinutes(-5), now));
        Assert.Contains("3 h", SteamFriend.FormatLastSeen(now.AddHours(-3), now));
        Assert.Contains("2 zile", SteamFriend.FormatLastSeen(now.AddDays(-2), now));
    }

    [Fact]
    public void PartialGameUpdatesKeepTheCurrentGameAndChangingGamesClearsOldRichPresence()
    {
        var friend = new SteamFriend(1, "Player", EPersonaState.Online, 570, "Dota 2") { RichActivity = "Ranked match" };
        Assert.Equal(friend, friend.MergeGameDetails(EClientPersonaStateFlag.PlayerName, 0, null));
        Assert.Equal("Ranked match", friend.MergeGameDetails(EClientPersonaStateFlag.GameExtraInfo, 570, "Dota 2").RichActivity);
        var changed = friend.MergeGameDetails(EClientPersonaStateFlag.GameExtraInfo, 730, "Counter-Strike 2");
        Assert.Equal(730u, changed.AppId);
        Assert.Equal("Counter-Strike 2", changed.Activity);
        Assert.Equal("", changed.RichActivity);
        Assert.False(friend.MergeGameDetails(EClientPersonaStateFlag.GameDataBlob, 0, null).IsPlaying);
    }

    [Fact]
    public void AvatarHashesMustBeRealSteamSha1Hashes()
    {
        Assert.Null(SteamFriend.NormalizeAvatarHash(null));
        Assert.Null(SteamFriend.NormalizeAvatarHash(new byte[20]));
        Assert.Null(SteamFriend.NormalizeAvatarHash(new byte[19]));
        Assert.Equal(new string('a', 40), SteamFriend.NormalizeAvatarHash(Enumerable.Repeat((byte)0xaa, 20).ToArray()));
        Assert.Equal("👾", new SteamFriend(1, "👾 Player", EPersonaState.Online, 0, null).Initial);
    }
}

public sealed class SteamImageCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "steamphone-images-test-" + Guid.NewGuid().ToString("N"));
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task ReopeningAnAvatarUsesDiskCacheAndInvalidKeysNeverReachNetwork()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal($"https://avatars.steamstatic.com/{Hash}_medium.jpg", request.RequestUri!.ToString());
            return Task.FromResult(Jpeg([1, 2, 3]));
        }));
        var cache = new SteamImageCache(http, directory);
        var first = await cache.GetAvatarAsync(Hash, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(first, await cache.GetAvatarAsync(Hash.ToUpperInvariant(), CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(first));
        Assert.Null(await cache.GetAvatarAsync("../../secret", CancellationToken.None));
        Assert.Null(await cache.GetAvatarAsync(new string('z', 40), CancellationToken.None));
        Assert.Null(await cache.GetGameHeaderAsync(0, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LeavingTheTabCancelsInFlightDownloadsAndDeletesPartialFiles()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Jpeg([1]);
        }));
        var cache = new SteamImageCache(http, directory);
        using var visible = new CancellationTokenSource();
        var download = cache.GetAvatarAsync(Hash, visible.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        visible.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.EnumerateFiles(directory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetGameHeaderAsync(570, visible.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizeResponsesAreRejectedEvenWithoutAContentLength(bool declared)
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            HttpContent content = declared ? new ByteArrayContent(new byte[SteamImageCache.MaximumImageBytes + 1])
                : new StreamContent(new UnseekableStream(new byte[SteamImageCache.MaximumImageBytes + 1]));
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        Assert.Null(await new SteamImageCache(http, directory).GetAvatarAsync(Hash, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task CacheEvictsOldImagesToStayWithinBudget()
    {
        Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "avatar-old.jpg");
        await File.WriteAllBytesAsync(old, new byte[SteamImageCache.MaximumCacheBytes]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Jpeg([1, 2, 3]))));
        Assert.NotNull(await new SteamImageCache(http, directory).GetAvatarAsync(Hash, CancellationToken.None));
        Assert.False(File.Exists(old));
        Assert.True(new DirectoryInfo(directory).GetFiles().Sum(x => x.Length) <= SteamImageCache.MaximumCacheBytes);
    }

    [Fact]
    public async Task SocialMediaDownloadsAreLimitedToFourConcurrentRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var peak = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            var current = Interlocked.Increment(ref count);
            Interlocked.Exchange(ref peak, Math.Max(peak, current));
            if (current == SteamImageCache.MaximumConcurrentDownloads) started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref count); }
            return Jpeg([1]);
        }));
        using var visible = new CancellationTokenSource();
        var cache = new SteamImageCache(http, directory);
        var downloads = Enumerable.Range(1, 10).Select(x => cache.GetGameHeaderAsync((uint)x, visible.Token)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        visible.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(downloads));
        Assert.Equal(SteamImageCache.MaximumConcurrentDownloads, peak);
    }

    [Fact]
    public async Task CachedAvatarsDoNotWaitBehindStalledDownloads()
    {
        var count = 0;
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "avatars.steamstatic.com") return Jpeg([1, 2, 3]);
            if (Interlocked.Increment(ref count) == SteamImageCache.MaximumConcurrentDownloads) saturated.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Jpeg([1]);
        }));
        var cache = new SteamImageCache(http, directory);
        var cached = await cache.GetAvatarAsync(Hash, CancellationToken.None);
        using var visit = new CancellationTokenSource();
        var stalled = Enumerable.Range(1, 12).Select(x => cache.GetGameHeaderAsync((uint)x, visit.Token)).ToArray();
        try
        {
            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(cached, await cache.GetAvatarAsync(Hash, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            visit.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(stalled));
        }
    }

    [Fact]
    public async Task TwelveStalledVisibleImagesFinishWithinTheInitialLoadBudget()
    {
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Jpeg([1]);
        }));
        var cache = new SteamImageCache(http, directory);
        using var visit = new CancellationTokenSource(FriendLoadProgress.LoadBudget);
        var results = await Task.WhenAll(Enumerable.Range(1, 12)
            .Select(x => cache.GetGameHeaderAsync((uint)x, visit.Token)));
        Assert.All(results, result => Assert.Null(result));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task FailedRefreshCanStillDisplayAnExpiredCachedImage()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Jpeg([1, 2, 3]))));
        var cache = new SteamImageCache(http, directory);
        var path = (await cache.GetAvatarAsync(Hash, CancellationToken.None))!;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        using var offline = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        Assert.Equal(path, await new SteamImageCache(offline, directory).GetAvatarAsync(Hash, CancellationToken.None));
    }

    private static HttpResponseMessage Jpeg(byte[] data)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}
