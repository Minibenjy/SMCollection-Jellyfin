using Jellyfin.Plugin.AutoThumbnails.Configuration;
using Jellyfin.Plugin.AutoThumbnails.Extraction;
using Microsoft.Extensions.Logging;

using var factory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var logger = factory.CreateLogger("test");
var extractor = new CoverExtractor(logger);
var config = new PluginConfiguration();
var outDir = args[0];
Directory.CreateDirectory(outDir);

int ok = 0, fail = 0;
foreach (var path in args.Skip(1))
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var cover = extractor.Extract(path, config);
    sw.Stop();
    if (cover is null)
    {
        fail++;
        Console.WriteLine($"FAIL  {Path.GetFileName(path)}");
        continue;
    }

    ok++;
    var name = Path.GetFileNameWithoutExtension(path) + ImageFormats.ToExtension(cover.Format);
    var dest = Path.Combine(outDir, name);
    File.WriteAllBytes(dest, cover.Data);
    Console.WriteLine($"OK    {Path.GetFileName(path)} -> {cover.Format} {cover.Data.Length / 1024}KB [{cover.Source}] {sw.ElapsedMilliseconds}ms");
}
Console.WriteLine($"\n== {ok} ok, {fail} fail ==");
