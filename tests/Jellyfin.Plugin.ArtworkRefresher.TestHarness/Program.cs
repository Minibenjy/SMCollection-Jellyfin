using Jellyfin.Plugin.ArtworkRefresher.TestHarness;

// Runs every group. Exit code 1 when anything failed, so CI can use it.
await PolicyTests.RunAsync();
await NetworkTests.RunAsync();
await SourceParserTests.RunAsync();
await ImageTests.RunAsync();
await RotationTests.RunAsync();
await MetadataTests.RunAsync();

Console.WriteLine(Check.Passed + " checks passed, " + Check.Failed + " failed.");
return Check.Failed == 0 ? 0 : 1;
