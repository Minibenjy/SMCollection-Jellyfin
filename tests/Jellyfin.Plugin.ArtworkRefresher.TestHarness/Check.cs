namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

/// <summary>A tiny assertion helper: the harness is a console program, not a test framework.</summary>
internal static class Check
{
    private static int _passed;
    private static int _failed;

    public static int Failed => _failed;

    public static int Passed => _passed;

    public static void True(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            return;
        }

        _failed++;
        Console.WriteLine("FAIL: " + name);
    }

    public static void Equal<T>(T expected, T actual, string name)
        => True(EqualityComparer<T>.Default.Equals(expected, actual), name + " (expected " + expected + ", got " + actual + ")");

    public static async Task Throws<T>(Func<Task> action, string name)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            _passed++;
            return;
        }

        _failed++;
        Console.WriteLine("FAIL: " + name + " (no exception)");
    }
}
