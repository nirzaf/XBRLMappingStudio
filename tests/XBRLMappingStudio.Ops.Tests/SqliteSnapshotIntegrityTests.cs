using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class SqliteSnapshotIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xms-snapshot-integrity-" + Guid.NewGuid().ToString("N"));

    public SqliteSnapshotIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("table", "insert")]
    [InlineData("table", "attach")]
    [InlineData("index", "insert")]
    [InlineData("index", "attach")]
    public async Task StoredDdlCannotExecuteAppendedStatements(string objectType, string payload)
    {
        var source = await CreateSourceAsync("""
            CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT);
            CREATE INDEX financial_note ON financials(note);
            INSERT INTO financials VALUES (1, '0012.5000', 'original');
            """);
        var marker = Path.Combine(_root, "external-marker.sqlite");
        var statement = payload == "insert"
            ? "INSERT INTO financials VALUES (9999, '777.00', 'injected');"
            : $"ATTACH DATABASE {Literal(marker)} AS injected; CREATE TABLE injected.executed(value TEXT);";
        var originalBytes = await File.ReadAllBytesAsync(source.Path!);
        Assert.Single(await RawRowsAsync(source));
        // Bundled SQLite 3.54.0 rejects this catalog during reload, whereas the
        // Python 3.53.1 reproduction exposed the first CREATE. Neither may execute
        // the appended batch; direct admission tests below also cover the guard.
        await RewriteSchemaAsync(source, objectType, objectType == "table" ? "financials" : "financial_note",
            sql => sql + "; /* untrusted tail */ " + statement);
        var tamperedBytes = await File.ReadAllBytesAsync(source.Path!);
        Assert.NotEqual(originalBytes, tamperedBytes);
        var destination = Path.Combine(_root, "rejected");
        var copiedRows = 0L;

        var error = await Record.ExceptionAsync(() => SqliteSourceSnapshotStore.CaptureAsync(
            source, destination, default, new InlineProgress<long>(count => copiedRows = count)));

        if (error is AdapterException adapterError)
            Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, adapterError.Code);
        else
            Assert.Equal(11, Assert.IsType<SqliteException>(error).SqliteErrorCode); // SQLITE_CORRUPT
        Assert.Equal(0L, copiedRows);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(marker + "-journal"));
        AssertNoCaptureArtifacts(destination);
        Assert.Equal(tamperedBytes, await File.ReadAllBytesAsync(source.Path!));
    }

    [Fact]
    public async Task DdlWithCommentsQuotedDelimitersAndDefaultsRemainsReplayable()
    {
        const string table = "financial; \"ledger\"";
        var source = await CreateSourceAsync(""""
            CREATE TABLE "financial; ""ledger""" (
                "id;--" INTEGER PRIMARY KEY,
                amount TEXT /* ; harmless SQL comment */ DEFAULT ('00;12.5000'),
                "note/*;*/" TEXT DEFAULT ('it''s ; -- /* quoted */'),
                [bracket;field] TEXT DEFAULT ('[;]'),
                `tick;field` TEXT DEFAULT ('` ;')
            ) /* ; INSERT INTO ignored VALUES (1); */;
            CREATE UNIQUE INDEX "idx; ""quoted""" ON "financial; ""ledger""" (
                "note/*;*/" COLLATE BINARY DESC /* ; quoted column */, "id;--"
            ) WHERE "note/*;*/" <> 'skip;--/*';
            INSERT INTO "financial; ""ledger""" ("id;--", amount) VALUES (8, '0012.5000'), (-2, '-000.00');
            """", table);
        var expectedIndexes = await IndexDefinitionsAsync(source);
        var replay = await CaptureAndAssertParityAsync(source);

        Assert.Equal(expectedIndexes, await IndexDefinitionsAsync(replay));
    }

    [Theory]
    [InlineData("CREATE TABLE financials(id INTEGER); INSERT INTO financials VALUES (2)", null)]
    [InlineData("CREATE TABLE financials(id INTEGER); /* ; */ ATTACH 'marker.sqlite' AS injected", null)]
    [InlineData("CREATE INDEX financial_note ON financials(amount); INSERT INTO financials VALUES (2)", "financial_note")]
    [InlineData("CREATE UNIQUE INDEX financial_note ON financials(amount); -- comment\nATTACH 'marker.sqlite' AS injected", "financial_note")]
    [InlineData("CREATE TABLE financials(id INTEGER);;", null)]
    [InlineData("CREATE TABLE financials(id TEXT DEFAULT 'unterminated)", null)]
    [InlineData("CREATE TABLE financials(id INTEGER) /* unterminated", null)]
    [InlineData("CREATE TABLE financials(id INTEGER)\0", null)]
    [InlineData("CREATE TABLE financials AS SELECT 1", null)]
    [InlineData("CREATE TABLE unrelated(id INTEGER)", null)]
    [InlineData("CREATE INDEX another_index ON financials(amount)", "financial_note")]
    [InlineData("CREATE INDEX financial_note ON unrelated(amount)", "financial_note")]
    [InlineData("CREATE TABLE attached.financials(id INTEGER)", null)]
    [InlineData("CREATE VIRTUAL TABLE financials USING fts5(amount)", null)]
    [InlineData("CREATE TEMP TABLE financials(id INTEGER)", null)]
    public void SchemaAdmissionRejectsAdditionalStatementsAndWrongTargets(string sql, string? indexName)
    {
        var error = Assert.Throws<AdapterException>(() => AdmitSchemaStatement(sql, "financials", indexName));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
    }

    [Theory]
    [InlineData("CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT DEFAULT 'semi;''quote')", null)]
    [InlineData("/* ; prefix */ CREATE /* ; */ TABLE IF NOT EXISTS main.[financials] (id INTEGER PRIMARY KEY, amount TEXT DEFAULT (';--')) STRICT; -- ; ATTACH 'ignored' AS ignored\n", null)]
    [InlineData("CREATE TABLE `financials` (id TEXT PRIMARY KEY) WITHOUT ROWID; /* ; final */", null)]
    [InlineData("CREATE TABLE financials(\"id;--\" INTEGER, [bracket;field] TEXT DEFAULT '[;]', `tick;field` TEXT DEFAULT '`;')", null)]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS main.`financial_note` ON [financials] (amount DESC) WHERE amount <> 'it''s ; -- /*'; -- ; final\n", "financial_note")]
    [InlineData("CREATE /* ; */ INDEX \"financial_note\" ON \"financials\" ((length(amount))) WHERE amount IS NOT NULL /* ; */", "financial_note")]
    public void SchemaAdmissionAcceptsOneDefinitionWithQuotedDelimitersAndComments(string sql, string? indexName)
        => AdmitSchemaStatement(sql, "financials", indexName);

    [Fact]
    public async Task OversizedRebuiltIndexExpressionFailsWithinTheRowBudget()
    {
        var source = await CreateSourceAsync("""
            CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT);
            INSERT INTO financials VALUES (1, '0012.5000');
            CREATE INDEX amount_index ON financials(amount);
            """);
        // The stored index stays tiny. Rebuilding its forged expression would
        // generate 4 MiB + 2 bytes, just beyond the native SQLite length limit.
        await RewriteSchemaAsync(source, "index", "amount_index",
            _ => "CREATE INDEX amount_index ON financials(hex(zeroblob(2097153)))");
        var sourceBytes = await File.ReadAllBytesAsync(source.Path!);
        var destination = Path.Combine(_root, "expression-budget");

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));

        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, error.Code);
        Assert.True(error.IsInputBudgetExceeded);
        AssertNoCaptureArtifacts(destination);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source.Path!));
    }

    [Theory]
    [InlineData("UTF-16le", "00D8")]
    [InlineData("UTF-16le", "00DC")]
    [InlineData("UTF-16be", "D800")]
    [InlineData("UTF-16be", "DC00")]
    public async Task MalformedUtf16SurrogatesAreRejectedWithoutReplacingSourceBytes(string encoding, string hex)
    {
        var source = await CreateSourceAsync($"""
            PRAGMA encoding={Literal(encoding)};
            CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT);
            INSERT INTO financials VALUES (1, '0012.5000', CAST(X'{hex}' AS TEXT));
            """);
        var originalRows = await RawRowsAsync(source);
        Assert.EndsWith("|text:" + hex, Assert.Single(originalRows));
        var destination = Path.Combine(_root, "malformed-utf16");

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
        AssertNoCaptureArtifacts(destination);
        Assert.Equal(originalRows, await RawRowsAsync(source));
    }

    [Theory]
    [InlineData("80")]
    [InlineData("C080")]
    [InlineData("C328")]
    [InlineData("E282")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    public async Task MalformedUtf8IsRejectedRatherThanSilentlyReplacingSourceBytes(string hex)
    {
        var source = await CreateSourceAsync($"""
            CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT);
            INSERT INTO financials VALUES (1, '0012.5000', CAST(X'{hex}' AS TEXT));
            """);
        var originalRows = await RawRowsAsync(source);
        var destination = Path.Combine(_root, "malformed");

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
        AssertNoCaptureArtifacts(destination);
        Assert.Equal(originalRows, await RawRowsAsync(source));
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task EncodingAndNonAsciiPrimaryKeyOrderArePreserved(string encoding)
    {
        var source = await CreateSourceAsync($"""
            PRAGMA encoding={Literal(encoding)};
            CREATE TABLE financials(id TEXT PRIMARY KEY, amount TEXT, note TEXT) WITHOUT ROWID;
            CREATE INDEX note_index ON financials(note, amount, id);
            """);
        // These keys expose both endian-specific ordering regressions: UTF-16le
        // puts U+0100 before 'a'; UTF-16be puts U+10000 before U+E000.
        var keys = new[] { "a", "\u0100", "\uE000", "\U00010000" };
        await using (var db = Open(source.Path!))
        {
            await db.OpenAsync();
            for (var index = 0; index < keys.Length; index++)
            {
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO financials VALUES ($id, $amount, $note)";
                command.Parameters.AddWithValue("$id", keys[index]);
                command.Parameters.AddWithValue("$amount", index % 2 == 0 ? "0012.5000" : "-000.00");
                command.Parameters.AddWithValue("$note", "é e\u0301 日本語 😀\0tail;--" + keys[index]);
                await command.ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(encoding, await ScalarAsync(source.Path!, "PRAGMA encoding"));

        var replay = await CaptureAndAssertParityAsync(source);

        Assert.Equal(encoding, await ScalarAsync(replay.Path!, "PRAGMA encoding"));
        var rows = await RowsAsync(replay);
        Assert.All(rows, row => Assert.Contains(row.Fields["amount"].LexicalSource, new[] { "0012.5000", "-000.00" }));
    }

    [Theory]
    [InlineData("integer-primary-key")]
    [InlineData("autoincrement")]
    [InlineData("text-primary-key")]
    [InlineData("composite-primary-key")]
    [InlineData("without-rowid")]
    [InlineData("strict")]
    [InlineData("strict-without-rowid")]
    [InlineData("keyless")]
    [InlineData("covering-index")]
    [InlineData("shadowed-rowid")]
    public async Task SupportedLayoutsPreserveExactAdapterRowsOrderAndLocators(string layout)
    {
        var sql = layout switch
        {
            "integer-primary-key" => """
                CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT);
                INSERT INTO financials VALUES (90, '0012.5000', 'last'), (-4, '-000.00', NULL), (7, ' 7.50 ', '');
                """,
            "autoincrement" => """
                CREATE TABLE financials(id INTEGER PRIMARY KEY AUTOINCREMENT, amount TEXT, note TEXT);
                INSERT INTO financials VALUES (90, '0012.5000', 'last'), (-4, '-000.00', NULL), (7, ' 7.50 ', '');
                CREATE TABLE unrelated(id INTEGER PRIMARY KEY AUTOINCREMENT);
                INSERT INTO unrelated VALUES (444);
                """,
            "text-primary-key" => """
                CREATE TABLE financials(id TEXT PRIMARY KEY COLLATE NOCASE, amount TEXT, note TEXT);
                INSERT INTO financials VALUES ('z', '0012.5000', 'last'), ('A', '-000.00', NULL), ('m', ' 7.50 ', '');
                CREATE INDEX financial_note ON financials(note DESC) WHERE note IS NOT NULL;
                """,
            "composite-primary-key" => """
                CREATE TABLE financials(region TEXT, sequence INTEGER, amount TEXT, note TEXT, PRIMARY KEY(region, sequence));
                INSERT INTO financials VALUES ('z', 3, '0012.5000', 'last'), ('a', 2, '-000.00', NULL), ('a', 1, ' 7.50 ', '');
                """,
            "without-rowid" => """
                CREATE TABLE financials(region TEXT, sequence INTEGER, amount TEXT, note TEXT,
                    PRIMARY KEY(region COLLATE NOCASE DESC, sequence)) WITHOUT ROWID;
                INSERT INTO financials VALUES ('z', 3, '0012.5000', 'last'), ('a', 2, '-000.00', NULL), ('a', 1, ' 7.50 ', '');
                CREATE INDEX financial_note ON financials(note DESC, amount);
                """,
            "strict" => """
                CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT) STRICT;
                INSERT INTO financials VALUES (90, '0012.5000', 'last'), (-4, '-000.00', NULL), (7, ' 7.50 ', '');
                """,
            "strict-without-rowid" => """
                CREATE TABLE financials(region TEXT, sequence INTEGER, amount TEXT, note TEXT,
                    PRIMARY KEY(region DESC, sequence)) STRICT, WITHOUT ROWID;
                INSERT INTO financials VALUES ('z', 3, '0012.5000', 'last'), ('a', 2, '-000.00', NULL), ('a', 1, ' 7.50 ', '');
                """,
            "keyless" => """
                CREATE TABLE financials(amount TEXT, note TEXT);
                INSERT INTO financials(rowid, amount, note) VALUES (90, '0012.5000', 'last'), (-4, '-000.00', NULL), (7, ' 7.50 ', '');
                """,
            "covering-index" => """
                CREATE TABLE financials(id TEXT, amount TEXT, note TEXT);
                CREATE INDEX financial_cover ON financials(note DESC, amount, id);
                CREATE INDEX financial_expression ON financials(length(note)) WHERE note IS NOT NULL;
                INSERT INTO financials VALUES ('z', '0012.5000', 'last'), ('a', '-000.00', NULL), ('m', ' 7.50 ', '');
                """,
            "shadowed-rowid" => """
                CREATE TABLE financials(rowid TEXT, _rowid_ INTEGER, oid TEXT, amount TEXT, note TEXT);
                INSERT INTO financials VALUES ('z', 3, 'other', '0012.5000', 'last'),
                    ('a', 2, 'another', '-000.00', NULL), ('m', 1, 'third', ' 7.50 ', '');
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
        var source = await CreateSourceAsync(sql);

        var replay = await CaptureAndAssertParityAsync(source);

        Assert.Equal(await IndexDefinitionsAsync(source), await IndexDefinitionsAsync(replay));
        var rows = await RowsAsync(replay);
        Assert.Equal(3, rows.Count);
        if (layout == "autoincrement")
            Assert.Equal("financials", await ScalarAsync(replay.Path!, "SELECT group_concat(name) FROM sqlite_sequence"));
        Assert.Equal(layout is not ("keyless" or "covering-index" or "shadowed-rowid"), rows[0].HasStableRowIdentity);
        Assert.Equal(new[] { " 7.50 ", "-000.00", "0012.5000" },
            rows.Select(row => row.Fields["amount"].LexicalSource).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CaptureFailsClosedWhenPlannerStatisticsChangeAdapterScanOrder()
    {
        var source = await CreateSourceAsync("""
            CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT);
            INSERT INTO financials VALUES (1, '30.00'), (2, '10.00'), (3, '20.00');
            CREATE INDEX amount_idx ON financials(amount, id);
            ANALYZE;
            UPDATE sqlite_stat1 SET stat='3 1 1 sz=1' WHERE idx='amount_idx';
            INSERT INTO sqlite_stat1 VALUES ('financials', NULL, '3 sz=1000000');
            ANALYZE sqlite_schema;
            """);
        var expected = await RowsAsync(source);
        Assert.Equal(new object?[] { 2L, 3L, 1L }, expected.Select(row => row.Fields["id"].Raw));
        var sourceBytes = await File.ReadAllBytesAsync(source.Path!);
        var destination = Path.Combine(_root, "changed-order");

        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
        AssertNoCaptureArtifacts(destination);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source.Path!));
        Assert.Equal(expected.Select(row => row.RowId), (await RowsAsync(source)).Select(row => row.RowId));
    }

    [Theory]
    [InlineData("virtual-generated")]
    [InlineData("stored-generated")]
    [InlineData("blob")]
    [InlineData("null-primary-key")]
    public async Task UnreplayableShapesFailWithoutPublishing(string shape)
    {
        var sql = shape switch
        {
            "virtual-generated" => "CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, computed TEXT AS (amount) VIRTUAL); INSERT INTO financials(id, amount) VALUES (1, '0012.5000');",
            "stored-generated" => "CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, computed TEXT AS (amount) STORED); INSERT INTO financials(id, amount) VALUES (1, '0012.5000');",
            "blob" => "CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT); INSERT INTO financials VALUES (1, '0012.5000', X'616263');",
            "null-primary-key" => "CREATE TABLE financials(id TEXT PRIMARY KEY, amount TEXT); INSERT INTO financials VALUES (NULL, '0012.5000');",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var source = await CreateSourceAsync(sql);
        var destination = Path.Combine(_root, "unsupported");

        await Assert.ThrowsAsync<AdapterException>(() => SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));

        AssertNoCaptureArtifacts(destination);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("view")]
    [InlineData("trigger")]
    [InlineData("reserved-name-table")]
    [InlineData("unrelated-sequence")]
    public async Task DirectVerificationRejectsScopeExpansionEvenWithRecomputedByteDigest(string extraObject)
    {
        var source = await CreateSourceAsync("CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT); INSERT INTO financials VALUES (1, '0012.5000');");
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        MakeWritable(replay.Path!);
        await using (var db = Open(replay.Path!))
        {
            await db.OpenAsync();
            Execute(db, extraObject switch
            {
                "table" => "CREATE TABLE unrelated(secret TEXT); INSERT INTO unrelated VALUES ('not selected');",
                "view" => "CREATE VIEW unrelated AS SELECT * FROM financials;",
                "trigger" => "CREATE TRIGGER unrelated AFTER INSERT ON financials BEGIN SELECT 1; END;",
                "unrelated-sequence" => """
                    CREATE TABLE discarded(id INTEGER PRIMARY KEY AUTOINCREMENT);
                    DROP TABLE discarded;
                    INSERT INTO sqlite_sequence(name, seq) VALUES ('unrelated', 12);
                    """,
                "reserved-name-table" => """
                    CREATE TABLE unrelated(secret TEXT);
                    INSERT INTO unrelated VALUES ('not an internal SQLite table');
                    PRAGMA writable_schema=ON;
                    UPDATE sqlite_master SET name='sqlite_unrelated', tbl_name='sqlite_unrelated',
                        sql='CREATE TABLE sqlite_unrelated(secret TEXT)' WHERE name='unrelated';
                    PRAGMA writable_schema=OFF;
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(extraObject))
            });
        }
        replay = replay with { RetainedSnapshot = replay.RetainedSnapshot! with { DatabaseDigest = await DigestAsync(replay.Path!) } };

        var error = await Assert.ThrowsAsync<AdapterException>(() => SqliteSourceSnapshotStore.VerifyConnectionAsync(replay, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
        // A legacy full-database reference deliberately permits other objects.
        var legacy = replay with { RetainedSnapshot = replay.RetainedSnapshot! with { SnapshotStrategy = SqliteSourceSnapshotStore.LegacyStrategy } };
        await SqliteSourceSnapshotStore.VerifyConnectionAsync(legacy, default);
    }

    [Theory]
    [InlineData("strategy", "unknown-strategy")]
    [InlineData("strategy", "sqlite-retained-backup-v1")]
    [InlineData("strategy", null)]
    [InlineData("scope", "full-database")]
    [InlineData("scope", null)]
    public async Task OpenRejectsUnknownOrInconsistentStrategyAndScope(string property, string? value)
    {
        var source = await CreateSourceAsync("CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT); INSERT INTO financials VALUES (1, '0012.5000');");
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
        node[property] = value;
        MakeWritable(metadata);
        await File.WriteAllTextAsync(metadata, node.ToJsonString());

        var error = await Assert.ThrowsAsync<AdapterException>(() => SqliteSourceSnapshotStore.OpenAsync(metadata, source, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
    }

    [Fact]
    public async Task DirectVerificationRejectsUnknownStrategy()
    {
        var source = await CreateSourceAsync("CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT); INSERT INTO financials VALUES (1, '0012.5000');");
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        replay = replay with { RetainedSnapshot = replay.RetainedSnapshot! with { SnapshotStrategy = "sqlite-retained-unknown-v9" } };

        var error = await Assert.ThrowsAsync<AdapterException>(() => SqliteSourceSnapshotStore.VerifyConnectionAsync(replay, default));

        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
    }

    private async Task<SourceConnection> CreateSourceAsync(string sql, string table = "financials")
    {
        var path = Path.Combine(_root, "source.sqlite");
        await using var db = Open(path);
        await db.OpenAsync();
        Execute(db, sql);
        return new SourceConnection("integrity-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = table, ["decimalColumns"] = "amount" });
    }

    private async Task<SourceConnection> CaptureAndAssertParityAsync(SourceConnection source)
    {
        var expectedRows = await RowsAsync(source);
        var expectedRaw = await RawRowsAsync(source);
        var sourceBytes = await File.ReadAllBytesAsync(source.Path!);
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        var actualRows = await RowsAsync(replay);
        Assert.Equal(SqliteSourceSnapshotStore.Strategy, replay.RetainedSnapshot!.SnapshotStrategy);
        Assert.Equal(expectedRaw, await RawRowsAsync(replay));
        Assert.Equal(expectedRows.Count, actualRows.Count);
        for (var index = 0; index < expectedRows.Count; index++)
        {
            var expected = expectedRows[index];
            var actual = actualRows[index];
            Assert.Equal(expected.RowId, actual.RowId);
            Assert.Equal(expected.HasStableRowIdentity, actual.HasStableRowIdentity);
            Assert.Equal(expected.Fields.Keys, actual.Fields.Keys);
            foreach (var column in expected.Fields.Keys)
            {
                Assert.Equal(expected.Fields[column], actual.Fields[column]);
                // DataValue.Equals intentionally omits lexical form and provenance.
                Assert.Equal(expected.Fields[column].LexicalSource, actual.Fields[column].LexicalSource);
                Assert.Equal(expected.Fields[column].Source, actual.Fields[column].Source);
                Assert.Equal(expected.CellSources![column], actual.CellSources![column]);
            }
        }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source.Path!));
        Assert.Empty(Directory.GetDirectories(_root, ".xms-snapshot-*"));
        return replay;
    }

    private static async Task<List<DataRecord>> RowsAsync(SourceConnection source)
    {
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(source, default);
        var rows = new List<DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(source, schema, default)) rows.Add(row);
        return rows;
    }

    private static async Task<List<string>> RawRowsAsync(SourceConnection source)
    {
        var schema = await new SqliteSourceAdapter().InspectSchemaAsync(source, default);
        var columns = schema.Columns.OrderBy(column => column.Ordinal).ToArray();
        await using var db = Open(source.Path!, SqliteOpenMode.ReadOnly);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        // Read hex inside SQLite, not decoded .NET strings, to expose replacement
        // characters, embedded-NUL truncation, affinity changes and UTF encoding.
        command.CommandText = "SELECT *, " + string.Join(", ", columns.Select(column =>
            $"typeof({Quote(column.Name)}), hex(CAST({Quote(column.Name)} AS BLOB))"))
            + " FROM " + Quote(SqliteSourceAdapter.GetConfiguredTable(source));
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, columns.Length).Select(index =>
                reader.GetString(columns.Length + index * 2) + ":" + reader.GetString(columns.Length + index * 2 + 1));
            rows.Add(string.Join("|", values));
        }
        return rows;
    }

    private static async Task<List<string>> IndexDefinitionsAsync(SourceConnection source)
    {
        await using var db = Open(source.Path!, SqliteOpenMode.ReadOnly);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name=$table AND sql IS NOT NULL ORDER BY name";
        command.Parameters.AddWithValue("$table", SqliteSourceAdapter.GetConfiguredTable(source));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0) + ":" + reader.GetString(1));
        return result;
    }

    private static void AdmitSchemaStatement(string sql, string table, string? indexName)
    {
        // Directly exercise the guard because SQLite versions differ in whether
        // forged catalog SQL survives schema reload. Keep production visibility
        // private: this test seam must not become a schema-execution API.
        var method = typeof(SqliteSourceSnapshotStore).GetMethod("AdmitSchemaStatement", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try { method.Invoke(null, [sql, table, indexName, CancellationToken.None]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        }
    }

    private static async Task RewriteSchemaAsync(SourceConnection source, string type, string name, Func<string, string> rewrite)
    {
        await using var db = Open(source.Path!);
        await db.OpenAsync();
        using var read = db.CreateCommand();
        read.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name";
        read.Parameters.AddWithValue("$type", type);
        read.Parameters.AddWithValue("$name", name);
        var original = Assert.IsType<string>(await read.ExecuteScalarAsync());
        Execute(db, "PRAGMA writable_schema=ON");
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE sqlite_master SET sql=$sql WHERE type=$type AND name=$name";
        update.Parameters.AddWithValue("$sql", rewrite(original));
        update.Parameters.AddWithValue("$type", type);
        update.Parameters.AddWithValue("$name", name);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
        Execute(db, "PRAGMA writable_schema=OFF");
    }

    private static async Task<string> ScalarAsync(string path, string sql)
    {
        await using var db = Open(path, SqliteOpenMode.ReadOnly);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> DigestAsync(string path)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private void AssertNoCaptureArtifacts(string destination)
    {
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, ".xms-snapshot-*"));
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void MakeWritable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
