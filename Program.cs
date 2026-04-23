using PlaywrightScraper;

string? url      = null;
string  output   = "scrape_result.json";
int     maxPages = 0;
int     workers  = 5;
int     delay    = 300;
int     saveEvery= 10;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--max-pages"  when i+1<args.Length: maxPages  = int.Parse(args[++i]); break;
        case "--workers"    when i+1<args.Length: workers   = int.Parse(args[++i]); break;
        case "--delay"      when i+1<args.Length: delay     = int.Parse(args[++i]); break;
        case "--output"     when i+1<args.Length: output    = args[++i];            break;
        case "--save-every" when i+1<args.Length: saveEvery = int.Parse(args[++i]); break;
        default: if (!args[i].StartsWith("--")) url = args[i]; break;
    }
}

if (string.IsNullOrWhiteSpace(url))
{
    Console.Write("Enter website URL: ");
    url = Console.ReadLine()?.Trim();
}
if (string.IsNullOrWhiteSpace(url)) { Console.Error.WriteLine("No URL."); return 1; }

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║   Universal Playwright Web Scraper   ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine($"  URL       : {url}");
Console.WriteLine($"  Workers   : {workers}");
Console.WriteLine($"  Max pages : {(maxPages==0?"unlimited":maxPages.ToString())}");
Console.WriteLine($"  Delay     : {delay}ms");
Console.WriteLine($"  Save every: {saveEvery} pages");
Console.WriteLine($"  Output    : {output}\n");

try
{
    var json = await new WebScraper(new ScraperOptions
    {
        Workers    = workers,
        MaxPages   = maxPages,
        DelayMs    = delay,
        OutputFile = output,
        SaveEvery  = saveEvery
    }).ScrapeAsync(url);

    var doc  = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;
    Console.WriteLine($"\n  Pages  : {root.GetProperty("total_pages_scraped").GetInt32()}");
    Console.WriteLine($"  Failed : {root.GetProperty("failed_urls").GetArrayLength()}");
    Console.WriteLine($"  File   : {output}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    return 1;
}
