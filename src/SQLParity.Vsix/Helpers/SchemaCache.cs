using System;
using System.Collections.Generic;
using SQLParity.Core.Model;

namespace SQLParity.Vsix.Helpers
{
    internal static class SchemaCache
    {
        private static readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private class CacheEntry
        {
            public DatabaseSchema Schema { get; set; }
            public DateTime CachedAtUtc { get; set; }
        }

        public static string MakeKey(string server, string database)
        {
            return (server ?? "").Trim() + "|" + (database ?? "").Trim();
        }

        /// <summary>
        /// Returns a cached schema if one exists and is within the TTL, otherwise null.
        /// </summary>
        public static DatabaseSchema Get(string server, string database, int ttlMinutes)
        {
            if (ttlMinutes <= 0) return null;

            var key = MakeKey(server, database);
            if (_cache.TryGetValue(key, out var entry))
            {
                if ((DateTime.UtcNow - entry.CachedAtUtc).TotalMinutes <= ttlMinutes)
                    return entry.Schema;
                else
                    _cache.Remove(key); // Expired
            }
            return null;
        }

        /// <summary>
        /// Stores a schema in the cache.
        /// </summary>
        public static void Put(string server, string database, DatabaseSchema schema)
        {
            var key = MakeKey(server, database);
            _cache[key] = new CacheEntry { Schema = schema, CachedAtUtc = DateTime.UtcNow };
        }

        /// <summary>
        /// Returns the age of a cached entry, or null if not cached.
        /// </summary>
        public static TimeSpan? GetAge(string server, string database)
        {
            var key = MakeKey(server, database);
            if (_cache.TryGetValue(key, out var entry))
                return DateTime.UtcNow - entry.CachedAtUtc;
            return null;
        }

        /// <summary>
        /// Clears a specific cache entry.
        /// </summary>
        public static void Invalidate(string server, string database)
        {
            var key = MakeKey(server, database);
            _cache.Remove(key);
        }

        /// <summary>
        /// Clears all cached schemas.
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
        }
    }
}
