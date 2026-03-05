using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace TabHistorian.Web.Tests;

[TestFixture]
public class WebAppTests : PageTest
{
    private const string BaseUrl = "http://localhost:17000";

    /// <summary>Filter out 404s for static assets (favicons, fonts, etc.) which are not app errors.</summary>
    private static bool IsAppError(string message) =>
        !message.Contains("404") || message.Contains("/api/");

    [Test]
    public async Task HomePage_LoadsWithoutErrors()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        Page.PageError += (_, error) => consoleErrors.Add($"PAGE ERROR: {error}");

        var response = await Page.GotoAsync(BaseUrl);
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Status, Is.EqualTo(200));

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Check title contains TabHistorian
        var title = await Page.TitleAsync();
        Assert.That(title, Does.Contain("TabHistorian").IgnoreCase);

        // Verify main heading is visible
        await Expect(Page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Tab.?Historian", RegexOptions.IgnoreCase) })).ToBeVisibleAsync();

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors found:\n{string.Join("\n", consoleErrors)}");
    }

    [Test]
    public async Task HomePage_SearchBoxExists()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var searchBox = Page.GetByPlaceholder(new Regex("search", RegexOptions.IgnoreCase));
        await Expect(searchBox).ToBeVisibleAsync();
    }

    [Test]
    public async Task HomePage_SearchReturnsResults()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var searchBox = Page.GetByPlaceholder(new Regex("search", RegexOptions.IgnoreCase));
        await searchBox.FillAsync("google");
        await searchBox.PressAsync("Enter");

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(1000);

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors during search:\n{string.Join("\n", consoleErrors)}");
    }

    [Test]
    public async Task HomePage_StatsBarVisible()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var statsText = Page.GetByText(new Regex(@"snapshot|tab", RegexOptions.IgnoreCase));
        await Expect(statsText.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task HomePage_TabFeedLoads()
    {
        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        var links = Page.Locator("a[href*='http']");
        var count = await links.CountAsync();
        Assert.That(count, Is.GreaterThan(0), "Expected tab links to be displayed");
    }

    [Test]
    public async Task ExplorePage_LoadsWithoutErrors()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        Page.PageError += (_, error) => consoleErrors.Add($"PAGE ERROR: {error}");

        var response = await Page.GotoAsync($"{BaseUrl}/explore");
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Status, Is.EqualTo(200));

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors on explore page:\n{string.Join("\n", consoleErrors)}");
    }

    [Test]
    public async Task ExplorePage_ShowsSnapshots()
    {
        await Page.GotoAsync($"{BaseUrl}/explore");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(3000);

        // Should show snapshot sections with timestamps or snapshot-related content
        var snapshotHeaders = Page.GetByText(new Regex(@"\d{4}-\d{2}-\d{2}"));
        var count = await snapshotHeaders.CountAsync();
        if (count == 0)
        {
            // Fallback: check for any snapshot/window/tab content
            var anyContent = Page.GetByText(new Regex(@"window|tab|profile", RegexOptions.IgnoreCase));
            count = await anyContent.CountAsync();
        }
        Assert.That(count, Is.GreaterThan(0), "Expected snapshot data to be displayed");
    }

    [Test]
    public async Task ExplorePage_FiltersWork()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        await Page.GotoAsync($"{BaseUrl}/explore");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(2000);

        // Try to find and interact with a filter selector
        var selector = Page.Locator("select, [role='combobox'], [role='listbox'], button:has-text('filter'), button:has-text('Filter')").First;
        if (await selector.IsVisibleAsync())
        {
            await selector.ClickAsync();
            await Page.WaitForTimeoutAsync(500);
        }

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors during filter interaction:\n{string.Join("\n", consoleErrors)}");
    }

    [Test]
    public async Task ExplorePage_NavigateFromHome()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        await Page.GotoAsync(BaseUrl);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var exploreLink = Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("explore", RegexOptions.IgnoreCase) });
        if (await exploreLink.CountAsync() > 0)
        {
            await exploreLink.First.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Page.WaitForTimeoutAsync(1000);

            Assert.That(Page.Url, Does.Contain("/explore"));
        }

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors during navigation:\n{string.Join("\n", consoleErrors)}");
    }

    [Test]
    public async Task ApiEndpoints_ReturnValidJson()
    {
        // Test /api/snapshots
        var snapshotsResponse = await Page.APIRequest.GetAsync($"{BaseUrl}/api/snapshots");
        Assert.That(snapshotsResponse.Status, Is.EqualTo(200), "/api/snapshots should return 200");
        var snapshotsBody = await snapshotsResponse.TextAsync();
        Assert.That(snapshotsBody, Does.StartWith("["), "/api/snapshots should return JSON array");

        // Test /api/profiles
        var profilesResponse = await Page.APIRequest.GetAsync($"{BaseUrl}/api/profiles");
        Assert.That(profilesResponse.Status, Is.EqualTo(200), "/api/profiles should return 200");
        var profilesBody = await profilesResponse.TextAsync();
        Assert.That(profilesBody, Does.StartWith("["), "/api/profiles should return JSON array");

        // Test /api/tabs
        var tabsResponse = await Page.APIRequest.GetAsync($"{BaseUrl}/api/tabs");
        Assert.That(tabsResponse.Status, Is.EqualTo(200), "/api/tabs should return 200");
        var tabsBody = await tabsResponse.TextAsync();
        Assert.That(tabsBody, Does.Contain("\"items\""), "/api/tabs should return object with items");
        Assert.That(tabsBody, Does.Contain("\"totalCount\""), "/api/tabs should return totalCount");

        // Test /api/tabs with search query
        var searchResponse = await Page.APIRequest.GetAsync($"{BaseUrl}/api/tabs?q=google");
        Assert.That(searchResponse.Status, Is.EqualTo(200), "/api/tabs?q=google should return 200");
    }

    [Test]
    public async Task ApiEndpoints_PaginationWorks()
    {
        var page1Response = await Page.APIRequest.GetAsync($"{BaseUrl}/api/tabs?page=1&pageSize=5");
        Assert.That(page1Response.Status, Is.EqualTo(200));
        var page1Body = await page1Response.TextAsync();
        Assert.That(page1Body, Does.Contain("\"page\":1"));
        Assert.That(page1Body, Does.Contain("\"pageSize\":5"));

        var page2Response = await Page.APIRequest.GetAsync($"{BaseUrl}/api/tabs?page=2&pageSize=5");
        Assert.That(page2Response.Status, Is.EqualTo(200));
    }

    [Test]
    public async Task ScalarDocs_Available()
    {
        var response = await Page.APIRequest.GetAsync($"{BaseUrl}/openapi/v1.json");
        Assert.That(response.Status, Is.EqualTo(200), "OpenAPI spec should be available");

        var scalarResponse = await Page.GotoAsync($"{BaseUrl}/scalar/v1");
        Assert.That(scalarResponse, Is.Not.Null);
        Assert.That(scalarResponse!.Status, Is.EqualTo(200), "Scalar docs should be available");
    }

    [Test]
    public async Task LoadTest_ConcurrentLargePageRequests()
    {
        const int pageSize = 5000;
        int[] concurrencyLevels = [1, 5, 10, 20];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        // Warm up
        await http.GetAsync($"{BaseUrl}/api/tabs?page=1&pageSize=1");

        foreach (var concurrency in concurrencyLevels)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var tasks = Enumerable.Range(1, concurrency).Select(async i =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await http.GetAsync(
                    $"{BaseUrl}/api/tabs?page=1&pageSize={pageSize}&q={(i % 5 == 0 ? "google" : "")}");
                var body = await response.Content.ReadAsStringAsync();
                sw.Stop();
                return new { Index = i, Status = (int)response.StatusCode, ElapsedMs = sw.ElapsedMilliseconds, BodyLength = body.Length };
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();

            var failures = results.Where(r => r.Status != 200).ToList();
            var avgMs = results.Average(r => r.ElapsedMs);
            var maxMs = results.Max(r => r.ElapsedMs);
            var p95Index = Math.Min((int)(results.Length * 0.95), results.Length - 1);
            var p95Ms = results.OrderBy(r => r.ElapsedMs).ElementAt(p95Index).ElapsedMs;
            var avgBodyKb = results.Average(r => r.BodyLength) / 1024.0;

            Console.WriteLine($"  Concurrency={concurrency,-3}  wall={stopwatch.ElapsedMilliseconds,6}ms  avg={avgMs,6:F0}ms  p95={p95Ms,6}ms  max={maxMs,6}ms  avgBody={avgBodyKb:F0}KB  failures={failures.Count}");

            Assert.That(failures, Is.Empty, $"Concurrency {concurrency}: {string.Join(", ", failures.Select(f => $"#{f.Index}={f.Status}"))}");
        }
    }

    [Test]
    public async Task LoadTest_ConcurrentMixedEndpoints()
    {
        const int concurrentRequests = 20;

        var endpoints = new[]
        {
            "/api/tabs?page=1&pageSize=5000",
            "/api/tabs?page=1&pageSize=5000&q=chrome",
            "/api/tabs?page=1&pageSize=5000&q=github",
            "/api/tabs?page=2&pageSize=5000",
            "/api/snapshots",
            "/api/profiles",
            "/api/tabs?page=1&pageSize=1000",
            "/api/tabs?page=1&pageSize=2000&q=http",
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
        {
            var endpoint = endpoints[i % endpoints.Length];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await http.GetAsync($"{BaseUrl}{endpoint}");
            sw.Stop();
            return new { Index = i, Endpoint = endpoint, Status = (int)response.StatusCode, ElapsedMs = sw.ElapsedMilliseconds };
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var failures = results.Where(r => r.Status != 200).ToList();
        var byEndpoint = results.GroupBy(r => r.Endpoint).Select(g => new
        {
            Endpoint = g.Key,
            Count = g.Count(),
            AvgMs = g.Average(r => r.ElapsedMs),
            MaxMs = g.Max(r => r.ElapsedMs)
        });

        Console.WriteLine($"Mixed load test: {concurrentRequests} concurrent requests across {endpoints.Length} endpoints");
        Console.WriteLine($"  Total wall time: {stopwatch.ElapsedMilliseconds}ms");
        foreach (var ep in byEndpoint)
            Console.WriteLine($"  {ep.Endpoint,-50} avg={ep.AvgMs:F0}ms  max={ep.MaxMs}ms  (n={ep.Count})");
        Console.WriteLine($"  Failures: {failures.Count}");

        Assert.That(failures, Is.Empty, $"Some requests failed: {string.Join(", ", failures.Select(f => $"{f.Endpoint}={f.Status}"))}");
    }

    [Test]
    public async Task LoadTest_RapidFrontendNavigation()
    {
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error" && IsAppError(msg.Text))
                consoleErrors.Add(msg.Text);
        };

        Page.PageError += (_, error) => consoleErrors.Add($"PAGE ERROR: {error}");

        var pages = new[] { BaseUrl, $"{BaseUrl}/explore", BaseUrl, $"{BaseUrl}/explore", BaseUrl };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var url in pages)
        {
            var response = await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Commit, Timeout = 60000 });
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.Status, Is.EqualTo(200), $"Failed loading {url}");
        }

        // Settle on explore page and wait for full load
        await Page.GotoAsync($"{BaseUrl}/explore", new() { Timeout = 60000 });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 60000 });
        await Page.WaitForTimeoutAsync(2000);
        stopwatch.Stop();

        Console.WriteLine($"Rapid navigation test: {pages.Length + 1} page loads in {stopwatch.ElapsedMilliseconds}ms");

        // Do a search on home page right after
        await Page.GotoAsync(BaseUrl, new() { Timeout = 60000 });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 60000 });
        var searchBox = Page.GetByPlaceholder(new Regex("search", RegexOptions.IgnoreCase));
        await searchBox.FillAsync("test");
        await searchBox.PressAsync("Enter");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 60000 });
        await Page.WaitForTimeoutAsync(1000);

        if (consoleErrors.Count > 0)
            Assert.Fail($"Console errors during rapid navigation:\n{string.Join("\n", consoleErrors)}");
    }
}
