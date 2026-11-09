using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.PublicationCrashHarness;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// Process-loss qualification, not power-loss or hostile-filesystem qualification.
/// The parent kills a real paused publisher; no exception or child self-kill stands in
/// for abruptly losing SQLite connections and the publisher's finally block.
/// </summary>
public sealed class SnapshotProcessLossTests
{
    private const string ConfidentialSentinel = "CONFIDENTIAL-unrelated-table-must-not-be-retained";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> CrashCases()
    {
        foreach (var journalMode in new[] { "DELETE", "WAL" })
        foreach (var boundary in new[]
        {
            SnapshotCaptureCrashContract.StagingCreated,
            SnapshotCaptureCrashContract.MidCopy,
            SnapshotCaptureCrashContract.AfterCopy,
            SnapshotCaptureCrashContract.MetadataCreated,
            SnapshotCaptureCrashContract.MetadataFlushed,
            SnapshotCaptureCrashContract.BeforePublish,
            SnapshotCaptureCrashContract.AfterPublish
        }) yield return new object[] { journalMode, boundary };
    }

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task ParentKillsPublisher_OnlyAtomicRenameExposesAcceptedBundle(string journalMode, string boundary)
    {
        using var root = new TemporaryDirectory();
        await using var fixture = await SourceFixture.CreateAsync(root.Path, "source", journalMode);
        var destination = Path.Combine(root.Path, "retained");
        await using var child = HarnessProcess.Start(root.Path, "crashed", fixture.Source.Path!, destination, boundary);
        var ready = await child.WaitUntilReadyAsync();
        Assert.Equal(boundary, ready.Boundary);
        Assert.True(Directory.Exists(ready.Path));
        AssertPrivateDirectory(ready.Path);
        if (boundary == SnapshotCaptureCrashContract.AfterPublish)
            Assert.Equal(destination, ready.Path);
        else
        {
            Assert.Equal(root.Path, Path.GetDirectoryName(ready.Path));
            Assert.StartsWith(".xms-snapshot-", Path.GetFileName(ready.Path));
            Assert.False(Directory.Exists(destination));
        }

        var terminated = await child.KillAndWaitAsync();
        Assert.NotEqual(0, terminated.ExitCode);
        AssertAllHandlesClosed(ready.Path);
        await fixture.AssertUnchangedAsync();

        if (boundary == SnapshotCaptureCrashContract.AfterPublish)
        {
            Assert.Empty(Directory.GetDirectories(root.Path, ".xms-snapshot-*"));
            await AssertCompleteBundleAsync(destination, fixture);
            var accepted = ReadTree(destination);
            await Assert.ThrowsAsync<IOException>(() =>
                SqliteSourceSnapshotStore.CaptureAsync(fixture.Source, destination, default));
            AssertTreeUnchanged(destination, accepted);
            Assert.Empty(Directory.GetDirectories(root.Path, ".xms-snapshot-*"));
        }
        else
        {
            Assert.False(Directory.Exists(destination));
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(Path.Combine(destination, "snapshot.json")));
            var error = await Assert.ThrowsAsync<AdapterException>(() =>
                SqliteSourceSnapshotStore.OpenAsync(Path.Combine(destination, "snapshot.json"), fixture.Source, default));
            Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
            Assert.Equal(ready.Path, Assert.Single(Directory.GetDirectories(root.Path, ".xms-snapshot-*")));
            if (boundary == SnapshotCaptureCrashContract.MetadataCreated)
                Assert.Equal(0L, new FileInfo(Path.Combine(ready.Path, "snapshot.json")).Length);

            // A flushed orphan may be valid when explicitly opened. The contract is
            // that it was not published at the final destination, not that all orphan
            // bytes are invalid or that a later capture automatically deletes them.
            var orphan = ReadTree(ready.Path);
            await CaptureInFreshProcessAsync(root.Path, fixture.Source.Path!, destination);
            await AssertCompleteBundleAsync(destination, fixture);
            AssertTreeUnchanged(ready.Path, orphan);
            AssertPrivateDirectory(ready.Path);
            Assert.Equal(ready.Path, Assert.Single(Directory.GetDirectories(root.Path, ".xms-snapshot-*")));
        }

        await fixture.AssertUnchangedAsync();
        await fixture.DeleteLiveSourceAsync();
        Assert.False(File.Exists(fixture.Source.Path));
        await AssertCompleteBundleAsync(destination, fixture);
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("WAL")]
    public async Task DestinationAppearsBeforeRename_LoserPreservesAcceptedWinnerAndUnrelatedOrphan(string journalMode)
    {
        using var root = new TemporaryDirectory();
        await using var candidate = await SourceFixture.CreateAsync(root.Path, "candidate", journalMode);
        await using var winner = await SourceFixture.CreateAsync(root.Path, "winner", journalMode);
        var destination = Path.Combine(root.Path, "retained");
        var unrelated = Path.Combine(root.Path, ".xms-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(unrelated, "retained-orphan-evidence"), "do not sweep another capture's files");
        var unrelatedBefore = ReadTree(unrelated);
        await using var child = HarnessProcess.Start(root.Path, "candidate", candidate.Source.Path!, destination,
            SnapshotCaptureCrashContract.BeforePublish);
        var ready = await child.WaitUntilReadyAsync();
        Assert.Equal(SnapshotCaptureCrashContract.BeforePublish, ready.Boundary);
        Assert.True(Directory.Exists(ready.Path));
        Assert.False(Directory.Exists(destination));
        AssertPrivateDirectory(ready.Path);

        // This is a real nonempty, fully verified competing bundle, not a placeholder.
        await SqliteSourceSnapshotStore.CaptureAsync(winner.Source, destination, default);
        await AssertCompleteBundleAsync(destination, winner);
        var accepted = ReadTree(destination);
        await child.ReleaseAsync();
        var result = await child.WaitForExitAsync();
        Assert.True(result.ExitCode != 0, result.Describe());
        Assert.Contains("IOException", result.StandardError);
        Assert.False(Directory.Exists(ready.Path));
        AssertTreeUnchanged(destination, accepted);
        AssertTreeUnchanged(unrelated, unrelatedBefore);
        Assert.Equal(unrelated, Assert.Single(Directory.GetDirectories(root.Path, ".xms-snapshot-*")));
        await AssertCompleteBundleAsync(destination, winner);
        await candidate.AssertUnchangedAsync();
        await winner.AssertUnchangedAsync();
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("WAL")]
    public async Task SimultaneousPublishers_OneCompleteWinnerAndNoOverwrite(string journalMode)
    {
        using var root = new TemporaryDirectory();
        await using var first = await SourceFixture.CreateAsync(root.Path, "first", journalMode);
        await using var second = await SourceFixture.CreateAsync(root.Path, "second", journalMode);
        Assert.NotEqual(first.RawRows, second.RawRows);
        var destination = Path.Combine(root.Path, "retained");
        var release = Path.Combine(root.Path, "both.release");
        await using var firstChild = HarnessProcess.Start(root.Path, "first", first.Source.Path!, destination,
            SnapshotCaptureCrashContract.BeforePublish, release);
        await using var secondChild = HarnessProcess.Start(root.Path, "second", second.Source.Path!, destination,
            SnapshotCaptureCrashContract.BeforePublish, release);
        var ready = await Task.WhenAll(firstChild.WaitUntilReadyAsync(), secondChild.WaitUntilReadyAsync());
        Assert.All(ready, signal => Assert.Equal(SnapshotCaptureCrashContract.BeforePublish, signal.Boundary));
        Assert.NotEqual(ready[0].Path, ready[1].Path);
        Assert.All(ready, signal => AssertPrivateDirectory(signal.Path));
        Assert.False(Directory.Exists(destination));

        // One shared release file puts both real processes beyond the same barrier.
        await File.WriteAllTextAsync(release, "release both publishers");
        var results = await Task.WhenAll(firstChild.WaitForExitAsync(), secondChild.WaitForExitAsync());
        var successfulIndex = Assert.Single(Enumerable.Range(0, results.Length), index => results[index].ExitCode == 0);
        var failedIndex = 1 - successfulIndex;
        Assert.True(results[failedIndex].ExitCode != 0, results[failedIndex].Describe());
        Assert.Contains("IOException", results[failedIndex].StandardError);
        var winner = successfulIndex == 0 ? first : second;
        var loser = successfulIndex == 0 ? second : first;
        Assert.All(ready, signal => Assert.False(Directory.Exists(signal.Path)));
        Assert.Empty(Directory.GetDirectories(root.Path, ".xms-snapshot-*"));
        AssertAllHandlesClosed(destination);
        await AssertCompleteBundleAsync(destination, winner);
        var accepted = ReadTree(destination);
        await Assert.ThrowsAsync<IOException>(() =>
            SqliteSourceSnapshotStore.CaptureAsync(loser.Source, destination, default));
        AssertTreeUnchanged(destination, accepted);
        await first.AssertUnchangedAsync();
        await second.AssertUnchangedAsync();
    }

    private static async Task CaptureInFreshProcessAsync(string root, string source, string destination)
    {
        await using var retry = HarnessProcess.Start(root, "retry", source, destination, SnapshotCaptureCrashContract.AfterPublish);
        var ready = await retry.WaitUntilReadyAsync();
        Assert.Equal(SnapshotCaptureCrashContract.AfterPublish, ready.Boundary);
        Assert.Equal(destination, ready.Path);
        await retry.ReleaseAsync();
        var result = await retry.WaitForExitAsync();
        Assert.True(result.ExitCode == 0, result.Describe());
        AssertAllHandlesClosed(destination);
    }

    private static async Task AssertCompleteBundleAsync(string destination, SourceFixture fixture)
    {
        Assert.True(Directory.Exists(destination));
        Assert.Equal(new[] { "snapshot.json", "source.sqlite" },
            Directory.GetFiles(destination).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(destination));
        AssertPrivateDirectory(destination);
        var metadataPath = Path.Combine(destination, "snapshot.json");
        var databasePath = Path.Combine(destination, "source.sqlite");
        var metadata = JsonSerializer.Deserialize<SqliteSourceSnapshotStore.SnapshotMetadata>(
            await File.ReadAllBytesAsync(metadataPath), JsonOptions);
        Assert.NotNull(metadata);
        Assert.Equal(2, metadata.Version);
        Assert.Equal(SnapshotCaptureCrashContract.SourceId, metadata.SourceId);
        Assert.Equal(fixture.Source.Path, metadata.OriginalSourcePath);
        Assert.Equal(SqliteSourceSnapshotStore.Strategy, metadata.Strategy);
        Assert.Equal(SqliteSourceSnapshotStore.SelectedTableScope, metadata.Scope);
        Assert.Equal(fixture.SchemaFingerprint, metadata.SchemaFingerprint);
        var bytes = await File.ReadAllBytesAsync(databasePath);
        Assert.Equal(bytes.LongLength, metadata.DatabaseBytes);
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), metadata.DatabaseDigest);
        Assert.DoesNotContain(ConfidentialSentinel, Encoding.UTF8.GetString(bytes));
        Assert.Equal(fixture.SelectedSchema, await SchemaAsync(databasePath, selectedOnly: false));
        Assert.Equal(fixture.RawRows, await RawRowsAsync(databasePath));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) Assert.False(File.Exists(databasePath + suffix));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(databasePath));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(metadataPath));
        }

        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadataPath, fixture.Source, default);
        Assert.Equal(metadata.DatabaseDigest, replay.RetainedSnapshot!.DatabaseDigest);
        Assert.Equal(SqliteSourceSnapshotStore.Strategy, replay.RetainedSnapshot.SnapshotStrategy);
        await SqliteSourceSnapshotStore.VerifyConnectionAsync(replay, default);
        AssertRowsEqual(fixture.Rows, await RowsAsync(replay));
    }

    private static void AssertRowsEqual(IReadOnlyList<DataRecord> expected, IReadOnlyList<DataRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].RowId, actual[index].RowId);
            Assert.Equal(expected[index].HasStableRowIdentity, actual[index].HasStableRowIdentity);
            Assert.Equal(expected[index].Fields.Keys, actual[index].Fields.Keys);
            foreach (var field in expected[index].Fields.Keys)
            {
                Assert.Equal(expected[index].Fields[field], actual[index].Fields[field]);
                Assert.Equal(expected[index].Fields[field].LexicalSource, actual[index].Fields[field].LexicalSource);
                Assert.Equal(expected[index].Fields[field].Source, actual[index].Fields[field].Source);
                Assert.Equal(expected[index].CellSources![field], actual[index].CellSources![field]);
            }
        }
    }

    private static void AssertPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            Assert.NotNull(user);
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.NotEmpty(rules);
            foreach (var rule in rules)
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(user.Value, rule.IdentityReference.Value);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            }
        }
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
    }

    private static void AssertAllHandlesClosed(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.CanRead);
        }
    }

    private static Dictionary<string, byte[]> ReadTree(string directory)
        => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertTreeUnchanged(string directory, IReadOnlyDictionary<string, byte[]> expected)
    {
        Assert.True(Directory.Exists(directory));
        var actual = ReadTree(directory);
        Assert.Equal(expected.Keys.OrderBy(name => name, StringComparer.Ordinal),
            actual.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var name in expected.Keys) Assert.Equal(expected[name], actual[name]);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
        => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());

    private static async Task<byte[]> ReadLiveBytesAsync(string path)
    {
        // Windows must allow the fixture's existing SQLite writer handle. No writer
        // mutates the fixture while these comparisons run; only the capture reads it.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(bytes.LongLength, stream.Length);
        return bytes;
    }

    private static async Task<List<string>> RawRowsAsync(string path)
    {
        await using var db = Open(path);
        await db.OpenAsync();
        using var query = db.CreateCommand();
        var columns = new[] { "id", "amount", "note", "units" };
        // No ORDER BY: detect changes in the adapter's actual scan order. SQLite's
        // own typeof/hex expose lexical bytes, embedded NULs and storage classes.
        query.CommandText = "SELECT *, " + string.Join(", ", columns.Select(column =>
            $"typeof({column}), hex(CAST({column} AS BLOB))")) + " FROM financials";
        var result = new List<string>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(string.Join("|", Enumerable.Range(0, columns.Length).Select(index =>
                reader.GetString(columns.Length + index * 2) + ":" + reader.GetString(columns.Length + index * 2 + 1))));
        return result;
    }

    private static async Task<List<string>> SchemaAsync(string path, bool selectedOnly)
    {
        await using var db = Open(path);
        await db.OpenAsync();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master"
            + (selectedOnly ? " WHERE tbl_name='financials'" : "") + " ORDER BY type, name";
        var result = new List<string>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(string.Join("|", Enumerable.Range(0, 4).Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetString(index))));
        return result;
    }

    private static async Task<List<DataRecord>> RowsAsync(SourceConnection source)
    {
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(source, default);
        var result = new List<DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(source, schema, default)) result.Add(row);
        return result;
    }

    private sealed class SourceFixture : IAsyncDisposable
    {
        private SqliteConnection? _writer;
        private byte[] _mainBytes = [];
        private byte[]? _walBytes;
        private List<string> _allSchema = [];
        public SourceConnection Source { get; }
        public List<string> RawRows { get; private set; } = [];
        public List<string> SelectedSchema { get; private set; } = [];
        public List<DataRecord> Rows { get; private set; } = [];
        public string SchemaFingerprint { get; private set; } = "";

        private SourceFixture(string path, SqliteConnection writer)
        {
            Source = SnapshotCaptureCrashContract.CreateSource(path);
            _writer = writer;
        }

        public static async Task<SourceFixture> CreateAsync(string root, string name, string journalMode)
        {
            var path = Path.Combine(root, name + ".sqlite");
            var writer = Open(path, SqliteOpenMode.ReadWriteCreate);
            var fixture = new SourceFixture(path, writer);
            try
            {
                await writer.OpenAsync();
                using (var setup = writer.CreateCommand())
                {
                    setup.CommandText = """
                        PRAGMA journal_mode=DELETE;
                        CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT NOT NULL, note TEXT, units INTEGER);
                        CREATE INDEX financial_note ON financials(note DESC, amount) WHERE note IS NOT NULL;
                        CREATE TABLE confidential(secret TEXT NOT NULL);
                        INSERT INTO confidential VALUES ($secret);
                        INSERT INTO financials VALUES
                            (90, '0012.5000', $name, 9223372036854775807),
                            (-4, '-000.00', NULL, NULL),
                            (7, ' 7.50 ', '', -9223372036854775808);
                        """;
                    setup.Parameters.AddWithValue("$secret", ConfidentialSentinel);
                    setup.Parameters.AddWithValue("$name", name);
                    await setup.ExecuteNonQueryAsync();
                }
                using (var mode = writer.CreateCommand())
                {
                    mode.CommandText = "PRAGMA journal_mode=" + journalMode;
                    Assert.Equal(journalMode.ToLowerInvariant(), Convert.ToString(await mode.ExecuteScalarAsync()));
                    mode.CommandText = "PRAGMA wal_autocheckpoint=0";
                    await mode.ExecuteNonQueryAsync();
                }
                var beforeCommit = await ReadLiveBytesAsync(path);
                using (var transaction = writer.BeginTransaction())
                using (var commit = writer.CreateCommand())
                {
                    commit.Transaction = transaction;
                    commit.CommandText = """
                        UPDATE financials SET amount='9007199254740993.0001', note=$committed WHERE id=7;
                        INSERT INTO financials VALUES (123, '+00042.0100', $unicode, 0);
                        """;
                    commit.Parameters.AddWithValue("$committed", "committed-" + name);
                    commit.Parameters.AddWithValue("$unicode", name + ": 日本語 é e\u0301 😀\0tail;--");
                    await commit.ExecuteNonQueryAsync();
                    await transaction.CommitAsync();
                }
                fixture._mainBytes = await ReadLiveBytesAsync(path);
                if (journalMode == "WAL")
                {
                    // The live connection prevents close-time checkpointing. Prove
                    // the committed update/insert are still absent from the main file.
                    Assert.Equal(beforeCommit, fixture._mainBytes);
                    Assert.True(File.Exists(path + "-wal"));
                    fixture._walBytes = await ReadLiveBytesAsync(path + "-wal");
                    Assert.True(fixture._walBytes.Length > 32);
                }
                else
                {
                    Assert.False(File.Exists(path + "-wal"));
                    Assert.False(File.Exists(path + "-journal"));
                }
                fixture.RawRows = await RawRowsAsync(path);
                fixture.Rows = await RowsAsync(fixture.Source);
                Assert.Equal(4, fixture.Rows.Count);
                Assert.Equal("9007199254740993.0001", fixture.Rows.Single(row => Equals(row.Fields["id"].Raw, 7L)).Fields["amount"].LexicalSource);
                Assert.Contains(fixture.Rows, row => Equals(row.Fields["id"].Raw, 123L));
                fixture.SelectedSchema = await SchemaAsync(path, selectedOnly: true);
                fixture._allSchema = await SchemaAsync(path, selectedOnly: false);
                fixture.SchemaFingerprint = (await new SqliteSourceAdapter().InspectSchemaAsync(fixture.Source, default)).Fingerprint;
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task AssertUnchangedAsync()
        {
            Assert.Equal(RawRows, await RawRowsAsync(Source.Path!));
            AssertRowsEqual(Rows, await RowsAsync(Source));
            Assert.Equal(_allSchema, await SchemaAsync(Source.Path!, selectedOnly: false));
            Assert.Equal(_mainBytes, await ReadLiveBytesAsync(Source.Path!));
            if (_walBytes is not null) Assert.Equal(_walBytes, await ReadLiveBytesAsync(Source.Path! + "-wal"));
            else Assert.False(File.Exists(Source.Path! + "-wal"));
            using var query = _writer!.CreateCommand();
            query.CommandText = "SELECT secret FROM confidential";
            Assert.Equal(ConfidentialSentinel, await query.ExecuteScalarAsync());
        }

        public async Task DeleteLiveSourceAsync()
        {
            await DisposeAsync();
            File.Delete(Source.Path!);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) File.Delete(Source.Path! + suffix);
        }

        public async ValueTask DisposeAsync()
        {
            if (_writer is null) return;
            await _writer.DisposeAsync();
            _writer = null;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Describe() => $"Harness exit {ExitCode}. stdout: {StandardOutput}\nstderr: {StandardError}";
    }

    private sealed class HarnessProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _readyPath;
        private readonly string _releasePath;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _boundary;

        private HarnessProcess(Process process, string readyPath, string releasePath, string boundary)
        {
            _process = process;
            _readyPath = readyPath;
            _releasePath = releasePath;
            _boundary = boundary;
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static HarnessProcess Start(string root, string name, string source, string destination, string boundary,
            string? releasePath = null)
        {
            var readyPath = Path.Combine(root, name + ".ready.json");
            releasePath ??= Path.Combine(root, name + ".release");
            var testAssembly = typeof(SnapshotProcessLossTests).Assembly.Location;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(testAssembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
                typeof(SnapshotCaptureCrashContract).Assembly.Location,
                "snapshot-capture", source, destination, boundary, readyPath, releasePath
            }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start snapshot capture harness.");
            return new HarnessProcess(process, readyPath, releasePath, boundary);
        }

        public async Task<SnapshotCaptureCrashContract.ReadySignal> WaitUntilReadyAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException($"Harness exited before {_boundary}. " + (await WaitForExitAsync()).Describe());
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    await StopAsync();
                    throw new TimeoutException($"Harness never reached {_boundary}. " + (await WaitForExitAsync()).Describe());
                }
                if (File.Exists(_readyPath))
                {
                    try
                    {
                        var ready = JsonSerializer.Deserialize<SnapshotCaptureCrashContract.ReadySignal>(
                            await File.ReadAllBytesAsync(_readyPath), JsonOptions);
                        Assert.NotNull(ready);
                        Assert.Equal(_boundary, ready.Boundary);
                        Assert.False(_process.HasExited, $"Harness exited rather than blocking at {_boundary}.");
                        return ready;
                    }
                    catch (IOException)
                    {
                        // The ready marker is published with an atomic rename, but
                        // Windows scanners can briefly hold the new path open.
                        // Retry within the existing bounded readiness deadline.
                    }
                }
                await Task.Delay(20);
            }
        }

        public Task ReleaseAsync() => File.WriteAllTextAsync(_releasePath, "release");

        public async Task<ProcessResult> KillAndWaitAsync()
        {
            Assert.False(_process.HasExited, "Publisher must still be alive when its parent kills it.");
            _process.Kill(entireProcessTree: true);
            return await WaitForExitAsync();
        }

        public async Task<ProcessResult> WaitForExitAsync()
        {
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                await StopAsync();
                throw new TimeoutException($"Harness failed to finish after {_boundary}. stdout: {await _stdout}\nstderr: {await _stderr}");
            }
            return new ProcessResult(_process.ExitCode, await _stdout.WaitAsync(TimeSpan.FromSeconds(10)),
                await _stderr.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        private async Task StopAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
                await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { _process.Dispose(); }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-snapshot-crash-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(Path, recursive: true); return; }
                catch (IOException) when (attempt < 9) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(50); }
            }
        }
    }
}
