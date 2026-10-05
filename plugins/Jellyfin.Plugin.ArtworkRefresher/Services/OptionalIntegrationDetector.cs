using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>
/// Notices which optional plugins are loaded, only to show them on the page and to turn on
/// adapters. The core never behaves differently because of one, and no type of another plugin is
/// ever referenced.
/// </summary>
public sealed class OptionalIntegrationDetector
{
    private static readonly (string Key, string AssemblyFragment)[] Known =
    [
        ("AutoThumbnails", "Jellyfin.Plugin.AutoThumbnails"),
        ("FileTransformation", ".FileTransformation"),
        ("JsInjector", "JavaScriptInjector"),
        ("DiscoverHome", "Jellyfin.Plugin.DiscoverHome"),
        ("HomeScreenSections", "HomeScreenSections"),
        ("CollectionSections", "CollectionSections"),
        ("KidsMode", "Jellyfin.Plugin.KidsMode"),
        ("MatureContent", "Jellyfin.Plugin.MatureContent"),
    ];

    /// <summary>
    /// Tells whether a known optional plugin is loaded.
    /// </summary>
    /// <param name="pluginKey">The key.</param>
    /// <returns>True when its assembly is loaded.</returns>
    public bool IsPluginAvailable(string pluginKey)
    {
        var fragment = Known.FirstOrDefault(k => string.Equals(k.Key, pluginKey, StringComparison.OrdinalIgnoreCase)).AssemblyFragment;
        if (fragment is null)
        {
            return false;
        }

        return AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .Any(a => a.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Lists every known key and whether it is loaded.
    /// </summary>
    /// <returns>The keys with their state.</returns>
    public IReadOnlyDictionary<string, bool> Detect()
        => Known.ToDictionary(k => k.Key, k => IsPluginAvailable(k.Key));
}
