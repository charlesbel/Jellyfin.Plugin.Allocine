using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2007
#pragma warning disable CA1849

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Owns the plugin's private SQLite rating cache.
    /// </summary>
    public sealed class AllocineRatingStore : IDisposable
    {
        private const int CurrentSchemaVersion = 5;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string _databasePath;
        private readonly ILogger<AllocineRatingStore> _logger;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private bool _initialized;
        private int _generation;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineRatingStore"/> class.
        /// </summary>
        /// <param name="applicationPaths">Jellyfin application paths.</param>
        /// <param name="logger">The logger.</param>
        public AllocineRatingStore(IApplicationPaths applicationPaths, ILogger<AllocineRatingStore> logger)
            : this(
                Path.Combine(
                    applicationPaths.PluginConfigurationsPath,
                    "Jellyfin.Plugin.Allocine",
                    "allocine-ratings.db"),
                logger)
        {
        }

        internal AllocineRatingStore(string databasePath, ILogger<AllocineRatingStore> logger)
        {
            _databasePath = databasePath;
            _logger = logger;
        }

        internal async Task<AllocineCacheEntry?> ReadAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                return await ReadCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                return await ReadCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsMalformedEntry(ex))
            {
                _logger.LogWarning(ex, "[Allocine] Deleted malformed private cache row for item {ItemId}", request.ItemId);
                await DeleteCoreAsync(request, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        internal async Task WriteAsync(
            AllocineRatingsRequest request,
            Dictionary<string, string>? ratings,
            DateTimeOffset fetchedAt,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                await WriteCoreAsync(request, ratings, fetchedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                await WriteCoreAsync(request, ratings, fetchedAt, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task RecordFailureAsync(
            AllocineRatingsRequest request,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                await RecordFailureCoreAsync(request, attemptedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                await RecordFailureCoreAsync(request, attemptedAt, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<AllocineMappingEntry?> ReadMappingAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                return await ReadMappingCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                return await ReadMappingCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task WriteMappingAsync(
            AllocineRatingsRequest request,
            string allocineId,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken)
        {
            await WriteMappingAsync(
                request,
                allocineId,
                resolvedAt,
                AllocineResolutionSource.ExactIdentifiers,
                cancellationToken).ConfigureAwait(false);
        }

        internal async Task WriteMappingAsync(
            AllocineRatingsRequest request,
            string allocineId,
            DateTimeOffset resolvedAt,
            AllocineResolutionSource source,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                await WriteMappingCoreAsync(request, allocineId, resolvedAt, source, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                await WriteMappingCoreAsync(request, allocineId, resolvedAt, source, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<AllocineNativeWriteEntry?> ReadNativeWriteAsync(
            string jellyfinItemId,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                return await ReadNativeWriteCoreAsync(jellyfinItemId, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                return await ReadNativeWriteCoreAsync(jellyfinItemId, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task RecordNativeWriteAsync(
            string jellyfinItemId,
            string allocineId,
            string identityKey,
            DateTimeOffset writtenAt,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            int generation = Volatile.Read(ref _generation);
            try
            {
                await RecordNativeWriteCoreAsync(jellyfinItemId, allocineId, identityKey, writtenAt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsCorruption(ex))
            {
                await RecoverFromCorruptionAsync(generation, ex, cancellationToken).ConfigureAwait(false);
                await RecordNativeWriteCoreAsync(jellyfinItemId, allocineId, identityKey, writtenAt, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static string IdentityKey(AllocineRatingsRequest request)
        {
            string mediaType = request.MediaType.Trim().ToUpperInvariant();
            string imdbId = request.ImdbId?.Trim().ToLowerInvariant() ?? string.Empty;
            string tmdbId = request.TmdbId?.Trim() ?? string.Empty;
            if (imdbId.Length > 0 || tmdbId.Length > 0)
            {
                return $"{mediaType}|imdb:{imdbId}|tmdb:{tmdbId}";
            }

            return $"{mediaType}|title:{request.Title.Trim().ToUpperInvariant()}|original:{request.OriginalTitle?.Trim().ToUpperInvariant()}|year:{request.Year.ToString(CultureInfo.InvariantCulture)}";
        }

        internal static string CacheKey(AllocineRatingsRequest request)
        {
            return string.IsNullOrWhiteSpace(request.ItemId)
                ? IdentityKey(request)
                : $"jellyfin:{request.ItemId.Trim().ToLowerInvariant()}";
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _initializationGate.Dispose();
        }

        private static bool IsCorruption(SqliteException exception)
        {
            return exception.SqliteErrorCode is 11 or 26;
        }

        private static bool IsMalformedEntry(Exception exception)
        {
            return exception is JsonException or FormatException or InvalidCastException;
        }

        private static object DbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        }

        private SqliteConnection CreateConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 15,
            };
            return new SqliteConnection(builder.ToString());
        }

        private async Task<AllocineMappingEntry?> ReadMappingCoreAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT allocine_id, resolved_utc, source
                FROM mappings
                WHERE identity_key = $identity_key;
                """;
            string identityKey = IdentityKey(request);
            command.Parameters.AddWithValue("$identity_key", identityKey);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            AllocineResolutionSource source = Enum.TryParse(reader.GetString(2), ignoreCase: false, out AllocineResolutionSource parsed)
                ? parsed
                : AllocineResolutionSource.Unknown;
            return new AllocineMappingEntry(
                identityKey,
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                source);
        }

        private async Task WriteMappingCoreAsync(
            AllocineRatingsRequest request,
            string allocineId,
            DateTimeOffset resolvedAt,
            AllocineResolutionSource source,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mappings (identity_key, media_type, imdb_id, tmdb_id, allocine_id, resolved_utc, source)
                VALUES ($identity_key, $media_type, $imdb_id, $tmdb_id, $allocine_id, $resolved_utc, $source)
                ON CONFLICT(identity_key) DO UPDATE SET
                    media_type = excluded.media_type,
                    imdb_id = excluded.imdb_id,
                    tmdb_id = excluded.tmdb_id,
                    allocine_id = excluded.allocine_id,
                    resolved_utc = excluded.resolved_utc,
                    source = excluded.source;
                """;
            command.Parameters.AddWithValue("$identity_key", IdentityKey(request));
            command.Parameters.AddWithValue("$media_type", request.MediaType);
            command.Parameters.AddWithValue("$imdb_id", DbValue(request.ImdbId));
            command.Parameters.AddWithValue("$tmdb_id", DbValue(request.TmdbId));
            command.Parameters.AddWithValue("$allocine_id", allocineId);
            command.Parameters.AddWithValue("$resolved_utc", resolvedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$source", source.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<AllocineNativeWriteEntry?> ReadNativeWriteCoreAsync(
            string jellyfinItemId,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT jellyfin_item_id, allocine_id, identity_key, written_utc
                FROM native_writes
                WHERE jellyfin_item_id = $item_id;
                """;
            command.Parameters.AddWithValue("$item_id", jellyfinItemId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new AllocineNativeWriteEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }

        private async Task RecordNativeWriteCoreAsync(
            string jellyfinItemId,
            string allocineId,
            string identityKey,
            DateTimeOffset writtenAt,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO native_writes (jellyfin_item_id, allocine_id, identity_key, written_utc)
                VALUES ($item_id, $allocine_id, $identity_key, $written_utc)
                ON CONFLICT(jellyfin_item_id) DO UPDATE SET
                    allocine_id = excluded.allocine_id,
                    identity_key = excluded.identity_key,
                    written_utc = excluded.written_utc;
                """;
            command.Parameters.AddWithValue("$item_id", jellyfinItemId);
            command.Parameters.AddWithValue("$allocine_id", allocineId);
            command.Parameters.AddWithValue("$identity_key", identityKey);
            command.Parameters.AddWithValue("$written_utc", writtenAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<AllocineCacheEntry?> ReadCoreAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT identity_key, found, ratings_json, fetched_utc,
                       last_attempt_utc, consecutive_failures, allocine_id
                FROM ratings
                WHERE item_key = $item_key;
                """;
            command.Parameters.AddWithValue("$item_key", CacheKey(request));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            Dictionary<string, string>? ratings = null;
            if (!reader.IsDBNull(2))
            {
                ratings = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2), JsonOptions);
            }

            return new AllocineCacheEntry(
                reader.GetString(0),
                reader.GetBoolean(1),
                ratings,
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        private async Task DeleteCoreAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ratings WHERE item_key = $item_key;";
            command.Parameters.AddWithValue("$item_key", CacheKey(request));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteCoreAsync(
            AllocineRatingsRequest request,
            Dictionary<string, string>? ratings,
            DateTimeOffset fetchedAt,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ratings (
                    item_key, identity_key, media_type, jellyfin_item_id, imdb_id, tmdb_id,
                    title, original_title, production_year, found, ratings_json, fetched_utc,
                    last_attempt_utc, consecutive_failures, allocine_id)
                VALUES (
                    $item_key, $identity_key, $media_type, $jellyfin_item_id, $imdb_id, $tmdb_id,
                    $title, $original_title, $production_year, $found, $ratings_json, $fetched_utc,
                    $fetched_utc, 0, $allocine_id)
                ON CONFLICT(item_key) DO UPDATE SET
                    identity_key = excluded.identity_key,
                    media_type = excluded.media_type,
                    jellyfin_item_id = excluded.jellyfin_item_id,
                    imdb_id = excluded.imdb_id,
                    tmdb_id = excluded.tmdb_id,
                    title = excluded.title,
                    original_title = excluded.original_title,
                    production_year = excluded.production_year,
                    found = excluded.found,
                    ratings_json = excluded.ratings_json,
                    fetched_utc = excluded.fetched_utc,
                    last_attempt_utc = excluded.last_attempt_utc,
                    consecutive_failures = 0,
                    allocine_id = excluded.allocine_id;
                """;
            command.Parameters.AddWithValue("$item_key", CacheKey(request));
            command.Parameters.AddWithValue("$identity_key", IdentityKey(request));
            command.Parameters.AddWithValue("$media_type", request.MediaType);
            command.Parameters.AddWithValue("$jellyfin_item_id", DbValue(request.ItemId));
            command.Parameters.AddWithValue("$imdb_id", DbValue(request.ImdbId));
            command.Parameters.AddWithValue("$tmdb_id", DbValue(request.TmdbId));
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$original_title", DbValue(request.OriginalTitle));
            command.Parameters.AddWithValue("$production_year", request.Year);
            command.Parameters.AddWithValue("$found", ratings is { Count: > 0 });
            command.Parameters.AddWithValue("$ratings_json", ratings is { Count: > 0 }
                ? JsonSerializer.Serialize(ratings, JsonOptions)
                : DBNull.Value);
            command.Parameters.AddWithValue("$fetched_utc", fetchedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$allocine_id", DbValue(request.AllocineId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordFailureCoreAsync(
            AllocineRatingsRequest request,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ratings (
                    item_key, identity_key, media_type, jellyfin_item_id, imdb_id, tmdb_id,
                    title, original_title, production_year, found, ratings_json, fetched_utc,
                    last_attempt_utc, consecutive_failures, allocine_id)
                VALUES (
                    $item_key, $identity_key, $media_type, $jellyfin_item_id, $imdb_id, $tmdb_id,
                    $title, $original_title, $production_year, 0, NULL, $attempted_utc,
                    $attempted_utc, 1, $allocine_id)
                ON CONFLICT(item_key) DO UPDATE SET
                    identity_key = excluded.identity_key,
                    media_type = excluded.media_type,
                    jellyfin_item_id = excluded.jellyfin_item_id,
                    imdb_id = excluded.imdb_id,
                    tmdb_id = excluded.tmdb_id,
                    title = excluded.title,
                    original_title = excluded.original_title,
                    production_year = excluded.production_year,
                    found = CASE WHEN ratings.identity_key = excluded.identity_key THEN ratings.found ELSE 0 END,
                    ratings_json = CASE WHEN ratings.identity_key = excluded.identity_key THEN ratings.ratings_json ELSE NULL END,
                    fetched_utc = CASE WHEN ratings.identity_key = excluded.identity_key THEN ratings.fetched_utc ELSE excluded.fetched_utc END,
                    last_attempt_utc = excluded.last_attempt_utc,
                    consecutive_failures = CASE
                        WHEN ratings.identity_key = excluded.identity_key THEN ratings.consecutive_failures + 1
                        ELSE 1
                    END,
                    allocine_id = CASE
                        WHEN ratings.identity_key = excluded.identity_key THEN ratings.allocine_id
                        ELSE excluded.allocine_id
                    END;
                """;
            command.Parameters.AddWithValue("$item_key", CacheKey(request));
            command.Parameters.AddWithValue("$identity_key", IdentityKey(request));
            command.Parameters.AddWithValue("$media_type", request.MediaType);
            command.Parameters.AddWithValue("$jellyfin_item_id", DbValue(request.ItemId));
            command.Parameters.AddWithValue("$imdb_id", DbValue(request.ImdbId));
            command.Parameters.AddWithValue("$tmdb_id", DbValue(request.TmdbId));
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$original_title", DbValue(request.OriginalTitle));
            command.Parameters.AddWithValue("$production_year", request.Year);
            command.Parameters.AddWithValue("$attempted_utc", attemptedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$allocine_id", DbValue(request.AllocineId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return;
            }

            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                try
                {
                    await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (IsCorruption(ex))
                {
                    QuarantineCorruptDatabase(ex);
                    await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
                }

                _initialized = true;
                Interlocked.Increment(ref _generation);
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task RecoverFromCorruptionAsync(
            int expectedGeneration,
            SqliteException exception,
            CancellationToken cancellationToken)
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_generation != expectedGeneration)
                {
                    return;
                }

                _initialized = false;
                QuarantineCorruptDatabase(exception);
                await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
                Interlocked.Increment(ref _generation);
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA busy_timeout=15000;
                    """;
                await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            int schemaVersion;
            await using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "PRAGMA user_version;";
                object? value = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                schemaVersion = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (schemaVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The AlloCiné rating database schema v{schemaVersion.ToString(CultureInfo.InvariantCulture)} is newer than supported v{CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (schemaVersion == CurrentSchemaVersion)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var migration = connection.CreateCommand();
            migration.Transaction = (SqliteTransaction)transaction;
            if (schemaVersion == 0)
            {
                migration.CommandText = """
                    CREATE TABLE IF NOT EXISTS ratings (
                        item_key TEXT PRIMARY KEY NOT NULL,
                        identity_key TEXT NOT NULL,
                        media_type TEXT NOT NULL,
                        jellyfin_item_id TEXT NULL,
                        imdb_id TEXT NULL,
                        tmdb_id TEXT NULL,
                        title TEXT NOT NULL,
                        original_title TEXT NULL,
                        production_year INTEGER NOT NULL,
                        found INTEGER NOT NULL,
                        ratings_json TEXT NULL,
                        fetched_utc TEXT NOT NULL,
                        last_attempt_utc TEXT NULL,
                        consecutive_failures INTEGER NOT NULL DEFAULT 0,
                        allocine_id TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_ratings_identity_key ON ratings(identity_key);
                    CREATE TABLE IF NOT EXISTS mappings (
                        identity_key TEXT PRIMARY KEY NOT NULL,
                        media_type TEXT NOT NULL,
                        imdb_id TEXT NULL,
                        tmdb_id TEXT NULL,
                        allocine_id TEXT NOT NULL,
                        resolved_utc TEXT NOT NULL,
                        source TEXT NOT NULL DEFAULT 'ExactIdentifiers'
                    );
                    CREATE TABLE IF NOT EXISTS native_writes (
                        jellyfin_item_id TEXT PRIMARY KEY NOT NULL,
                        allocine_id TEXT NOT NULL,
                        identity_key TEXT NOT NULL,
                        written_utc TEXT NOT NULL
                    );
                    PRAGMA user_version=5;
                    """;
            }
            else
            {
                if (schemaVersion == 1)
                {
                    migration.CommandText = """
                        ALTER TABLE ratings ADD COLUMN last_attempt_utc TEXT NULL;
                        ALTER TABLE ratings ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
                        CREATE TABLE IF NOT EXISTS mappings (
                            identity_key TEXT PRIMARY KEY NOT NULL,
                            media_type TEXT NOT NULL,
                            imdb_id TEXT NULL,
                            tmdb_id TEXT NULL,
                            allocine_id TEXT NOT NULL,
                            resolved_utc TEXT NOT NULL
                        );
                        DELETE FROM ratings WHERE found=0 AND ratings_json IS NULL;
                        """;
                }
                else if (schemaVersion == 2)
                {
                    migration.CommandText = """
                        CREATE TABLE IF NOT EXISTS mappings (
                            identity_key TEXT PRIMARY KEY NOT NULL,
                            media_type TEXT NOT NULL,
                            imdb_id TEXT NULL,
                            tmdb_id TEXT NULL,
                            allocine_id TEXT NOT NULL,
                            resolved_utc TEXT NOT NULL
                        );
                        DELETE FROM ratings WHERE found=0 AND ratings_json IS NULL;
                        """;
                }
                else if (schemaVersion == 3)
                {
                    migration.CommandText = """
                        DELETE FROM ratings WHERE found=0 AND ratings_json IS NULL;
                        """;
                }
                else
                {
                    migration.CommandText = "SELECT 1;";
                }

                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await EnsureRatingsAllocineIdColumnAsync(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
                await EnsureMappingsSourceColumnAsync(connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);
                await using var schemaFive = connection.CreateCommand();
                schemaFive.Transaction = (SqliteTransaction)transaction;
                schemaFive.CommandText = """
                    UPDATE ratings
                    SET allocine_id = (
                        SELECT m.allocine_id FROM mappings m WHERE m.identity_key = ratings.identity_key
                    )
                    WHERE allocine_id IS NULL;
                    UPDATE mappings
                    SET source = 'TitleYear'
                    WHERE source IN ('Unknown', 'ExactIdentifiers')
                      AND (imdb_id IS NULL OR trim(imdb_id) = '')
                      AND (tmdb_id IS NULL OR trim(tmdb_id) = '');
                    CREATE TABLE IF NOT EXISTS native_writes (
                        jellyfin_item_id TEXT PRIMARY KEY NOT NULL,
                        allocine_id TEXT NOT NULL,
                        identity_key TEXT NOT NULL,
                        written_utc TEXT NOT NULL
                    );
                    PRAGMA user_version=5;
                    """;
                await schemaFive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureRatingsAllocineIdColumnAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (await RatingsHasAllocineIdColumnAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE ratings ADD COLUMN allocine_id TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureMappingsSourceColumnAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            if (await MappingsHasSourceColumnAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE mappings ADD COLUMN source TEXT NOT NULL DEFAULT 'Unknown';";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> RatingsHasAllocineIdColumnAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var info = connection.CreateCommand();
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(ratings);";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "allocine_id", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> MappingsHasSourceColumnAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            await using var info = connection.CreateCommand();
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(mappings);";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "source", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void QuarantineCorruptDatabase(SqliteException exception)
        {
            using (SqliteConnection connection = CreateConnection())
            {
                SqliteConnection.ClearPool(connection);
            }

            string suffix = $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + suffix, overwrite: false);
                }
            }

            _logger.LogError(exception, "[Allocine] Quarantined corrupt private rating database {DatabasePath}", _databasePath);
        }
    }
}

#pragma warning restore CA1849
#pragma warning restore CA2007
