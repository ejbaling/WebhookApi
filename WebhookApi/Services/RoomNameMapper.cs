using System;
using System.Collections.Generic;

namespace WebhookApi.Services;

public static class RoomNameMapper
{
    private static readonly Dictionary<string, string> Mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Rangiora", "Room 1" },
        { "Rimu", "Room 2" },
        { "Kauri", "Room 3" },
        { "Kowhai", "Room 4" }
    };

    /// <summary>
    /// Map a subject to a friendly room label if a known room name appears.
    /// Returns the original subject when no mapping matches.
    /// </summary>
    public static string MapSubject(string? subject)
    {
        if (!string.IsNullOrWhiteSpace(subject))
        {
            foreach (var kvp in Mappings)
            {
                if (subject.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }

        return subject ?? string.Empty;
    }
}
