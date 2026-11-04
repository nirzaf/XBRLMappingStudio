using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class SqliteSourceAdapterTests
{
    [Fact]
    public async Task DestinationInspectorDiscoversOnlyCompatibleUniqueKeyMetadataWithoutWriting()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("target-schema.db");
        await using (var database = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE facts (fact_key TEXT NOT NULL, value TEXT, other TEXT); CREATE UNIQUE INDEX facts_key ON facts(fact_key); CREATE UNIQUE INDEX facts_partial ON facts(other) WHERE other IS NOT NULL; CREATE UNIQUE INDEX facts_nocase ON facts(other COLLATE NOCASE);";
            await command.ExecuteNonQueryAsync();
        }

        var before = await File.ReadAllBytesAsync(path);
        var inspector = new SqliteDestinationInspector();
        var tables = await inspector.DiscoverTablesAsync(path);
        var schema = await inspector.InspectTableAsync(path, "facts");

        Assert.Equal(new[] { "facts" }, tables);
        Assert.Equal(new[] { "fact_key", "value", "other" }, schema.Columns.Keys);
        Assert.Contains(schema.UniqueKeys, key => key.SequenceEqual(new[] { "fact_key" }));
        Assert.DoesNotContain(schema.UniqueKeys, key => key.SequenceEqual(new[] { "other" }));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));

        var missing = ws.PathFor("missing.db");
        await Assert.ThrowsAsync<AdapterException>(() => inspector.DiscoverTablesAsync(missing));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task ReadsRowsWithSchemaAndCellLineageUsingReadOnlyConnection()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("source.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (account TEXT NOT NULL, amount TEXT, active INTEGER); INSERT INTO ledger VALUES ('0007', '123.4500', 1), ('0008', NULL, 0);";
            await command.ExecuteNonQueryAsync();
        }

        var connection = new SourceConnection("ledger-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger", ["decimalColumns"] = "amount" });
        var adapter = new SqliteSourceAdapter();
        Assert.Equal(new[] { "ledger" }, await adapter.DiscoverTablesAsync(connection, default));
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal(new[] { "account", "amount", "active" }, schema.Columns.Select(c => c.Name));
        Assert.Equal(PortShape.Decimal, schema.Columns[1].InferredShape);
        Assert.All(schema.Columns, column => Assert.Equal(0, column.PrimaryKeyOrdinal));

        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        Assert.Single(sample.Rows);
        Assert.True(sample.IsTruncated);
        Assert.Equal("0007", sample.Rows[0]["account"].AsString());
        Assert.Equal(123.45m, sample.Rows[0]["amount"].AsDecimal());
        Assert.Contains("ledger.amount", sample.Rows[0].CellSources!["amount"].Locator);

        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) rows.Add(row);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[1]["amount"].IsNil);
        Assert.All(rows, row => Assert.False(row.HasStableRowIdentity));
        Assert.StartsWith("pos:", rows[0].RowId, StringComparison.Ordinal);
        Assert.False(adapter.Capabilities.AllowsWriteTarget);
    }

    [Fact]
    public async Task DeclaredCompositePrimaryKeyProducesStableHashedRowIdentities()
    {
        using var ws = new TempWorkspace();

        async Task<(ISourceSchema Schema, IReadOnlyList<DataRecord> Rows)> ReadSource(string fileName, bool reverse)
        {
            var path = ws.PathFor(fileName);
            await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE ledger (tenant TEXT NOT NULL, account TEXT NOT NULL, amount TEXT, PRIMARY KEY(tenant,account)) WITHOUT ROWID;";
                await command.ExecuteNonQueryAsync();

                var rows = new[] { (Tenant: "tenant-a", Account: "0007", Amount: "1.25"), (Tenant: "tenant-b", Account: "0007", Amount: "2.50") };
                foreach (var row in reverse ? rows.Reverse() : rows)
                {
                    command.CommandText = "INSERT INTO ledger VALUES($tenant,$account,$amount)";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$tenant", row.Tenant);
                    command.Parameters.AddWithValue("$account", row.Account);
                    command.Parameters.AddWithValue("$amount", row.Amount);
                    await command.ExecuteNonQueryAsync();
                }
            }

            var connection = new SourceConnection("ledger-source", "sqlite", path,
                new Dictionary<string, string> { ["table"] = "ledger", ["decimalColumns"] = "amount" });
            var adapter = new SqliteSourceAdapter();
            var schema = await adapter.InspectSchemaAsync(connection, default);
            var result = new List<DataRecord>();
            await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) result.Add(row);
            return (schema, result);
        }

        var first = await ReadSource("first.db", reverse: false);
        var second = await ReadSource("second.db", reverse: true);

        Assert.Equal(1, first.Schema.Columns.Single(column => column.Name == "tenant").PrimaryKeyOrdinal);
        Assert.Equal(2, first.Schema.Columns.Single(column => column.Name == "account").PrimaryKeyOrdinal);
        Assert.Equal(first.Schema.Fingerprint, second.Schema.Fingerprint);

        static string CompositeKey(DataRecord record)
            => record["tenant"].AsString() + "\0" + record["account"].AsString();

        var firstIds = first.Rows.ToDictionary(CompositeKey, row => row.RowId, StringComparer.Ordinal);
        var secondIds = second.Rows.ToDictionary(CompositeKey, row => row.RowId, StringComparer.Ordinal);
        Assert.Equal(firstIds, secondIds);
        Assert.All(first.Rows, row =>
        {
            Assert.True(row.HasStableRowIdentity);
            Assert.StartsWith("pk:sha256:", row.RowId, StringComparison.Ordinal);
            Assert.DoesNotContain("tenant-a", row.RowId, StringComparison.Ordinal);
            Assert.Equal(row.RowId, row.CellSources!["amount"].DisplayName);
        });
    }

    [Fact]
    public async Task ReadFullKeepsOneWalSnapshotWhileConcurrentWriterCommits()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("snapshot.db");
        await using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "CREATE TABLE ledger (id INTEGER PRIMARY KEY, amount TEXT); INSERT INTO ledger VALUES (1,'original-first'),(2,'original-second');";
            await command.ExecuteNonQueryAsync();
        }

        var connection = new SourceConnection("ledger-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger" });
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        await using var enumerator = adapter.ReadFullAsync(connection, schema, default).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("original-first", enumerator.Current["amount"].AsString());

        await using (var writer = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await writer.OpenAsync();
            await using var transaction = writer.BeginTransaction();
            await using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ledger SET amount='changed-second' WHERE id=2; INSERT INTO ledger VALUES (3,'new-third');";
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var observed = new List<string> { enumerator.Current["amount"].AsString() };
        while (await enumerator.MoveNextAsync())
            observed.Add(enumerator.Current["amount"].AsString());
        Assert.Equal(new[] { "original-first", "original-second" }, observed);

        await using var verify = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT amount FROM ledger ORDER BY id";
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        var current = new List<string>();
        while (await reader.ReadAsync()) current.Add(reader.GetString(0));
        Assert.Equal(new[] { "original-first", "changed-second", "new-third" }, current);
    }

    [Fact]
    public async Task SchemaFingerprintChangesWhenPrimaryKeyDefinitionChanges()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("schema.db");
        await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE ledger (account TEXT, amount TEXT);";
        await command.ExecuteNonQueryAsync();
        var connection = new SourceConnection("ledger-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger" });
        var adapter = new SqliteSourceAdapter();
        var noKeySchema = await adapter.InspectSchemaAsync(connection, default);

        command.CommandText = "DROP TABLE ledger; CREATE TABLE ledger (account TEXT PRIMARY KEY, amount TEXT);";
        await command.ExecuteNonQueryAsync();
        var primaryKeySchema = await adapter.InspectSchemaAsync(connection, default);

        Assert.NotEqual(noKeySchema.Fingerprint, primaryKeySchema.Fingerprint);
    }

    [Fact]
    public async Task NullValueInDeclaredPrimaryKeyCannotProduceStableIdentity()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("nullable-primary-key.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (account TEXT PRIMARY KEY, amount TEXT); INSERT INTO ledger VALUES (NULL,'1.25');";
            await command.ExecuteNonQueryAsync();
        }

        var connection = new SourceConnection("ledger-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger" });
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, default)) { }
        });

        Assert.Equal(DiagnosticCodes.IoDuplicateKey, error.Code);
    }

    [Fact]
    public async Task DiscoveryExcludesOnlyLiteralSqliteInternalPrefix()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("tables.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (id TEXT); CREATE TABLE sqliteData (id TEXT); CREATE TABLE [sqlite-x] (id TEXT); CREATE TABLE generated (id INTEGER PRIMARY KEY AUTOINCREMENT);";
            await command.ExecuteNonQueryAsync();
        }
        var tables = await new SqliteSourceAdapter().DiscoverTablesAsync(new SourceConnection("s", "sqlite", path), default);
        Assert.Equal(new[] { "generated", "ledger", "sqlite-x", "sqliteData" }, tables);
    }

    [Fact]
    public async Task RejectsFloatingPointFinancialValuesAndTableSqlInjection()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("source.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (amount REAL); INSERT INTO ledger VALUES (1.25);";
            await command.ExecuteNonQueryAsync();
        }

        var adapter = new SqliteSourceAdapter();
        var connection = new SourceConnection("sqlite", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger" });
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, default)) { }
        });
        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, error.Code);

        var injected = connection with
        {
            Configuration = new Dictionary<string, string> { ["table"] = "ledger; DROP TABLE ledger" }
        };
        var missing = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(injected, default));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, missing.Code);
    }

    [Fact]
    public async Task TargetUpsertsTransactionallyAndUsesIntentBoundReceipt()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("target.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();
        var first = await publisher.PublishAsync(destination,
            new[] { Record("007", 123.4500m) }, new[] { "account" }, "op-1", "sha256:intent-a");
        var replay = await publisher.PublishAsync(destination,
            new[] { Record("007", 123.4500m) }, new[] { "account" }, "op-1", "sha256:intent-a");
        Assert.Equal(first, replay);

        foreach (var intent in new[] { "sha256:intent-a", "sha256:intent-b" })
        {
            var conflict = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
                new[] { Record("007", 999m) }, new[] { "account" }, "op-1", intent));
            Assert.Equal(DiagnosticCodes.PubReceiptConflict, conflict.Code);
        }
        var changedIntentOnly = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record("007", 123.4500m) }, new[] { "account" }, "op-1", "sha256:intent-changed-only"));
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, changedIntentOnly.Code);

        await using var read = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var query = read.CreateCommand();
        query.CommandText = "SELECT amount FROM report WHERE account=$account";
        query.Parameters.AddWithValue("$account", "007");
        Assert.Equal("123.4500", query.ExecuteScalar());
    }

    [Fact]
    public async Task TargetPreviewReportsBoundedInsertUpdateUnchangedAndConflictCountsWithoutWriting()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("preview.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount TEXT); INSERT INTO report(account,amount) VALUES('existing','9'),('changed','1');";
            await command.ExecuteNonQueryAsync();
        }

        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var records = new[]
        {
            Record("existing", 9m),
            Record("changed", 2m),
            Record("new", 11m),
            Record("new", 12m)
        };
        var before = await File.ReadAllBytesAsync(path);
        var publisher = new SqliteTargetPublisher();

        var complete = await publisher.PreviewAsync(destination, records, new[] { "account" });
        var bounded = await publisher.PreviewAsync(destination, records, new[] { "account" },
            maximumCandidateRows: 2, sampleLimit: 1);

        Assert.Equal(4, complete.CandidateRows);
        Assert.Equal(4, complete.InspectedRows);
        Assert.True(complete.CountsAreComplete);
        Assert.Equal(1, complete.Inserts);
        Assert.Equal(2, complete.Updates);
        Assert.Equal(1, complete.UnchangedValueUpdates);
        Assert.Equal(1, complete.Conflicts);
        Assert.Equal(new[] { "unchanged", "update", "insert", "conflict" },
            complete.Samples.Select(sample => sample.Outcome));

        Assert.Equal(2, bounded.InspectedRows);
        Assert.False(bounded.CountsAreComplete);
        Assert.Equal(2, bounded.UncertainRows);
        Assert.Single(bounded.Samples);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TargetUpsertsRowIdIntegerPrimaryKeyWithoutCollationConflictTarget()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("integer-primary-key.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account INTEGER PRIMARY KEY, amount TEXT); CREATE UNIQUE INDEX uq_account_nocase ON report(account COLLATE NOCASE);";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();

        await publisher.PublishAsync(destination, new[] { Record(DataValue.IntegerValue(7), 1m) },
            new[] { "account" }, "integer-key-1", "intent-1");
        var updated = await publisher.PublishAsync(destination, new[] { Record(DataValue.IntegerValue(7), 2m) },
            new[] { "account" }, "integer-key-2", "intent-2");

        Assert.Equal(1, updated.WrittenRows);
        await using var read = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var query = read.CreateCommand();
        query.CommandText = "SELECT amount FROM report WHERE account=7";
        Assert.Equal("2", query.ExecuteScalar());
    }

    [Fact]
    public async Task TargetRejectsNullAndDuplicateBatchKeysAndPartialIndexes()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("keys.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT, amount TEXT); CREATE UNIQUE INDEX uq_report_account ON report(account) WHERE account <> '';";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path, new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();
        var partial = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record("007", 1m) }, new[] { "account" }, "partial", "intent"));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, partial.Code);

        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "DROP INDEX uq_report_account; CREATE UNIQUE INDEX uq_report_account ON report(account);";
            await command.ExecuteNonQueryAsync();
        }
        var duplicate = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record("007", 1m), Record("007", 2m) }, new[] { "account" }, "dupe", "intent"));
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, duplicate.Code);
        var nil = new XBRLMappingStudio.Engine.DataRecord { RowId = "nil", Fields = new Dictionary<string, DataValue>
        {
            ["account"] = DataValue.ExplicitNil(PortShape.String), ["amount"] = DataValue.DecimalValue(1m)
        }};
        var nullKey = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { nil }, new[] { "account" }, "nil", "intent"));
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, nullKey.Code);
    }

    [Fact]
    public async Task TargetRejectsDecimalValuesForNumericAffinity()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("numeric.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount NUMERIC);";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path, new Dictionary<string, string> { ["table"] = "report" });
        var error = await Assert.ThrowsAsync<AdapterException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { Record("007", 12345678901234567890.12345678m) }, new[] { "account" }, "numeric", "intent"));
        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, error.Code);
    }

    [Fact]
    public async Task TargetAcceptsCompositeBusinessKeysWithoutCollapsingDistinctTuples()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("composite.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (tenant TEXT NOT NULL, account TEXT NOT NULL, amount TEXT, UNIQUE(tenant,account));";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path, new Dictionary<string, string> { ["table"] = "report" });
        var error = await Assert.ThrowsAsync<AdapterException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { CompositeRecord("t1", "a1", 1m), CompositeRecord("t1", "a1", 2m) },
            new[] { "tenant", "account" }, "duplicate-composite", "intent"));
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, error.Code);
        var receipt = await new SqliteTargetPublisher().PublishAsync(destination,
            new[] { CompositeRecord("t1", "a1", 1m), CompositeRecord("t1", "a2", 2m) },
            new[] { "tenant", "account" }, "distinct-composite", "intent");
        Assert.Equal(2, receipt.WrittenRows);
    }

    [Fact]
    public async Task TargetRejectsExpressionAndNonBinaryUniqueIndexes()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("index-shapes.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT, tenant TEXT, amount TEXT); CREATE UNIQUE INDEX uq_expr ON report(account, lower(tenant));";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path, new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();
        var expression = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record("a1", 1m) }, new[] { "account" }, "expression", "intent"));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, expression.Code);

        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "DROP INDEX uq_expr; CREATE UNIQUE INDEX uq_nocase ON report(account COLLATE NOCASE);";
            await command.ExecuteNonQueryAsync();
        }
        var collation = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record("a1", 1m) }, new[] { "account" }, "nocase", "intent"));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, collation.Code);
    }

    [Fact]
    public async Task IntentFingerprintDistinguishesValueShape()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("shape.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account, amount TEXT, UNIQUE(account));";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path, new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();
        await publisher.PublishAsync(destination, new[] { Record(DataValue.IntegerValue(7), 1m) }, new[] { "account" }, "shape", "same-upstream");
        var error = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
            new[] { Record(DataValue.StringValue("7"), 1m) }, new[] { "account" }, "shape", "same-upstream"));
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, error.Code);
    }

    [Fact]
    public async Task FailedBatchRollsBackRowsAndReceipt()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("rollback.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount TEXT); CREATE TRIGGER reject_bad BEFORE INSERT ON report WHEN NEW.account='bad' BEGIN SELECT RAISE(ABORT,'rejected'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { Record("good", 1m), Record("bad", 2m) }, new[] { "account" }, "op-rollback", "sha256:intent"));

        await using var read = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var check = read.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM report";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='xms_idempotency_receipts'";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Fact]
    public async Task TargetRejectsBusinessKeyWithoutUniqueConstraintBeforeWriting()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("non-unique.db");
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT, amount TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var error = await Assert.ThrowsAsync<AdapterException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { Record("007", 1m) }, new[] { "account" }, "op-key-check", "sha256:intent"));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    private static XBRLMappingStudio.Engine.DataRecord Record(string account, decimal amount)
        => Record(DataValue.StringValue(account), amount);

    private static XBRLMappingStudio.Engine.DataRecord Record(DataValue account, decimal amount)
        => new()
        {
            RowId = "key",
            Fields = new Dictionary<string, DataValue>
            {
                ["account"] = account,
                ["amount"] = DataValue.DecimalValue(amount)
            }
        };

    private static XBRLMappingStudio.Engine.DataRecord CompositeRecord(string tenant, string account, decimal amount)
        => new()
        {
            RowId = tenant + ":" + account,
            Fields = new Dictionary<string, DataValue>
            {
                ["tenant"] = DataValue.StringValue(tenant),
                ["account"] = DataValue.StringValue(account),
                ["amount"] = DataValue.DecimalValue(amount)
            }
        };
}
