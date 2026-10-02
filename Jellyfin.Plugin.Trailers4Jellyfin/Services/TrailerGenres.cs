using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Trailers4Jellyfin.Services;

internal static class TrailerGenres
{
    // Directional, one-hop relationships. Animation, Documentary, Music and TV Movie
    // add no related genres of their own; a movie's other genres supply the context.
    private static readonly Dictionary<string, string[]> Related = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = new[] { "Adventure", "Thriller" },
        ["Adventure"] = new[] { "Action", "Fantasy", "Science Fiction" },
        ["Comedy"] = new[] { "Romance", "Drama" },
        ["Crime"] = new[] { "Thriller", "Mystery" },
        ["Drama"] = new[] { "Romance", "History" },
        ["Family"] = new[] { "Animation", "Adventure", "Fantasy" },
        ["Fantasy"] = new[] { "Adventure", "Science Fiction" },
        ["History"] = new[] { "War", "Drama", "Documentary" },
        ["Horror"] = new[] { "Thriller", "Mystery" },
        ["Mystery"] = new[] { "Thriller", "Crime" },
        ["Romance"] = new[] { "Comedy", "Drama" },
        ["Science Fiction"] = new[] { "Adventure", "Fantasy", "Action" },
        ["Thriller"] = new[] { "Mystery", "Crime", "Action" },
        ["War"] = new[] { "History", "Action", "Drama" },
        ["Western"] = new[] { "Adventure", "Action", "Drama" },
    };

    internal static HashSet<string> Normalize(IEnumerable<string>? genres) =>
        (genres ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static HashSet<string> GetRelated(IEnumerable<string> genres) =>
        genres.SelectMany(g => Related.TryGetValue(g, out var values) ? values : Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
