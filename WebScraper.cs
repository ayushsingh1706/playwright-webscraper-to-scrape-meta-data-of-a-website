using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlaywrightScraper;

public class PageMetadata
{
    [JsonPropertyName("url")]            public string       Url           { get; set; } = "";
    [JsonPropertyName("title")]          public string       Title         { get; set; } = "";
    [JsonPropertyName("description")]    public string       Description   { get; set; } = "";
    [JsonPropertyName("keywords")]       public string       Keywords      { get; set; } = "";
    [JsonPropertyName("author")]         public string       Author        { get; set; } = "";
    [JsonPropertyName("og_title")]       public string       OgTitle       { get; set; } = "";
    [JsonPropertyName("og_description")] public string       OgDescription { get; set; } = "";
    [JsonPropertyName("og_image")]       public string       OgImage       { get; set; } = "";
    [JsonPropertyName("canonical")]      public string       Canonical     { get; set; } = "";
    [JsonPropertyName("robots")]         public string       Robots        { get; set; } = "";
    [JsonPropertyName("viewport")]       public string       Viewport      { get; set; } = "";
    [JsonPropertyName("charset")]        public string       Charset       { get; set; } = "";
    [JsonPropertyName("h1_tags")]        public List<string> H1Tags        { get; set; } = new();
    [JsonPropertyName("h2_tags")]        public List<string> H2Tags        { get; set; } = new();
    [JsonPropertyName("links")]          public List<LinkInfo>  Links      { get; set; } = new();
    [JsonPropertyName("images")]         public List<ImageInfo> Images     { get; set; } = new();
    [JsonPropertyName("status_code")]    public int          StatusCode    { get; set; }
    [JsonPropertyName("scraped_at")]     public string       ScrapedAt     { get; set; } = "";
    [JsonPropertyName("load_time_ms")]   public long         LoadTimeMs    { get; set; }
}

public class LinkInfo
{
    [JsonPropertyName("href")] public string Href { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public class ImageInfo
{
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("alt")] public string Alt { get; set; } = "";
}

public class ScrapeResult
{
    [JsonPropertyName("base_url")]            public string             BaseUrl           { get; set; } = "";
    [JsonPropertyName("total_pages_scraped")] public int                TotalPagesScraped { get; set; }
    [JsonPropertyName("scrape_started_at")]   public string             ScrapeStartedAt   { get; set; } = "";
    [JsonPropertyName("scrape_finished_at")]  public string             ScrapeFinishedAt  { get; set; } = "";
    [JsonPropertyName("pages")]               public List<PageMetadata> Pages             { get; set; } = new();
    [JsonPropertyName("failed_urls")]         public List<FailedUrl>    FailedUrls        { get; set; } = new();
}

public class FailedUrl
{
    [JsonPropertyName("url")]    public string Url    { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public class ScraperOptions
{
    public int    Workers                { get; set; } = 5;
    public int    MaxPages               { get; set; } = 0;
    public int    DelayBetweenRequestsMs { get; set; } = 200;
    public float  PageTimeoutMs          { get; set; } = 20_000;
    public bool   SkipErrorPages         { get; set; } = true;
    public int    SaveProgressEvery      { get; set; } = 20;
    public string OutputFile             { get; set; } = "scrape_result.json";
    public string UserAgent              { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}

public class WebScraper
{
    private readonly ScraperOptions _options;

    // Thread-safe shared state
    private readonly ConcurrentDictionary<string, byte> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string>         _urlChannel;
    private readonly ConcurrentBag<PageMetadata>        _pages   = new();
    private readonly ConcurrentBag<FailedUrl>           _failed  = new();

    private Uri      _baseUri   = null!;
    private string   _startUrl  = "";
    private DateTime _startedAt;
    private int      _pageCount = 0;
    private int      _activeWorkers = 0;

    public WebScraper(ScraperOptions? options = null)
    {
        _options    = options ?? new ScraperOptions();
        _urlChannel = new BlockingCollection<string>(boundedCapacity: 50_000);
    }

    public async Task<string> ScrapeWebsiteAsync(string startUrl)
    {
        if (!startUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            startUrl = "https://" + startUrl;

        _startUrl  = startUrl;
        _baseUri   = new Uri(startUrl);
        _startedAt = DateTime.UtcNow;

        Console.WriteLine("[Scraper] Verifying Chromium...");
        Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        Console.WriteLine("[Scraper] Chromium ready.\n");

        // Seed the first URL
        _visited.TryAdd(NormaliseUrl(startUrl), 0);
        _urlChannel.Add(NormaliseUrl(startUrl));

        var playwright = await Playwright.CreateAsync();
        var browser    = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] {
                "--no-sandbox", "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--disable-dev-shm-usage"
            }
        });

        Console.WriteLine($"[Scraper] Starting {_options.Workers} parallel workers...");
        Console.WriteLine($"[Scraper] MaxPages = {(_options.MaxPages == 0 ? "UNLIMITED" : _options.MaxPages.ToString())}\n");

        // Start all workers simultaneously
        _activeWorkers = _options.Workers;
        var workerTasks = Enumerable.Range(1, _options.Workers)
            .Select(id => Task.Run(() => RunWorkerAsync(browser, id)))
            .ToList();

        // Auto-save in background
        using var cts      = new CancellationTokenSource();
        var       saveTask = AutoSaveAsync(cts.Token);

        await Task.WhenAll(workerTasks);
        cts.Cancel();
        try { await saveTask; } catch { }

        await browser.CloseAsync();
        playwright.Dispose();

        var json = BuildJson();
        await File.WriteAllTextAsync(_options.OutputFile, json);
        Console.WriteLine($"\n[Scraper] DONE — {_pages.Count} pages, {_failed.Count} failures.");
        Console.WriteLine($"[Scraper] Saved: {_options.OutputFile}");
        return json;
    }

    private async Task RunWorkerAsync(IBrowser browser, int workerId)
    {
        // Each worker gets its own browser context
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent         = _options.UserAgent,
            IgnoreHTTPSErrors = true,
            ViewportSize      = new ViewportSize { Width = 1280, Height = 800 },
            ExtraHTTPHeaders  = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Accept"]          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            }
        });

        await context.AddInitScriptAsync(
            "Object.defineProperty(navigator,'webdriver',{get:()=>undefined})");

        // Block images, fonts, stylesheets — not needed for metadata
        await context.RouteAsync("**/*", async route =>
        {
            var t = route.Request.ResourceType;
            if (t is "image" or "media" or "font" or "stylesheet")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });

        Console.WriteLine($"[W{workerId}] Worker started");

        try
        {
            // Keep taking URLs until channel is complete
            foreach (var url in _urlChannel.GetConsumingEnumerable())
            {
                if (_options.MaxPages > 0 && _pageCount >= _options.MaxPages)
                    break;

                var count = Interlocked.Increment(ref _pageCount);
                Console.WriteLine($"[W{workerId}] [{count}] {url}  (queue:{_urlChannel.Count})");

                var meta = await ScrapePageAsync(context, url);
                if (meta != null)
                {
                    _pages.Add(meta);

                    // Enqueue new internal links — webpages only
                    int newLinks = 0;
                    foreach (var link in meta.Links.Where(l => l.Type == "internal"))
                    {
                        var n = NormaliseUrl(link.Href);
                        n = DeduplicateRegionalUrl(n);
                        if (!IsScrapableUrl(n)) continue;
                        if (_visited.TryAdd(n, 0))
                        {
                            if (_options.MaxPages == 0 || _pageCount < _options.MaxPages)
                            {
                                _urlChannel.TryAdd(n);
                                newLinks++;
                            }
                        }
                    }
                    if (newLinks > 0)
                        Console.WriteLine($"[W{workerId}]   +{newLinks} links added");
                }

                if (_options.DelayBetweenRequestsMs > 0)
                    await Task.Delay(_options.DelayBetweenRequestsMs);
            }
        }
        finally
        {
            await context.CloseAsync();
            Console.WriteLine($"[W{workerId}] Worker done");

            // Last worker closes the channel so others stop
            if (Interlocked.Decrement(ref _activeWorkers) == 0)
                _urlChannel.CompleteAdding();
        }
    }

    private async Task<PageMetadata?> ScrapePageAsync(IBrowserContext context, string url)
    {
        IPage? page = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int statusCode = 0;

        try
        {
            page = await context.NewPageAsync();
            page.Response += (_, r) =>
            {
                try { if (r.Url.TrimEnd('/') == url.TrimEnd('/')) statusCode = r.Status; } catch { }
            };

            try
            {
                var r = await page.GotoAsync(url, new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _options.PageTimeoutMs });
                if (r != null) statusCode = r.Status;
            }
            catch (TimeoutException) { }

            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 6000 }); } catch { }

            sw.Stop();
            if (statusCode == 0) statusCode = 200;

            if (_options.SkipErrorPages && statusCode >= 400)
            {
                _failed.Add(new() { Url = url, Reason = $"HTTP {statusCode}" });
                return null;
            }

            var meta = new PageMetadata
            {
                Url = url, StatusCode = statusCode,
                ScrapedAt = DateTime.UtcNow.ToString("o"), LoadTimeMs = sw.ElapsedMilliseconds
            };

            try { meta.Title = await page.TitleAsync(); } catch { }

            // All meta in one JS call — tries every possible meta tag format
            try
            {
                var m = await page.EvaluateAsync<Dictionary<string, string>>(@"() => {
                    const g = (a,v) => {
                        const e = document.querySelector('meta['+a+'=""'+v+'""]')
                               || document.querySelector('meta['+a+""='""+v+""'""]');
                        return e ? (e.getAttribute('content') || '') : '';
                    };
                    const c   = document.querySelector('link[rel=""canonical""]');
                    const ogD = g('property','og:description') || g('name','og:description');
                    const ogT = g('property','og:title')       || g('name','og:title');
                    const ogI = g('property','og:image')       || g('name','og:image');

                    // Fallback: grab first <p> text if no description meta found
                    const desc = g('name','description')
                              || g('property','description')
                              || '';

                    return {
                        description:    desc,
                        keywords:       g('name','keywords'),
                        author:         g('name','author'),
                        robots:         g('name','robots'),
                        viewport:       g('name','viewport'),
                        og_title:       ogT,
                        og_description: ogD,
                        og_image:       ogI,
                        charset:        document.characterSet || document.charset || '',
                        canonical:      c ? c.href : window.location.href
                    };
                }");
                if (m != null)
                {
                    meta.Description   = m.GetValueOrDefault("description","");
                    meta.Keywords      = m.GetValueOrDefault("keywords","");
                    meta.Author        = m.GetValueOrDefault("author","");
                    meta.Robots        = m.GetValueOrDefault("robots","");
                    meta.Viewport      = m.GetValueOrDefault("viewport","");
                    meta.OgTitle       = m.GetValueOrDefault("og_title","");
                    meta.OgDescription = m.GetValueOrDefault("og_description","");
                    meta.OgImage       = m.GetValueOrDefault("og_image","");
                    meta.Charset       = m.GetValueOrDefault("charset","");
                    meta.Canonical     = m.GetValueOrDefault("canonical","");
                }
            }
            catch { }

            try { meta.H1Tags = await page.EvaluateAsync<List<string>>("()=>[...document.querySelectorAll('h1')].map(h=>h.innerText.trim()).filter(Boolean)") ?? new(); } catch { }
            try { meta.H2Tags = await page.EvaluateAsync<List<string>>("()=>[...document.querySelectorAll('h2')].map(h=>h.innerText.trim()).filter(Boolean)") ?? new(); } catch { }

            // Links
            var allLinks = new List<LinkInfo>();
            try
            {
                var jsLinks = await page.EvaluateAsync<List<Dictionary<string, string>>>(
                    @"()=>[...document.querySelectorAll('a[href]')].map(a=>({
                        href:a.href||'',
                        text:(a.innerText||'').trim().substring(0,150)
                    })).filter(l=>l.href&&!l.href.startsWith('javascript'))");
                foreach (var raw in jsLinks ?? new())
                {
                    if (!raw.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href)) continue;
                    var t = ClassifyLink(href);
                    allLinks.Add(new() { Href = t=="internal"?NormaliseUrl(href):href, Text=raw.GetValueOrDefault("text",""), Type=t });
                }
            }
            catch { }

            // Regex fallback
            try
            {
                var html = await page.ContentAsync();
                foreach (Match match in Regex.Matches(html, @"href\s*=\s*[""']([^""'#][^""']{2,200})[""']", RegexOptions.IgnoreCase))
                {
                    var raw = match.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string abs; try { abs = new Uri(new Uri(url), raw).ToString(); } catch { continue; }
                    var t = ClassifyLink(abs);
                    allLinks.Add(new() { Href = t=="internal"?NormaliseUrl(abs):abs, Text="", Type=t });
                }
            }
            catch { }

            meta.Links = allLinks
                .GroupBy(l => l.Href, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(l => l.Text.Length).First())
                .ToList();

            try { meta.Images = await page.EvaluateAsync<List<ImageInfo>>("()=>[...document.querySelectorAll('img')].map(i=>({src:i.src||'',alt:i.alt||''}))") ?? new(); } catch { }

            return meta;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"       ERROR: {ex.Message}");
            _failed.Add(new() { Url = url, Reason = ex.Message });
            return null;
        }
        finally { if (page != null) try { await page.CloseAsync(); } catch { } }
    }

    private async Task AutoSaveAsync(CancellationToken ct)
    {
        int lastSaved = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000).ContinueWith(_ => { });
            var current = _pages.Count;
            if (current >= lastSaved + _options.SaveProgressEvery)
            {
                await File.WriteAllTextAsync(_options.OutputFile, BuildJson());
                lastSaved = current;
                Console.WriteLine($"\n  ✅ Progress saved — {current} pages\n");
            }
        }
    }

    private string BuildJson() => JsonSerializer.Serialize(new ScrapeResult
    {
        BaseUrl = _startUrl, TotalPagesScraped = _pages.Count,
        ScrapeStartedAt = _startedAt.ToString("o"), ScrapeFinishedAt = DateTime.UtcNow.ToString("o"),
        Pages = _pages.ToList(), FailedUrls = _failed.ToList()
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    // ── Only queue actual webpages — skip files ──────────────────────
    private string DeduplicateRegionalUrl(string url)
    {
        try
        {
            var uri  = new Uri(url);
            var path = uri.AbsolutePath;
            var regionalPrefixes = new[] { "/in/", "/mena/", "/ap/", "/uk/", "/ca/", "/au/" };
            foreach (var prefix in regionalPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var canonical = path.Substring(prefix.Length - 1);
                    return $"{uri.Scheme}://{uri.Host}{canonical}";
                }
            }
        }
        catch { }
        return url;
    }

    private static bool IsScrapableUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Skip non-webpage file extensions
        var skipExtensions = new[]
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
            ".css", ".js", ".json", ".xml",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".zip", ".rar", ".tar", ".gz",
            ".mp4", ".mp3", ".avi", ".mov", ".wmv",
            ".woff", ".woff2", ".ttf", ".eot", ".otf",
            ".map", ".min.css", ".min.js"
        };

        try
        {
            var path = new Uri(url).AbsolutePath.ToLower();
            foreach (var ext in skipExtensions)
                if (path.EndsWith(ext)) return false;

            // Skip clientlib/static asset paths common in AEM sites
            if (path.Contains("/etc.clientlibs/")) return false;
            if (path.Contains("/content/dam/") && !path.EndsWith("/")) return false;
            if (path.Contains("/system/")) return false;
            if (path.Contains("/logout"))  return false;
            if (path.Contains("/login"))   return false;
            if (path.Contains("/saml_"))   return false;
        }
        catch { return false; }

        return true;
    }

    private string ClassifyLink(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "other";
        if (href.StartsWith("#")) return "anchor";
        if (href.StartsWith("mailto:") || href.StartsWith("tel:") || href.StartsWith("javascript:")) return "other";
        try { return new Uri(href, UriKind.Absolute).Host.Equals(_baseUri.Host, StringComparison.OrdinalIgnoreCase) ? "internal" : "external"; }
        catch { return "other"; }
    }

    private string NormaliseUrl(string href)
    {
        try { return new Uri(href).GetLeftPart(UriPartial.Query).TrimEnd('/'); }
        catch { return href; }
    }
}

