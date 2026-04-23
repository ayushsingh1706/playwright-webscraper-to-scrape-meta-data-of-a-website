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
    public int    Workers       { get; set; } = 3;
    public int    MaxPages      { get; set; } = 0;
    public int    DelayMs       { get; set; } = 1000;
    public int    PageTimeoutMs { get; set; } = 30_000;
    public int    HardTimeoutMs { get; set; } = 40_000;
    public int    NetworkIdleMs { get; set; } = 5_000;
    public int    SaveEvery     { get; set; } = 10;
    public string OutputFile    { get; set; } = "scrape_result.json";
    public string UserAgent     { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}

public class WebScraper
{
    private readonly ScraperOptions _opt;
    private Uri      _base     = null!;
    private string   _startUrl = "";
    private DateTime _t0;

    private readonly ConcurrentDictionary<string, byte> _seen  = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<PageMetadata>        _pages = new();
    private readonly ConcurrentBag<FailedUrl>           _fails = new();
    private readonly ConcurrentQueue<string>            _queue = new();
    private int                                         _idle  = 0;
    private readonly SemaphoreSlim                      _sig   = new(0, int.MaxValue);
    private volatile bool                               _done  = false;

    private static readonly HashSet<string> BadExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",".jpg",".jpeg",".gif",".webp",".svg",".ico",".bmp",
        ".css",".js",".mjs",".ts",".map",
        ".woff",".woff2",".ttf",".eot",".otf",
        ".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",
        ".zip",".rar",".tar",".gz",".7z",
        ".mp4",".mp3",".avi",".mov",".wmv",".webm",".ogg",".wav",
        ".json",".xml",".rss",".atom",".txt",".csv"
    };

    private static readonly string[] BadPaths =
    {
        "/wp-json/","/wp-admin/","/wp-login","/wp-cron",
        "/api/","/graphql","/rest/v","/__data","/.well-known/",
        "/cdn-cgi/","/etc.clientlibs/","/content/dam/",
        "/feed/","/rss","/sitemap",
        "/login","/logout","/signin","/signout",
        "/register","/signup","/auth/","/oauth/",
        "/system/","/saml","/cart","/checkout",
        "/search?","/print/","/embed/",
        "/404","/500","/error"
    };

    public WebScraper(ScraperOptions? opt = null) => _opt = opt ?? new ScraperOptions();

    public async Task<string> ScrapeAsync(string startUrl)
    {
        if (!startUrl.StartsWith("http")) startUrl = "https://" + startUrl;
        _startUrl = startUrl;
        _base     = new Uri(startUrl);
        _t0       = DateTime.UtcNow;

        Console.WriteLine("[*] Checking Chromium...");
        Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        Console.WriteLine("[*] Chromium OK\n");
        Console.WriteLine($"  URL      : {startUrl}");
        Console.WriteLine($"  Workers  : {_opt.Workers}");
        Console.WriteLine($"  MaxPages : {(_opt.MaxPages == 0 ? "unlimited" : _opt.MaxPages.ToString())}");
        Console.WriteLine($"  Delay    : {_opt.DelayMs}ms\n");

        var seed = Clean(startUrl);
        _seen.TryAdd(seed, 0);
        Push(seed);

        var pw      = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args     = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--disable-dev-shm-usage",
                "--disable-infobars",
                "--window-size=1920,1080"
            }
        });

        var tasks = Enumerable.Range(1, _opt.Workers)
                              .Select(id => WorkerAsync(browser, id))
                              .ToArray();
        await Task.WhenAll(tasks);

        await browser.CloseAsync();
        pw.Dispose();

        var json = Serialize();
        await File.WriteAllTextAsync(_opt.OutputFile, json);
        Console.WriteLine($"\n[✓] DONE — {_pages.Count} pages, {_fails.Count} failed");
        Console.WriteLine($"[✓] Saved → {_opt.OutputFile}");
        return json;
    }

    private async Task WorkerAsync(IBrowser browser, int id)
    {
        var ctx = await MakeCtxAsync(browser);
        Console.WriteLine($"[W{id}] Ready");

        while (true)
        {
            if (_opt.MaxPages > 0 && _pages.Count >= _opt.MaxPages) break;
            if (_done) break;

            if (!_queue.TryDequeue(out var url))
            {
                int idle = Interlocked.Increment(ref _idle);
                if (idle == _opt.Workers && _queue.IsEmpty)
                {
                    _done = true;
                    _sig.Release(_opt.Workers);
                    Interlocked.Decrement(ref _idle);
                    break;
                }
                await _sig.WaitAsync(10_000);
                Interlocked.Decrement(ref _idle);
                if (_done) break;
                if (!_queue.TryDequeue(out url)) continue;
            }

            Console.WriteLine($"[W{id}] [{_pages.Count + _fails.Count + 1}] {url}  (queue:{_queue.Count})");

            PageMetadata? meta = null;
            try
            {
                using var cts = new CancellationTokenSource(_opt.HardTimeoutMs);
                meta = await ScrapePage(ctx, url).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[W{id}] [!] Timeout — {url}");
                _fails.Add(new() { Url = url, Reason = "Timeout" });
                try { await ctx.CloseAsync(); } catch { }
                ctx = await MakeCtxAsync(browser);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[W{id}] [!] {ex.Message}");
                _fails.Add(new() { Url = url, Reason = ex.Message });
            }

            if (meta != null)
            {
                // Update base host if site redirected (e.g. bare → www)
                if (_pages.IsEmpty)
                {
                    try
                    {
                        var h = new Uri(meta.Url).Host;
                        if (!h.Equals(_base.Host, StringComparison.OrdinalIgnoreCase))
                        {
                            _base = new Uri(meta.Url);
                            Console.WriteLine($"[*] Base host updated to: {h}");
                        }
                    }
                    catch { }
                }

                _pages.Add(meta);

                int added = 0;
                foreach (var link in meta.Links.Where(l => l.Type == "internal"))
                {
                    var c = Clean(link.Href);
                    if (IsGood(c) && _seen.TryAdd(c, 0))
                    {
                        Push(c);
                        added++;
                    }
                }

                Console.WriteLine($"[W{id}]   \"{Trunc(meta.Title, 55)}\"");
                Console.WriteLine(
                    $"[W{id}]   {meta.Links.Count(l => l.Type == "internal")} internal, " +
                    $"{meta.Links.Count(l => l.Type == "external")} external | " +
                    $"{added} new queued | queue: {_queue.Count}");

                if (_pages.Count % _opt.SaveEvery == 0) await SaveNow();
            }

            if (_opt.DelayMs > 0) await Task.Delay(_opt.DelayMs);
        }

        await ctx.CloseAsync();
        Console.WriteLine($"[W{id}] Done");
    }

    private void Push(string url) { _queue.Enqueue(url); _sig.Release(); }

    private async Task<PageMetadata?> ScrapePage(IBrowserContext ctx, string url)
    {
        var page = await ctx.NewPageAsync();
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        int code = 0;

        try
        {
            page.Response += (_, r) =>
            { try { if (r.Url.TrimEnd('/') == url.TrimEnd('/')) code = r.Status; } catch { } };

            try
            {
                var r = await page.GotoAsync(url, new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _opt.PageTimeoutMs });
                if (r != null) code = r.Status;
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"  [~] Timeout loading {url}, using partial content");
            }
            catch { }

            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new() { Timeout = _opt.NetworkIdleMs }); }
            catch { }

            sw.Stop();
            if (code == 0) code = 200;

            // Log the actual status code for debugging
            if (code >= 400)
            {
                Console.WriteLine($"  [!] HTTP {code} for {url}");
                _fails.Add(new() { Url = url, Reason = $"HTTP {code}" });
                return null;
            }

            // Check if page was blocked by bot protection
            var pageTitle = "";
            try { pageTitle = await page.TitleAsync(); } catch { }

            if (pageTitle.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                pageTitle.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
                pageTitle.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                pageTitle.Contains("DDoS protection", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [!] Bot protection detected on {url} — waiting 3s and retrying");
                await Task.Delay(3000);

                // Try once more after waiting
                try
                {
                    await page.ReloadAsync(new PageReloadOptions
                        { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _opt.PageTimeoutMs });
                    pageTitle = await page.TitleAsync();
                }
                catch { }

                if (pageTitle.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                {
                    _fails.Add(new() { Url = url, Reason = "Bot protection (Cloudflare)" });
                    return null;
                }
            }

            var m = new PageMetadata
            {
                Url        = url,
                StatusCode = code,
                ScrapedAt  = DateTime.UtcNow.ToString("o"),
                LoadTimeMs = sw.ElapsedMilliseconds,
                Title      = pageTitle
            };

            try
            {
                var d = await page.EvaluateAsync<Dictionary<string, string>>(@"() => {
                    const q=(a,v)=>{const e=document.querySelector('meta['+a+'=""'+v+'""]');return e?(e.getAttribute('content')||''):'';};
                    const can=document.querySelector('link[rel=""canonical""]');
                    return {
                        desc:   q('name','description')        || q('property','description'),
                        kw:     q('name','keywords'),
                        author: q('name','author')             || q('property','article:author'),
                        robots: q('name','robots'),
                        vp:     q('name','viewport'),
                        ogt:    q('property','og:title')       || q('name','og:title'),
                        ogd:    q('property','og:description') || q('name','og:description'),
                        ogi:    q('property','og:image')       || q('name','og:image'),
                        cs:     document.characterSet          || '',
                        canon:  can ? can.href : ''
                    };
                }");
                if (d != null)
                {
                    m.Description   = d.GetValueOrDefault("desc",   "");
                    m.Keywords      = d.GetValueOrDefault("kw",     "");
                    m.Author        = d.GetValueOrDefault("author", "");
                    m.Robots        = d.GetValueOrDefault("robots", "");
                    m.Viewport      = d.GetValueOrDefault("vp",     "");
                    m.OgTitle       = d.GetValueOrDefault("ogt",    "");
                    m.OgDescription = d.GetValueOrDefault("ogd",    "");
                    m.OgImage       = d.GetValueOrDefault("ogi",    "");
                    m.Charset       = d.GetValueOrDefault("cs",     "");
                    m.Canonical     = d.GetValueOrDefault("canon",  "");
                }
            }
            catch { }

            try { m.H1Tags = await page.EvaluateAsync<List<string>>(
                "()=>[...document.querySelectorAll('h1')].map(h=>h.innerText.trim()).filter(Boolean)") ?? new(); }
            catch { }

            try { m.H2Tags = await page.EvaluateAsync<List<string>>(
                "()=>[...document.querySelectorAll('h2')].map(h=>h.innerText.trim()).filter(Boolean)") ?? new(); }
            catch { }

            var raw = new List<LinkInfo>();
            try
            {
                var js = await page.EvaluateAsync<List<Dictionary<string, string>>>(@"
                    () => [...document.querySelectorAll('a[href]')].map(a => ({
                        href: a.href || '',
                        text: (a.innerText || a.textContent || '').trim().substring(0, 150)
                    })).filter(l => l.href && !l.href.startsWith('javascript'))
                ");
                foreach (var item in js ?? new())
                {
                    if (!item.TryGetValue("href", out var h) || string.IsNullOrWhiteSpace(h)) continue;
                    var t = Cls(h);
                    raw.Add(new() { Href = t == "internal" ? Clean(h) : h,
                                    Text = item.GetValueOrDefault("text", ""), Type = t });
                }
            }
            catch { }

            try
            {
                var html = await page.ContentAsync();
                foreach (Match mx in Regex.Matches(html,
                    @"href\s*=\s*[""']([^""'#][^""']{1,200})[""']", RegexOptions.IgnoreCase))
                {
                    var h = mx.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    string abs;
                    try { abs = new Uri(new Uri(url), h).ToString(); } catch { continue; }
                    var t = Cls(abs);
                    raw.Add(new() { Href = t == "internal" ? Clean(abs) : abs, Text = "", Type = t });
                }
            }
            catch { }

            m.Links = raw
                .GroupBy(l => l.Href, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(l => l.Text.Length).First())
                .ToList();

            try { m.Images = await page.EvaluateAsync<List<ImageInfo>>(
                "()=>[...document.querySelectorAll('img')].map(i=>({src:i.src||'',alt:i.alt||''}))") ?? new(); }
            catch { }

            return m;
        }
        finally { try { await page.CloseAsync(); } catch { } }
    }

    private async Task<IBrowserContext> MakeCtxAsync(IBrowser browser)
    {
        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent         = _opt.UserAgent,
            IgnoreHTTPSErrors = true,
            ViewportSize      = new ViewportSize { Width = 1920, Height = 1080 },
            // Realistic browser headers to avoid bot detection
            ExtraHTTPHeaders  = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Accept"]          = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
                ["Accept-Encoding"] = "gzip, deflate, br",
                ["Cache-Control"]   = "max-age=0",
                ["Sec-Fetch-Dest"]  = "document",
                ["Sec-Fetch-Mode"]  = "navigate",
                ["Sec-Fetch-Site"]  = "none",
                ["Sec-Fetch-User"]  = "?1",
                ["Upgrade-Insecure-Requests"] = "1"
            }
        });

        // Full anti-detection script
        await ctx.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'plugins', { get: () => [1,2,3,4,5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['en-US','en'] });
            Object.defineProperty(navigator, 'platform', { get: () => 'Win32' });
            window.chrome = { runtime: {} };
        ");

        // Only block media — keep images/CSS so pages render properly
        // (some sites detect bot by checking if CSS loaded)
        await ctx.RouteAsync("**/*", async route =>
        {
            var t = route.Request.ResourceType;
            if (t is "media" or "font")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });

        return ctx;
    }

    private async Task SaveNow()
    {
        try
        {
            await File.WriteAllTextAsync(_opt.OutputFile, Serialize());
            Console.WriteLine($"\n  [✓] Saved {_pages.Count} pages → {_opt.OutputFile}\n");
        }
        catch (Exception ex) { Console.WriteLine($"  [!] Save error: {ex.Message}"); }
    }

    private string Serialize() => JsonSerializer.Serialize(new ScrapeResult
    {
        BaseUrl           = _startUrl,
        TotalPagesScraped = _pages.Count,
        ScrapeStartedAt   = _t0.ToString("o"),
        ScrapeFinishedAt  = DateTime.UtcNow.ToString("o"),
        Pages             = _pages.OrderBy(p => p.Url).ToList(),
        FailedUrls        = _fails.ToList()
    }, new JsonSerializerOptions
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    private static string Clean(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Path).TrimEnd('/'); }
        catch { return url; }
    }

    private string Cls(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "other";
        if (href.StartsWith("#"))            return "anchor";
        if (href.StartsWith("mailto:") || href.StartsWith("tel:") || href.StartsWith("javascript:"))
            return "other";
        try
        {
            var h        = new Uri(href, UriKind.Absolute).Host;
            var baseHost = _base.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                           ? _base.Host[4..] : _base.Host;
            var linkHost = h.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                           ? h[4..] : h;
            return linkHost.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
                   ? "internal" : "external";
        }
        catch { return "other"; }
    }

    private static bool IsGood(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.Length > 200)               return false;
        if (url.Contains("%20"))            return false;
        if (url.Contains("%2F%2F"))         return false;
        if (url.Contains("?"))
        {
            var q = url[(url.IndexOf('?'))..].ToLowerInvariant();
            if (new[]{"rel=","modalname=","promo","discount","offer","utm_",
                       "fbclid","gclid","_ga=","lp_","showcookies","date=","showtime"}
                .Any(b => q.Contains(b))) return false;
        }
        try
        {
            var uri  = new Uri(url);
            var path = uri.AbsolutePath.ToLowerInvariant();
            var ext  = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && BadExts.Contains(ext)) return false;
            foreach (var bad in BadPaths)
                if (path.Contains(bad, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        return true;
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
