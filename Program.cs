using PlaywrightScraper;

string? url       = null;
string  output    = "scrape_result.json";
int     maxPages  = 0;
int     workers   = 5;
int     delay     = 200;
int     saveEvery = 20;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--max-pages"  when i+1 < args.Length: maxPages  = int.Parse(args[++i]); break;
        case "--workers"    when i+1 < args.Length: workers   = int.Parse(args[++i]); break;
        case "--delay"      when i+1 < args.Length: delay     = int.Parse(args[++i]); break;
        case "--output"     when i+1 < args.Length: output    = args[++i];            break;
        case "--save-every" when i+1 < args.Length: saveEvery = int.Parse(args[++i]); break;
        default: if (!args[i].StartsWith("--")) url = args[i]; break;
    }
}

if (string.IsNullOrWhiteSpace(url))
{
    Console.Write("Enter website URL: ");
    url = Console.ReadLine()?.Trim();
}

if (string.IsNullOrWhiteSpace(url))
{
    Console.Error.WriteLine("No URL provided. Exiting.");
    return 1;
}

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║     Fast Parallel Playwright Scraper     ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine($"  URL        : {url}");
Console.WriteLine($"  Workers    : {workers}  (parallel tabs)");
Console.WriteLine($"  Max pages  : {(maxPages == 0 ? "UNLIMITED" : maxPages.ToString())}");
Console.WriteLine($"  Delay      : {delay}ms per worker");
Console.WriteLine($"  Save every : {saveEvery} pages");
Console.WriteLine($"  Output     : {output}\n");

var options = new ScraperOptions
{
    Workers                = workers,
    MaxPages               = maxPages,
    DelayBetweenRequestsMs = delay,
    OutputFile             = output,
    SaveProgressEvery      = saveEvery
};

try
{
    var json = await new WebScraper(options).ScrapeWebsiteAsync(url);

    using var doc  = System.Text.Json.JsonDocument.Parse(json);
    var       root = doc.RootElement;

    Console.WriteLine("\n╔══════════════════════════════════════════╗");
    Console.WriteLine("║                 SUMMARY                  ║");
    Console.WriteLine("╚══════════════════════════════════════════╝");
    Console.WriteLine($"  Pages scraped : {root.GetProperty("total_pages_scraped").GetInt32()}");
    Console.WriteLine($"  Failed URLs   : {root.GetProperty("failed_urls").GetArrayLength()}");
    Console.WriteLine($"  Started       : {root.GetProperty("scrape_started_at").GetString()}");
    Console.WriteLine($"  Finished      : {root.GetProperty("scrape_finished_at").GetString()}");
    Console.WriteLine($"  Saved to      : {output}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nFatal error: {ex.Message}");
    return 1;
}