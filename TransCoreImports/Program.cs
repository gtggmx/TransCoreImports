using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace TransCoreImports;

internal static class Program
{
    // @OrgID = 0 returns every OrgName's rows in one call, so there's no need to
    // loop over the individual OrgIDs from #TMP_GANTRY_CONV in 1310_Proc_Result.sql.
    private const string AllOrgsId = "0";

    private const int MaxDaysPerCall = 7;

    // Raw per-lane-per-hour row as returned by the stored procedure (matches
    // #TMP_EXECRESULT in 1310_Proc_Result.sql). Every import type that shares a
    // procedure aggregates this same row list its own way, so the procedure is
    // only ever called once per (chunk, OrgID) no matter how many import types
    // are requested in one run.
    private sealed record RawRow(
        string OrgName, string LaneGroupName, int LaneNumber, DateTime TransDate, int TransHour,
        DateTime TollDay, int LaneGroupId, long[] Measures);

    // Raw row as returned by [dbo].[uspr_aud_PlazaRevenue_sql] (only the columns
    // 2725 actually needs).
    private sealed record RevenueRow(string LaneGroupName, decimal ExpectedRevenue, decimal Isf, DateTime DateDropped);

    // Raw row as returned by [RPrl].[uspr_trn_Leakage] (matches #TMP_2552 in 2552_test.sql).
    private sealed record LeakageRow(long OrgId, string OrgName, int LaneNumber, DateTime TransDate, double[] Measures);

    // Raw row for 5695 (violation summary by transmit date / transaction date / plaza): no stored
    // procedure exists for this one, it's a raw query. All values are the text the query formats
    // ("12,600", "$4,218.39"); PlazaID and the revenue can be empty since tblUFMRecord is joined
    // with a LEFT OUTER JOIN.
    private sealed record ViolSummaryRow(
        string DateTransmitted, string TransactionDate, string PlazaId, string TransactionCount, string IndicatedRevenue);

    // Raw row as returned by [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]
    // (matches #TMP_1220 in 1220_test.sql, columns 1220 actually needs).
    private sealed record TransactionRow(
        DateTime TransDate, int Lane, int VehClass, decimal TollFull, decimal TollCharged, decimal TollCollected,
        int IvisAxles, int CollectorAxles, string TransType, string OrgName, string GroupData,
        int LaneGroupId, string LaneGroupName);

    // Each import type has its own CSV column structure and aggregation, but types
    // that share a ProcedureName reuse one fetch of the raw rows (Fetch), boxed as
    // object since different procedures return different row shapes. WriteRows
    // casts back to the shape its own Fetch produces, writes the rows, and returns
    // the row count.
    // Finalize is for import types whose grouping spans the WHOLE run instead of
    // a single chunk (e.g. 1320 sums per lane across every date/hour requested).
    // When set, WriteRows only accumulates state and Finalize writes the final
    // rows once after every chunk has been processed.
    // 4438: one row of the roll-up of [dbo].[uspr_csc_UniqueTransponderReport_Sarad_Migration].
    // One group per (date, state, agency, revenue type); ByRoad holds the per-road
    // count/amount, DistinctTransponders is across ALL roads of the group.
    private sealed record TransponderGroup(
        DateTime Date, string State, string Agency, string RevenueType,
        SortedDictionary<string, (long Count, long Cents)> ByRoad, int DistinctTransponders);

    private sealed record ImportTypeConfig(
        string ProcedureName,
        string HeaderRow,
        Func<SqlConnection, SqlSettings, string, DateTime, DateTime, Task<object>> Fetch,
        Func<object, StreamWriter, long> WriteRows,
        Func<StreamWriter, long>? Finalize = null,
        // Days of data fetched per procedure call. Types that return one row per
        // transaction (hundreds of thousands a day) use 1 so a chunk fits in memory.
        int ChunkDays = MaxDaysPerCall);

    private static readonly Dictionary<string, ImportTypeConfig> ImportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1310"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_HourlyTraffic_1315b_1320b]",
            // Matches the header row from the legacy Susan_Old_CSV export, so downstream
            // consumers keyed on these SSRS textbox names keep working unchanged.
            // Facility,LaneGroup,Date,Hour,VIOL2,VIOL3,VIOL4,VIOL5,VIOL6_9,VIOL,
            // ETC2,ETC3,ETC4,ETC5,ETC6_9,ETC_Sum,AR,CardNonRev,PassNR,NRETC,NR_TOTAL,GrandTotal
            HeaderRow: "txtFacilityName,txtPriority,textbox82,textbox124,textbox83,textbox85,textbox86,textbox87,textbox88,textbox89," +
                       "textbox248,textbox63,textbox70,textbox71,textbox72,textbox73,textbox74,textbox75,textbox142,textbox183,textbox168,textbox213",
            Fetch: FetchTrafficRowsAsync,
            WriteRows: Write1310Rows),

        // Same source rows as 1310, aggregated per lane per day instead of per gantry
        // per hour (lanes are NOT summed together here).
        ["1270"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_HourlyTraffic_1315b_1320b]",
            HeaderRow: "OrgName,Date,Location,Lane,SunPass,Spec Evnt,NR,Viol",
            Fetch: FetchTrafficRowsAsync,
            WriteRows: Write1270Rows),

        // Straight passthrough of [dbo].[uspr_aud_PlazaRevenue_sql]: one row per
        // gantry, no aggregation.
        ["2725"] = new ImportTypeConfig(
            ProcedureName: "[dbo].[uspr_aud_PlazaRevenue_sql]",
            HeaderRow: "textBox35,textBox51,textBox61,textBox62",
            Fetch: FetchRevenueRowsAsync,
            WriteRows: Write2725Rows),

        // Grouped by OrgID/OrgName/LaneNumber/TransDate, summing every measure
        // column except ExecutionID -- matches the aggregation query already
        // drafted in 2552_test.sql. Columns A-Z match the "09-01-26 Leakage
        // Report 2552.csv" legacy layout: B-J are literal caption text repeated
        // on every row, W/Z both carry InProcess, X is a literal 0, and
        // Amendment is unused.
        ["2552"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_Leakage]",
            HeaderRow: "OrgName,Sent To IPS,Rejected,Awaiting Response,Returned From IPS,Uncollectable Non-System," +
                       "Uncollectable System,Flushed,No Image,In Process,LaneNumber,SentToIPS,Rejected,Outstanding," +
                       "ReceivedFromIPS,Collectable,UncollectableNonSystem,UncollectableNonSystemAmount,UncollectableSystem," +
                       "UncollectableSystemAmount,Flush,NoImage,InProcess,Zero,IPS,InProcess",
            Fetch: FetchLeakageRowsAsync,
            WriteRows: Write2552Rows),

        // No stored procedure exists for this one -- raw SQL text against base
        // tables, no aggregation (one row per UFM record). The name here is
        // just a display label, not a real SQL object.
        ["5695"] = new ImportTypeConfig(
            ProcedureName: "[raw-sql].[5695-ViolationsByTransmitDate]",
            HeaderRow: "DateTransmitted,TransactionDate,PlazaID,TransactionCount,IndicatedRevenueOrig",
            Fetch: FetchViolSummaryRowsAsync,
            WriteRows: Write5695Rows,
            ChunkDays: 3660),

        // Straight passthrough of [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum],
        // one row per transaction, with PlazaID looked up locally (see
        // GantryConvTable) since that lookup table lives on a different server
        // with no linked server between them -- matches 1220_test.sql.
        ["1220"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]",
            HeaderRow: "PlazaID,TransDate,TransTime,Lane,VehClass,TollFull,TollCharged,TollCollected," +
                       "IVISAxles,CollectorAxles,TransType,OrgName,GroupData,LaneGroupID,LaneGroupName",
            Fetch: FetchTransactionRowsAsync,
            WriteRows: Write1220Rows),

        // Per-transaction OTO rows rolled up in the app: per road (SR 112/836/874/878/924),
        // date, state, agency and revenue type -> transaction count and charged amount,
        // plus the group totals and distinct-transponder count repeated on each road
        // row. Header and layout match the "4438_TestFrom Susan" SSRS sample.
        ["4438"] = new ImportTypeConfig(
            ProcedureName: "[dbo].[uspr_csc_UniqueTransponderReport_Sarad_Migration]",
            HeaderRow: "Textbox778,RoadName,LaneExitDate1,State3,Agency3,RevenueType3,TransponderID2," +
                       "Transactions,ChargedAmount,Transactions2,ChargedAmount1",
            Fetch: FetchUniqueTransponderRowsAsync,
            WriteRows: Write4438Rows),

        // Per-transaction OTO rows (every road in one file), straight from
        // [dbo].[uspr_csc_OTOFileDetails_sql_Sarad_Migration]. Matches the legacy 5625
        // report's columns B..AB only: its batch label (A) and batch/grand total columns
        // are left out because the new source keeps no transaction->batch link.
        // One day per call: a day is ~800,000 rows.
        ["5625"] = new ImportTypeConfig(
            ProcedureName: "[dbo].[uspr_csc_OTOFileDetails_sql_Sarad_Migration]",
            HeaderRow: "PlazaCode,LaneNumber,LaneType,TollCollector,LaneExitDate,TransponderID,Agency,State,SignedBalance," +
                       "CollectedAmt,ChargedAmt,FullAmt,LowBattery,TransponderStatus,LowBalance,InsufficentFunds,CreditListNum," +
                       "CreditListUpdated,AntiPassbackCode,AVCClass,TransponderClass,RevenueType,ViolationCode,ReaderConfirmation," +
                       "AmtechTransponderNumber,VehicleSpeed,RejectReasonCode",
            Fetch: FetchOtoDetailRowsAsync,
            WriteRows: Write5625Rows,
            ChunkDays: 1),

        // Raw multi-table SQL (6192_20261008.sql): violation transactions joined to the TRIP /
        // UFM amendment tables, counted and totalled per road and category over the WHOLE
        // requested range -- so the range goes to SQL in one call (ChunkDays well above the
        // 31-day web limit), not weekly, or the categories would repeat once per chunk.
        // The name here is just a display label, not a real SQL object.
        ["6192"] = new ImportTypeConfig(
            ProcedureName: "[raw-sql].[6192-ViolationCategories]",
            HeaderRow: "RoadWay,Category,TripSecondaryCode_Joined,TotalCount,TotalAmount",
            Fetch: FetchViolationCategoryRowsAsync,
            WriteRows: Write6192Rows,
            ChunkDays: 3660),

        // Raw SQL (5680 "ByTransmitDate" query, hourly-bucket version): ETCX records successfully
        // transmitted in the requested Eastern-time window, ETC revenue/count per plaza, transaction
        // date and transmit date (both converted to Eastern). The result set is written exactly as
        // the query returns it, including its two blank placeholder columns. The grouping spans the
        // whole range, so the range goes to SQL in one call. The name here is just a display
        // label, not a real SQL object.
        ["5680"] = new ImportTypeConfig(
            ProcedureName: "[raw-sql].[5680-UFMByTransmitDate]",
            HeaderRow: "OrgName,textbox47_ignore,Roadway_Gantry,PlazaID,ETC_Revenue,ETC_Count,TransactionDate,DateTransmitted",
            Fetch: FetchTransmitSummaryRowsAsync,
            WriteRows: Write5680Rows,
            ChunkDays: 3660),

        // Raw SQL (5678 export, SQL_TEST/5678_export.sql): one summary row with ETCX and VIOL
        // transaction counts and indicated revenue for the requested Eastern-time window
        // (successfully transmitted records). The whole range is one aggregate, so it goes to SQL in
        // one call and yields one row. The name here is just a display label, not a real SQL object.
        ["5678"] = new ImportTypeConfig(
            ProcedureName: "[raw-sql].[5678-Export]",
            HeaderRow: "Total_UFMID_ETCX_Count,Total_IndicatedRevenueOrig_ETCX,Total_UFMID_VIOL_Count,Total_IndicatedRevenueOrig_VIOL",
            Fetch: FetchTotalsSummaryRowsAsync,
            WriteRows: Write5678Rows,
            ChunkDays: 3660),

        // Raw SQL (5678_V1, SQL_TEST/5678_V1_export.sql): violation count and indicated revenue per
        // transmit date and transaction date (no plaza). Same optimized hourly-bucket form as 5695.
        // The grouping spans the whole range, so the range goes to SQL in one call. The name here is
        // just a display label, not a real SQL object.
        ["5678_V1"] = new ImportTypeConfig(
            ProcedureName: "[raw-sql].[5678_V1-ViolationsByDate]",
            HeaderRow: "DateTransmitted,TransactionDate,TransactionCount,IndicatedRevenueOrig",
            Fetch: FetchViolByDateRowsAsync,
            WriteRows: Write5678V1Rows,
            ChunkDays: 3660),

        // Same source rows as 1310 (shares its procedure, one SP call covers both),
        // but drops Date/Hour (columns C/D) in favor of a LaneNumber column, and
        // sums across every date/hour in the whole requested range per lane --
        // not per gantry+hour like 1310, and not per chunk. Only Finalize writes
        // the accumulated totals, once, after all chunks are processed.
        ["1320"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_HourlyTraffic_1315b_1320b]",
            HeaderRow: "Roadway,Gantry,Lane,VT axle 2,VT axle 3,VT axle 4,VT axle 5,VT axle 6+,VT," +
                       "SP axle 2,SP axle 3,SP axle 4,SP axle 5,SP axle 6+,SP,Non-Rev,All Trans",
            Fetch: FetchTrafficRowsAsync,
            WriteRows: Accumulate1320Rows,
            Finalize: Finalize1320Rows)
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var (startDate, endDate, importTypeNames, outputFolder) = ParseArguments(args);
            var importTypes = importTypeNames.Select(t => (Name: t, Config: ImportTypes[t])).ToList();

            var settings = LoadOrPromptSqlSettings();
            var connectionString = BuildConnectionString(settings.Sql);

            var sessionId = await InsertSessionStartAsync(settings.SessionSql);

            Console.WriteLine($"Session ID : {sessionId}");
            Console.WriteLine($"Import type(s): {string.Join(", ", importTypeNames)}");
            Console.WriteLine($"Date range : {startDate:yyyy-MM-dd} .. {endDate:yyyy-MM-dd}");

            var runNow = DateTime.Now;
            var outputPaths = new Dictionary<string, string>();
            var writers = new Dictionary<string, StreamWriter>();
            var totalRowsByType = new Dictionary<string, long>();

            foreach (var (name, config) in importTypes)
            {
                var outputPath = BuildOutputPath(outputFolder, name, startDate, endDate, runNow);
                outputPaths[name] = outputPath;
                totalRowsByType[name] = 0;

                var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
                writer.Write(config.HeaderRow);
                writer.Write("\r\n");
                writers[name] = writer;

                Console.WriteLine($"Output file [{name}]: {Path.GetFullPath(outputPath)}");
                await InsertOutFileStartAsync(settings.SessionSql, sessionId, name, Path.GetFileName(outputPath), startDate, endDate);
            }
            Console.WriteLine();

            var filesProcessed = 0;
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var failures = new List<string>();

                // Group requested import types by shared procedure so each procedure is
                // called exactly once per chunk, regardless of how many import types
                // consume its result. @OrgID = 0 returns every OrgName in one call.
                var byProcedure = importTypes.GroupBy(t => t.Config.ProcedureName).ToList();

                await using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    foreach (var procGroup in byProcedure)
                    {
                        // Types sharing a procedure share one call, so the smallest chunk wins.
                        var chunkDays = procGroup.Min(t => t.Config.ChunkDays);
                        foreach (var (chunkStart, chunkEnd) in SplitIntoWeeklyChunks(startDate, endDate, chunkDays))
                        {
                            var typeNames = string.Join("+", procGroup.Select(t => t.Name));
                            Console.WriteLine($"[{typeNames}]  {chunkStart:yyyy-MM-dd} .. {chunkEnd:yyyy-MM-dd} ...");
                            try
                            {
                                var fetch = procGroup.First().Config.Fetch;
                                var rawRows = await fetch(connection, settings.Sql, procGroup.Key, chunkStart, chunkEnd);
                                foreach (var (name, config) in procGroup)
                                {
                                    var count = config.WriteRows(rawRows, writers[name]);
                                    totalRowsByType[name] += count;
                                    Console.WriteLine($"    [{name}] {count} row(s)");
                                }
                            }
                            catch (Exception ex)
                            {
                                var message = $"{chunkStart:yyyy-MM-dd}..{chunkEnd:yyyy-MM-dd}: {ex.Message}";
                                failures.Add(message);
                                Console.Error.WriteLine($"    FAILED: {ex.Message}");
                            }
                        }
                    }
                }

                foreach (var (name, config) in importTypes)
                {
                    if (config.Finalize is null)
                        continue;

                    var finalCount = config.Finalize(writers[name]);
                    totalRowsByType[name] = finalCount;
                    Console.WriteLine($"[{name}] finalized: {finalCount} row(s)");
                }

                foreach (var writer in writers.Values)
                    await writer.DisposeAsync();

                filesProcessed = importTypes.Count;
                stopwatch.Stop();

                Console.WriteLine();
                var totalRows = totalRowsByType.Values.Sum();
                Console.WriteLine($"Done. {totalRows} row(s) written in {stopwatch.Elapsed.TotalSeconds:F1} sec.");
                foreach (var (name, _) in importTypes)
                    Console.WriteLine($"  [{name}] {totalRowsByType[name]} row(s) -> {outputPaths[name]}");

                if (failures.Count > 0)
                {
                    Console.WriteLine($"{failures.Count} call(s) failed:");
                    foreach (var f in failures)
                        Console.WriteLine($"  - {f}");
                    return 1;
                }

                return 0;
            }
            finally
            {
                foreach (var (name, _) in importTypes)
                {
                    var outputPath = outputPaths[name];
                    var fileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : (long?)null;
                    await UpdateOutFileEndAsync(settings.SessionSql, sessionId, Path.GetFileName(outputPath), totalRowsByType[name], fileSize);
                }
                await UpdateSessionEndAsync(settings.SessionSql, sessionId, filesProcessed);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<object> FetchTrafficRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@OrgID", System.Data.SqlDbType.VarChar, 50) { Value = AllOrgsId });
        command.Parameters.Add(new SqlParameter("@StartDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inDayType", System.Data.SqlDbType.VarChar, 10) { Value = "0" });
        command.Parameters.Add(new SqlParameter("@inLaneGroupID", System.Data.SqlDbType.VarChar, 10) { Value = "0" });

        // Source column layout matches #TMP_EXECRESULT in 1310_Proc_Result.sql:
        // 0 OrgName, 1 LaneNumber, 2 LaneGroupName, 3 TransDate, 4 TransHour,
        // 5..23 the 19 numeric measures, 24 TollDay, 25 LaneGroupID.
        var rows = new List<RawRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                var measures = new long[MeasureColumnCount];
                for (var m = 0; m < MeasureColumnCount; m++)
                    measures[m] = reader.IsDBNull(SourceFirstMeasureColumn + m) ? 0 : Convert.ToInt64(reader.GetValue(SourceFirstMeasureColumn + m), CultureInfo.InvariantCulture);

                rows.Add(new RawRow(
                    OrgName: reader.GetString(0),
                    LaneGroupName: reader.GetString(2),
                    LaneNumber: reader.GetInt32(1),
                    TransDate: reader.GetDateTime(3).Date,
                    TransHour: reader.GetInt32(4),
                    TollDay: reader.GetDateTime(24),
                    LaneGroupId: reader.GetInt32(25),
                    Measures: measures));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private const int SourceFirstMeasureColumn = 5; // index into the SP's result set (still has LaneNumber at 1)
    private const int MeasureColumnCount = 19; // VIOL2 .. ETCIVISAxles (source order)

    // Measures, in source order: 0 VIOL2, 1 VIOL3, 2 VIOL4, 3 VIOL5, 4 VIOL6_9, 5 VIOL,
    // 6 VIOLIndAxles, 7 VIOLIVISAxles, 8 AR, 9 CardNonRev, 10 PassNR, 11 ETC2, 12 ETC3,
    // 13 ETC4, 14 ETC5, 15 ETC6_9, 16 NRETC, 17 ETCIndAxles, 18 ETCIVISAxles.
    private const int SrcVIOL = 5;
    private const int SrcAR = 8, SrcCardNonRev = 9, SrcPassNR = 10;
    private const int SrcETC2 = 11, SrcETC3 = 12, SrcETC4 = 13, SrcETC5 = 14, SrcETC6_9 = 15;
    private const int SrcNRETC = 16;

    // ---- 1310: gantry + hour, lanes summed together ----

    private sealed record GantryHourKey(string OrgName, string LaneGroupName, DateTime TransDate, int TransHour, DateTime TollDay, int LaneGroupId);

    private static long Write1310Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<RawRow>)rowsObj;
        var groups = new Dictionary<GantryHourKey, long[]>();
        foreach (var row in rows)
        {
            var key = new GantryHourKey(row.OrgName, row.LaneGroupName, row.TransDate, row.TransHour, row.TollDay, row.LaneGroupId);
            if (!groups.TryGetValue(key, out var sums))
            {
                sums = new long[MeasureColumnCount];
                groups[key] = sums;
            }

            for (var m = 0; m < MeasureColumnCount; m++)
                sums[m] += row.Measures[m];
        }

        // Hour is no longer printed, so write rows out in a stable, deterministic
        // order (Dictionary enumeration order is not guaranteed).
        foreach (var pair in groups
            .OrderBy(p => p.Key.OrgName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.LaneGroupName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.TransDate)
            .ThenBy(p => p.Key.TransHour))
        {
            Write1310Row(writer, pair.Key, pair.Value);
        }

        return groups.Count;
    }

    private static void Write1310Row(StreamWriter writer, GantryHourKey key, long[] sums)
    {
        var etcSum = sums[SrcETC2] + sums[SrcETC3] + sums[SrcETC4] + sums[SrcETC5] + sums[SrcETC6_9]; // P
        var nrTotal = sums[SrcAR] + sums[SrcCardNonRev] + sums[SrcPassNR] + sums[SrcNRETC]; // U: NR_TOTAL = Q+R+S+T
        var grandTotal = sums[SrcVIOL] + etcSum + nrTotal; // V: J (VIOL) + P + U

        // VIOLIndAxles/VIOLIVISAxles and ETCIndAxles/ETCIVISAxles (old columns K/L, V/W)
        // and TollDay/LaneGroupID (X/Y) are dropped. The ETC2..ETC6_9 sum sits right
        // before AR/CardNonRev/PassNR, which now sit right before NRETC. NR_TOTAL
        // (=AR+CardNonRev+PassNR+NRETC) and then GrandTotal (J+P+U) are appended at the end.
        object?[] fields =
        [
            $"Facility: {key.OrgName}",
            $"Lane Group: {key.LaneGroupName}",
            $"Date: {key.TransDate:MM/dd/yyyy}",
            $"{key.TransHour:D2}:00",
            sums[0], sums[1], sums[2], sums[3], sums[4], sums[SrcVIOL], // VIOL2, VIOL3, VIOL4, VIOL5, VIOL6_9, VIOL
            sums[SrcETC2], sums[SrcETC3], sums[SrcETC4], sums[SrcETC5], sums[SrcETC6_9],
            etcSum,
            sums[SrcAR], sums[SrcCardNonRev], sums[SrcPassNR],
            sums[SrcNRETC],
            nrTotal,
            grandTotal
        ];

        WriteCsvLine(writer, fields);
    }

    // ---- 1270: lane + day, hours summed together, lanes kept separate ----

    private sealed record LaneDayKey(string OrgName, string LaneGroupName, DateTime TransDate, int LaneNumber);

    private static long Write1270Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<RawRow>)rowsObj;
        var groups = new Dictionary<LaneDayKey, long[]>();
        foreach (var row in rows)
        {
            var key = new LaneDayKey(row.OrgName, row.LaneGroupName, row.TransDate, row.LaneNumber);
            if (!groups.TryGetValue(key, out var sums))
            {
                sums = new long[MeasureColumnCount];
                groups[key] = sums;
            }

            for (var m = 0; m < MeasureColumnCount; m++)
                sums[m] += row.Measures[m];
        }

        foreach (var pair in groups
            .OrderBy(p => p.Key.OrgName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.LaneGroupName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.TransDate)
            .ThenBy(p => p.Key.LaneNumber))
        {
            Write1270Row(writer, pair.Key, pair.Value);
        }

        return groups.Count;
    }

    private static void Write1270Row(StreamWriter writer, LaneDayKey key, long[] sums)
    {
        var etcSum = sums[SrcETC2] + sums[SrcETC3] + sums[SrcETC4] + sums[SrcETC5] + sums[SrcETC6_9];

        object?[] fields =
        [
            $"Plaza: {key.OrgName}",
            $"Date: {key.TransDate:MM/dd/yyyy}",
            $"LaneGroup: {key.LaneGroupName}",
            $"Lane {key.LaneNumber:D2}",
            etcSum,
            0,
            sums[SrcNRETC],
            sums[SrcVIOL]
        ];

        WriteCsvLine(writer, fields);
    }

    // ---- 1320: gantry lanes kept separate, summed across the WHOLE requested range ----

    private sealed record LaneTotalKey(string OrgName, string LaneGroupName, int LaneNumber);

    // Accumulates across every chunk in the run instead of writing per chunk --
    // 1320's grouping (OrgName, LaneGroupName, LaneNumber) has no Date/Hour, so a
    // per-chunk row would just be a partial total for multi-week ranges.
    private static readonly Dictionary<LaneTotalKey, long[]> LaneTotals = new();

    private static long Accumulate1320Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<RawRow>)rowsObj;
        foreach (var row in rows)
        {
            var key = new LaneTotalKey(row.OrgName, row.LaneGroupName, row.LaneNumber);
            if (!LaneTotals.TryGetValue(key, out var sums))
            {
                sums = new long[MeasureColumnCount];
                LaneTotals[key] = sums;
            }

            for (var m = 0; m < MeasureColumnCount; m++)
                sums[m] += row.Measures[m];
        }

        return LaneTotals.Count;
    }

    private static long Finalize1320Rows(StreamWriter writer)
    {
        foreach (var pair in LaneTotals
            .OrderBy(p => p.Key.OrgName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.LaneGroupName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.LaneNumber))
        {
            Write1320Row(writer, pair.Key, pair.Value);
        }

        return LaneTotals.Count;
    }

    private static void Write1320Row(StreamWriter writer, LaneTotalKey key, long[] sums)
    {
        var etcSum = sums[SrcETC2] + sums[SrcETC3] + sums[SrcETC4] + sums[SrcETC5] + sums[SrcETC6_9];
        var nonRev = sums[SrcNRETC];
        var allTrans = sums[SrcVIOL] + etcSum + nonRev;

        // AR/CardNonRev/PassNR and the 4 blank columns are dropped entirely to
        // match the legacy 1320 report's layout -- straight to Non-Rev (=NRETC)
        // and All Trans (=VIOL + ETC_Sum + NRETC, no longer including AR/CardNonRev/PassNR).
        object?[] fields =
        [
            $"Facility: {key.OrgName}",
            $"Lane Group: {key.LaneGroupName}",
            key.LaneNumber,
            sums[0], sums[1], sums[2], sums[3], sums[4], sums[SrcVIOL], // VIOL2, VIOL3, VIOL4, VIOL5, VIOL6_9, VIOL
            sums[SrcETC2], sums[SrcETC3], sums[SrcETC4], sums[SrcETC5], sums[SrcETC6_9],
            etcSum,
            nonRev,
            allTrans
        ];

        WriteCsvLine(writer, fields);
    }

    // ---- 2725: straight passthrough of [dbo].[uspr_aud_PlazaRevenue_sql] ----

    private static async Task<object> FetchRevenueRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@OrgID", System.Data.SqlDbType.VarChar, 50) { Value = AllOrgsId });
        command.Parameters.Add(new SqlParameter("@StartDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date });

        var rows = new List<RevenueRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var laneGroupNameOrdinal = reader.GetOrdinal("LaneGroupName");
            var expectedRevenueOrdinal = reader.GetOrdinal("ExpectedRevenue");
            var isfOrdinal = reader.GetOrdinal("ISF");
            var dateDroppedOrdinal = reader.GetOrdinal("DateDropped");

            while (await reader.ReadAsync())
            {
                rows.Add(new RevenueRow(
                    LaneGroupName: reader.GetString(laneGroupNameOrdinal),
                    ExpectedRevenue: reader.IsDBNull(expectedRevenueOrdinal) ? 0m : reader.GetDecimal(expectedRevenueOrdinal),
                    Isf: reader.IsDBNull(isfOrdinal) ? 0m : reader.GetDecimal(isfOrdinal),
                    DateDropped: reader.GetDateTime(dateDroppedOrdinal)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write2725Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<RevenueRow>)rowsObj;
        foreach (var row in rows)
        {
            object?[] fields =
            [
                row.LaneGroupName,
                FormatCurrency(row.ExpectedRevenue),
                FormatCurrency(row.Isf),
                $"{row.DateDropped:MM/dd/yyyy}"
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    private static string FormatCurrency(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    // ---- 2552: [RPrl].[uspr_trn_Leakage], grouped by OrgID/OrgName/LaneNumber/TransDate ----

    // Measures, in #TMP_2552 column order (ExecutionID excluded):
    private const int SrcSentToIPS = 0, SrcOutstanding = 1, SrcReceivedFromIPS = 2, SrcUncollectableNonSystem = 3;
    private const int SrcUncollectableNonSystemAmount = 4, SrcUncollectableSystem = 5, SrcUncollectableSystemAmount = 6;
    private const int SrcCollectable = 7, SrcIPSRejected = 8, SrcAmendment = 9, SrcFlush = 10, SrcNoImage = 11, SrcIPS = 12, SrcInProcess = 13;
    private const int LeakageMeasureCount = 14;

    private static async Task<object> FetchLeakageRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@inOrgID", System.Data.SqlDbType.Int) { Value = 0 });
        command.Parameters.Add(new SqlParameter("@inStartDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@inEndDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inLaneGroupID", System.Data.SqlDbType.VarChar, 10) { Value = "0" });

        var rows = new List<LeakageRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var orgIdOrdinal = reader.GetOrdinal("OrgID");
            var orgNameOrdinal = reader.GetOrdinal("OrgName");
            var laneNumberOrdinal = reader.GetOrdinal("LaneNumber");
            var transDateOrdinal = reader.GetOrdinal("TransDate");
            var measureOrdinals = new[]
            {
                reader.GetOrdinal("SentToIPS"), reader.GetOrdinal("Outstanding"), reader.GetOrdinal("ReceivedFromIPS"),
                reader.GetOrdinal("UncollectableNonSystem"), reader.GetOrdinal("UncollectableNonSystemAmount"),
                reader.GetOrdinal("UncollectableSystem"), reader.GetOrdinal("UncollectableSystemAmount"),
                reader.GetOrdinal("Collectable"), reader.GetOrdinal("IPSRejected"), reader.GetOrdinal("Amendment"),
                reader.GetOrdinal("Flush"), reader.GetOrdinal("NoImage"), reader.GetOrdinal("IPS"), reader.GetOrdinal("InProcess")
            };

            while (await reader.ReadAsync())
            {
                var measures = new double[LeakageMeasureCount];
                for (var m = 0; m < LeakageMeasureCount; m++)
                    measures[m] = reader.IsDBNull(measureOrdinals[m]) ? 0 : Convert.ToDouble(reader.GetValue(measureOrdinals[m]), CultureInfo.InvariantCulture);

                rows.Add(new LeakageRow(
                    OrgId: Convert.ToInt64(reader.GetValue(orgIdOrdinal), CultureInfo.InvariantCulture),
                    OrgName: reader.GetString(orgNameOrdinal),
                    LaneNumber: reader.GetInt32(laneNumberOrdinal),
                    TransDate: reader.GetDateTime(transDateOrdinal).Date,
                    Measures: measures));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private sealed record LeakageKey(long OrgId, string OrgName, int LaneNumber, DateTime TransDate);

    private static long Write2552Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<LeakageRow>)rowsObj;
        var groups = new Dictionary<LeakageKey, double[]>();
        foreach (var row in rows)
        {
            var key = new LeakageKey(row.OrgId, row.OrgName, row.LaneNumber, row.TransDate);
            if (!groups.TryGetValue(key, out var sums))
            {
                sums = new double[LeakageMeasureCount];
                groups[key] = sums;
            }

            for (var m = 0; m < LeakageMeasureCount; m++)
                sums[m] += row.Measures[m];
        }

        foreach (var pair in groups
            .OrderBy(p => p.Key.OrgId)
            .ThenBy(p => p.Key.LaneNumber)
            .ThenBy(p => p.Key.TransDate))
        {
            Write2552Row(writer, pair.Key, pair.Value);
        }

        return groups.Count;
    }

    // The 9 caption strings repeated verbatim on every row (columns B-J), matching
    // the legacy "09-01-26 Leakage Report 2552.csv" export exactly, embedded
    // newlines included.
    private static readonly object[] LeakageCaptions =
    [
        "Sent\r\nTo IPS", "Rejected", "Awaiting\r\nResponse", "Returned\r\nFrom IPS",
        "Uncollectable\r\nNon-System", "Uncollectable\r\nSystem", "Flushed", "No \r\nImage", "In \r\nProcess"
    ];

    private static void Write2552Row(StreamWriter writer, LeakageKey key, double[] sums)
    {
        var inProcess = (long)sums[SrcInProcess];

        object?[] fields =
        [
            $"Facility: {key.OrgName}",
            .. LeakageCaptions,
            key.LaneNumber,
            (long)sums[SrcSentToIPS],
            (long)sums[SrcIPSRejected],
            (long)sums[SrcOutstanding],
            (long)sums[SrcReceivedFromIPS],
            (long)sums[SrcCollectable],
            (long)sums[SrcUncollectableNonSystem],
            FormatCurrency((decimal)sums[SrcUncollectableNonSystemAmount]),
            (long)sums[SrcUncollectableSystem],
            FormatCurrency((decimal)sums[SrcUncollectableSystemAmount]),
            (long)sums[SrcFlush],
            (long)sums[SrcNoImage],
            inProcess, // W
            0, // X
            (long)sums[SrcIPS],
            inProcess // Z, same value as W
        ];

        WriteCsvLine(writer, fields);
    }

    // ---- 5695: violation summary by transmit date / transaction date / plaza, raw SQL ----

    // Optimized version of the row-level query: hour buckets first, time-zone conversion only on
    // the small grouped set. Output verified identical to the row-level original (169 rows, 7/22).
    // @STDate/@ENDDate are the Eastern days (@ENDDate exclusive); the Eastern window is converted to UTC
    // in SQL. TransDate is already local time, so only the transmit time is converted.
    private const string ViolSummarySql = """
        SET NOCOUNT ON;

        DECLARE @STDate_UTC  datetime = CAST(@STDate  AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';
        DECLARE @ENDDate_UTC datetime = CAST(@ENDDate AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';

        WITH base AS (
            SELECT
                -- truncate to the hour first; AT TIME ZONE then runs per hour bucket, not per row
                DATEADD(HOUR, DATEDIFF(HOUR, 0, ubd.UTCDateTransmitted), 0) AS SentHourUTC,
                CAST(trv.TransDate AS date)                                 AS TransDay,
                trv.OrgID, ur.PlazaID, ur.IndicatedRevenueOrig
            FROM ICD.dbo.tblUFMBatchDetail   ubd WITH (NOLOCK)
            JOIN ICD.dbo.tblCoopTagTollUsage trv WITH (NOLOCK) ON trv.TransID = ubd.UFMID
            LEFT JOIN ICD.dbo.tblUFMRecord   ur  WITH (NOLOCK) ON ur.UFMID   = ubd.UFMID
            WHERE ubd.UTCDateTransmitted >= @STDate_UTC
              AND ubd.UTCDateTransmitted <  @ENDDate_UTC
              AND ubd.DispCode = 'SUC'
              AND trv.TransType = 'VIOL'
        ),
        hourly AS (
            SELECT SentHourUTC, TransDay, OrgID, PlazaID,
                   SUM(IndicatedRevenueOrig) AS Rev,
                   COUNT(*)                  AS Cnt
            FROM base
            GROUP BY SentHourUTC, TransDay, OrgID, PlazaID
        ),
        local AS (
            SELECT h.*,
                   CAST(SentHourUTC AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS date) AS SentDate
            FROM hourly h
        )
        SELECT CONVERT(varchar(10), SentDate, 101) AS DateTransmitted,
               CONVERT(varchar(10), TransDay, 101) AS TransactionDate,
               l.PlazaID                           AS PlazaID,
               FORMAT(SUM(Cnt), 'N0')              AS TransactionCount,
               FORMAT(SUM(Rev), 'C', 'en-US')      AS IndicatedRevenueOrig
        FROM local l
        JOIN CPC.dbo.tblOrg o WITH (NOLOCK) ON o.OrgID = l.OrgID
        GROUP BY SentDate, TransDay, l.PlazaID
        ORDER BY SentDate, TransDay, l.PlazaID
        OPTION (RECOMPILE);
        """;

    private static async Task<object> FetchViolSummaryRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ViolSummarySql;
        command.CommandTimeout = Math.Max(settings.CommandTimeoutSeconds, 7200);

        command.Parameters.Add(new SqlParameter("@STDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        // The query's end date is exclusive; the program's end date is inclusive.
        command.Parameters.Add(new SqlParameter("@ENDDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date.AddDays(1) });

        var rows = new List<ViolSummaryRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount != 5)
                continue;

            string Text(int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";

            while (await reader.ReadAsync())
            {
                rows.Add(new ViolSummaryRow(Text(0), Text(1), Text(2), Text(3), Text(4)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write5695Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<ViolSummaryRow>)rowsObj;
        foreach (var row in rows)
        {
            // Values are already the text the query formats ("12,600", "$4,218.39") and PlazaID
            // is an identifier -- written as strings, quoted only when they contain a comma.
            object?[] fields =
            [
                row.DateTransmitted, row.TransactionDate, row.PlazaId, row.TransactionCount, row.IndicatedRevenue
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    // ---- 5678: ETC / VIOL totals for the window, one summary row, raw SQL ----

    private sealed record TotalsSummaryRow(long EtcCount, decimal EtcRevenue, long ViolCount, decimal ViolRevenue);

    // @STDate/@ENDDate are the Eastern days (@ENDDate exclusive); the Eastern window is converted to UTC
    // in SQL with the Windows time zone, so it is correct year-round (no hard-coded UTC offset).
    private const string TotalsSummarySql = """
        SET NOCOUNT ON;

        DECLARE @STDate_UTC  datetime = CAST(@STDate  AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';
        DECLARE @ENDDate_UTC datetime = CAST(@ENDDate AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';

        SELECT
            COUNT(CASE WHEN trv.TransType = 'ETCX' THEN ubd.UFMID END) AS Total_UFMID_ETCX_Count,
            SUM(CASE WHEN trv.TransType = 'ETCX' THEN ur.IndicatedRevenueOrig ELSE 0 END) AS Total_IndicatedRevenueOrig_ETCX,
            COUNT(CASE WHEN trv.TransType = 'VIOL' THEN ubd.UFMID END) AS Total_UFMID_VIOL_Count,
            SUM(CASE WHEN trv.TransType = 'VIOL' THEN ur.IndicatedRevenueOrig ELSE 0 END) AS Total_IndicatedRevenueOrig_VIOL
        FROM [ICD].[dbo].[tblUFMBatchDetail] ubd WITH (NOLOCK)
        INNER JOIN [ICD].[dbo].[tblCoopTagTollUsage] trv WITH (NOLOCK) ON ubd.UFMID = trv.TransID
        LEFT OUTER JOIN [ICD].[dbo].[tblUFMRecord] ur WITH (NOLOCK) ON ubd.UFMID = ur.UFMID
        WHERE ubd.UTCDateTransmitted >= @STDate_UTC
          AND ubd.UTCDateTransmitted <  @ENDDate_UTC
          AND ubd.DispCode = 'SUC'
          AND trv.TransType IN ('VIOL', 'ETCX')
        OPTION (RECOMPILE);   -- lets the optimizer see the date variables (without it the plan is far slower)
        """;

    private static async Task<object> FetchTotalsSummaryRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = TotalsSummarySql;
        command.CommandTimeout = Math.Max(settings.CommandTimeoutSeconds, 7200);

        command.Parameters.Add(new SqlParameter("@STDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        // The query's end date is exclusive; the program's end date is inclusive.
        command.Parameters.Add(new SqlParameter("@ENDDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date.AddDays(1) });

        var rows = new List<TotalsSummaryRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount != 4)
                continue;

            while (await reader.ReadAsync())
            {
                rows.Add(new TotalsSummaryRow(
                    EtcCount: reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    EtcRevenue: reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture),
                    ViolCount: reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    ViolRevenue: reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write5678Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<TotalsSummaryRow>)rowsObj;
        foreach (var row in rows)
        {
            // Counts get the usual thousands separator; revenue is plain 2-decimal text with grouping.
            object?[] fields =
            [
                row.EtcCount, FormatCurrency(row.EtcRevenue),
                row.ViolCount, FormatCurrency(row.ViolRevenue)
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    // ---- 5678_V1: violations by transmit date / transaction date, raw SQL ----

    private sealed record ViolByDateRow(string DateTransmitted, string TransactionDate, string TransactionCount, string IndicatedRevenue);

    // Same query as 5695 without the plaza column. @STDate/@ENDDate are the Eastern days (@ENDDate
    // exclusive); the window is converted to UTC in SQL with the Windows time zone. TransDate is
    // already local time, so only the transmit time is converted.
    private const string ViolByDateSql = """
        SET NOCOUNT ON;

        DECLARE @STDate_UTC  datetime = CAST(@STDate  AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';
        DECLARE @ENDDate_UTC datetime = CAST(@ENDDate AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';

        WITH base AS (
            SELECT
                -- truncate to the hour first; AT TIME ZONE then runs per hour bucket, not per row
                DATEADD(HOUR, DATEDIFF(HOUR, 0, ubd.UTCDateTransmitted), 0) AS SentHourUTC,
                CAST(trv.TransDate AS date)                                 AS TransDay,
                trv.OrgID, ur.IndicatedRevenueOrig
            FROM ICD.dbo.tblUFMBatchDetail   ubd WITH (NOLOCK)
            JOIN ICD.dbo.tblCoopTagTollUsage trv WITH (NOLOCK) ON trv.TransID = ubd.UFMID
            LEFT JOIN ICD.dbo.tblUFMRecord   ur  WITH (NOLOCK) ON ur.UFMID   = ubd.UFMID
            WHERE ubd.UTCDateTransmitted >= @STDate_UTC
              AND ubd.UTCDateTransmitted <  @ENDDate_UTC
              AND ubd.DispCode = 'SUC'
              AND trv.TransType = 'VIOL'
        ),
        hourly AS (
            SELECT SentHourUTC, TransDay, OrgID,
                   SUM(IndicatedRevenueOrig) AS Rev,
                   COUNT(*)                  AS Cnt
            FROM base
            GROUP BY SentHourUTC, TransDay, OrgID
        ),
        local AS (
            SELECT h.*,
                   CAST(SentHourUTC AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS date) AS SentDate
            FROM hourly h
        )
        SELECT CONVERT(varchar(10), SentDate, 101) AS DateTransmitted,
               CONVERT(varchar(10), TransDay, 101) AS TransactionDate,
               FORMAT(SUM(Cnt), 'N0')              AS TransactionCount,
               FORMAT(SUM(Rev), 'C', 'en-US')      AS IndicatedRevenueOrig
        FROM local l
        JOIN CPC.dbo.tblOrg o WITH (NOLOCK) ON o.OrgID = l.OrgID
        GROUP BY SentDate, TransDay
        ORDER BY SentDate, TransDay
        OPTION (RECOMPILE);
        """;

    private static async Task<object> FetchViolByDateRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ViolByDateSql;
        command.CommandTimeout = Math.Max(settings.CommandTimeoutSeconds, 7200);

        command.Parameters.Add(new SqlParameter("@STDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        // The query's end date is exclusive; the program's end date is inclusive.
        command.Parameters.Add(new SqlParameter("@ENDDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date.AddDays(1) });

        var rows = new List<ViolByDateRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount != 4)
                continue;

            string Text(int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";

            while (await reader.ReadAsync())
            {
                rows.Add(new ViolByDateRow(Text(0), Text(1), Text(2), Text(3)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write5678V1Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<ViolByDateRow>)rowsObj;
        foreach (var row in rows)
        {
            // Values are already the text the query formats ("12,600", "$4,218.39"): written as
            // strings, quoted only when they contain a comma.
            object?[] fields = [row.DateTransmitted, row.TransactionDate, row.TransactionCount, row.IndicatedRevenue];
            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    // ---- 4438: [dbo].[uspr_csc_UniqueTransponderReport_Sarad_Migration], rolled up while streaming ----

    private static readonly Regex RoadCodeRegex = new(@"^\s*SR\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "SR836EAST(at97thAve)" / "SR836West(87thAveRamp)" -> "SR 836". Anything that
    // doesn't start with SR<digits> keeps its raw name so it shows up instead of vanishing.
    private static string RoadGroupName(string roadName)
    {
        var m = RoadCodeRegex.Match(roadName);
        return m.Success ? "SR " + m.Groups[1].Value : roadName.Trim();
    }

    private static async Task<object> FetchUniqueTransponderRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@inStartDate", System.Data.SqlDbType.DateTime) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@inEndDate", System.Data.SqlDbType.DateTime) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inReportType", System.Data.SqlDbType.TinyInt) { Value = (byte)1 });
        command.Parameters.Add(new SqlParameter("@inDateType", System.Data.SqlDbType.TinyInt) { Value = (byte)1 });
        command.Parameters.Add(new SqlParameter("@inOrgID", System.Data.SqlDbType.BigInt) { Value = 0L });

        // A chunk is millions of raw rows, so aggregate while streaming instead of
        // keeping them. The distinct-transponder sets are per (date,state,agency,type).
        var groups = new Dictionary<(DateTime Date, string State, string Agency, string RevType),
            (SortedDictionary<string, (long Count, long Cents)> ByRoad, HashSet<string> Transponders)>();
        var unknownRoads = new HashSet<string>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var roadOrdinal = reader.GetOrdinal("RoadName");
            var laneExitOrdinal = reader.GetOrdinal("LaneExitDate");
            var transponderOrdinal = reader.GetOrdinal("TransponderID");
            var agencyOrdinal = reader.GetOrdinal("Agency");
            var stateOrdinal = reader.GetOrdinal("State");
            var chargedOrdinal = reader.GetOrdinal("ChargedAmt");
            var revenueTypeOrdinal = reader.GetOrdinal("RevenueType");

            while (await reader.ReadAsync())
            {
                // LaneExitDate is text like "07/01/2026  000000"; only the date part is used.
                var laneExit = reader.GetString(laneExitOrdinal);
                var date = DateTime.ParseExact(laneExit.AsSpan(0, 10), "MM/dd/yyyy", CultureInfo.InvariantCulture);

                var road = RoadGroupName(reader.IsDBNull(roadOrdinal) ? "" : reader.GetString(roadOrdinal));
                if (!road.StartsWith("SR ", StringComparison.Ordinal))
                    unknownRoads.Add(road);

                var key = (
                    date,
                    reader.GetString(stateOrdinal).Trim(),
                    reader.GetString(agencyOrdinal).Trim(),
                    reader.GetString(revenueTypeOrdinal).Trim());

                if (!groups.TryGetValue(key, out var group))
                {
                    group = (new SortedDictionary<string, (long, long)>(StringComparer.Ordinal), new HashSet<string>());
                    groups[key] = group;
                }

                var cents = reader.IsDBNull(chargedOrdinal)
                    ? 0L
                    : long.Parse(reader.GetString(chargedOrdinal).Trim(), CultureInfo.InvariantCulture);
                group.ByRoad.TryGetValue(road, out var existing);
                group.ByRoad[road] = (existing.Count + 1, existing.Cents + cents);

                group.Transponders.Add(reader.GetString(transponderOrdinal).Trim());
            }
        }
        while (await reader.NextResultAsync());

        if (unknownRoads.Count > 0)
            Console.WriteLine($"    [4438] WARNING: road name(s) not in SR<number> form: {string.Join(" | ", unknownRoads.Select(r => r.Length == 0 ? "(blank)" : r))}");

        // Every group lists every road seen in the chunk (zero-filled), like the sample does.
        var allRoads = groups.Values.SelectMany(g => g.ByRoad.Keys).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

        return groups
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => g.Key.State, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Agency, StringComparer.Ordinal)
            .ThenBy(g => g.Key.RevType, StringComparer.Ordinal)
            .Select(g =>
            {
                var byRoad = new SortedDictionary<string, (long Count, long Cents)>(StringComparer.Ordinal);
                foreach (var road in allRoads)
                    byRoad[road] = g.Value.ByRoad.TryGetValue(road, out var v) ? v : (0, 0);
                return new TransponderGroup(g.Key.Date, g.Key.State, g.Key.Agency, g.Key.RevType, byRoad, g.Value.Transponders.Count);
            })
            .ToList();
    }

    // "$7,644.45 " -- the trailing space is part of the SSRS currency format in the sample.
    private static string FormatDollarsFromCents(long cents) =>
        "$" + (cents / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture) + " ";

    private static long Write4438Rows(object rowsObj, StreamWriter writer)
    {
        var groups = (IReadOnlyList<TransponderGroup>)rowsObj;
        long written = 0;
        foreach (var group in groups)
        {
            var totalCount = group.ByRoad.Values.Sum(v => v.Count);
            var totalCents = group.ByRoad.Values.Sum(v => v.Cents);

            foreach (var (road, value) in group.ByRoad)
            {
                object?[] fields =
                [
                    "Lane Date",
                    road,
                    group.Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                    group.State,
                    group.Agency,
                    group.RevenueType,
                    (long)group.DistinctTransponders,
                    value.Count,
                    FormatDollarsFromCents(value.Cents),
                    totalCount,
                    FormatDollarsFromCents(totalCents)
                ];

                WriteCsvLine(writer, fields);
                written++;
            }
        }

        return written;
    }

    // ---- 5680: UFM transmit summary, raw SQL ----

    private sealed record TransmitSummaryRow(
        string OrgName, string Placeholder1, string Placeholder2, string PlazaId,
        string EtcRevenue, string EtcCount, string TransactionDate, string DateTransmitted);

    // Straight from the 5680 query. @STDate/@ENDDate replace the script's DECLARE/SELECT
    // (@ENDDate is exclusive, as in the script). The Eastern window is converted to UTC
    // in SQL exactly as the script does.
    private const string TransmitSummarySql = """
        SET NOCOUNT ON;

        DECLARE @STDate_UTC  datetime = CAST(@STDate  AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';
        DECLARE @ENDDate_UTC datetime = CAST(@ENDDate AS datetime2) AT TIME ZONE 'Eastern Standard Time' AT TIME ZONE 'UTC';

        WITH base AS (
            SELECT
                -- truncate to the hour in UTC first; AT TIME ZONE then runs per hour bucket, not per row
                DATEADD(HOUR, DATEDIFF(HOUR, 0, ubd.UTCDateTransmitted), 0) AS SentHourUTC,
                DATEADD(HOUR, DATEDIFF(HOUR, 0, trv.TransDate), 0)          AS TransHourUTC,
                trv.OrgID, trv.TransType, ur.PlazaID, ur.IndicatedRevenueOrig
            FROM ICD.dbo.tblUFMBatchDetail  ubd WITH (NOLOCK)
            JOIN ICD.dbo.tblCoopTagTollUsage trv WITH (NOLOCK) ON trv.TransID = ubd.UFMID
            LEFT JOIN ICD.dbo.tblUFMRecord   ur  WITH (NOLOCK) ON ur.UFMID   = ubd.UFMID
            WHERE ubd.UTCDateTransmitted >= @STDate_UTC
              AND ubd.UTCDateTransmitted <  @ENDDate_UTC
              AND ubd.DispCode = 'SUC'
              AND trv.TransType = 'ETCX'         -- filter early
        ),
        hourly AS (                                           -- shrink to a few hundred rows
            SELECT SentHourUTC, TransHourUTC, OrgID, PlazaID,
                   SUM(ISNULL(IndicatedRevenueOrig,0)) AS EtcRev,
                   COUNT(1)                            AS EtcCnt
            FROM base
            GROUP BY SentHourUTC, TransHourUTC, OrgID, PlazaID
        ),
        local AS (                                            -- time-zone conversion on the small set only
            SELECT h.*,
                   CAST(SentHourUTC  AT TIME ZONE 'UTC' AT TIME ZONE 'Eastern Standard Time' AS date) AS SentDate,
                   CAST(TransHourUTC AS date) AS TransDateLocal   -- TransDate is already local time: no conversion
            FROM hourly h
        )
        SELECT o.OrgName, '' AS textbox47_ignore, '' AS Roadway_Gantry, l.PlazaID,
               FORMAT(ISNULL(SUM(EtcRev),0),  'C',  'en-US') AS ETC_Revenue,
               FORMAT(SUM(EtcCnt),            'N0', 'en-US') AS ETC_Count,
               CONVERT(varchar(10), TransDateLocal, 101) AS TransactionDate,
               CONVERT(varchar(10), SentDate,       101) AS DateTransmitted
        FROM local l
        JOIN CPC.dbo.tblOrg o WITH (NOLOCK) ON o.OrgID = l.OrgID
        GROUP BY SentDate, TransDateLocal, l.PlazaID, o.OrgName
        ORDER BY SentDate, l.PlazaID, TransDateLocal          -- sort on real dates, not strings
        OPTION (RECOMPILE);                                   -- lets the optimizer see the variable values
        """;

    private static async Task<object> FetchTransmitSummaryRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = TransmitSummarySql;
        command.CommandTimeout = Math.Max(settings.CommandTimeoutSeconds, 7200);

        command.Parameters.Add(new SqlParameter("@STDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        // The script's end date is exclusive; the program's end date is inclusive.
        command.Parameters.Add(new SqlParameter("@ENDDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date.AddDays(1) });

        var rows = new List<TransmitSummaryRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount != 8)
                continue;

            string Text(int ordinal) => reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";

            while (await reader.ReadAsync())
            {
                rows.Add(new TransmitSummaryRow(
                    OrgName: Text(0), Placeholder1: Text(1), Placeholder2: Text(2), PlazaId: Text(3),
                    EtcRevenue: Text(4), EtcCount: Text(5),
                    TransactionDate: Text(6), DateTransmitted: Text(7)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write5680Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<TransmitSummaryRow>)rowsObj;
        foreach (var row in rows)
        {
            // All values are already the text the query formats ("$4,218.39", "12,600"), and
            // PlazaID is an identifier -- so they are written as strings, quoted only when they
            // contain a comma.
            object?[] fields =
            [
                row.OrgName, row.Placeholder1, row.Placeholder2, row.PlazaId,
                row.EtcRevenue, row.EtcCount,
                row.TransactionDate, row.DateTransmitted
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    // ---- 6192: violation categories, raw SQL ----

    private sealed record ViolationCategoryRow(string? RoadWay, string Category, string SecondaryCodes, long Count, double Amount);

    // Straight from 6192_20261008.sql. @STDATE/@ENDDATE replace the script's DECLAREs
    // (@ENDDATE is exclusive, as in the script). Temp tables are dropped up front so the
    // batch is safe to run again on the same connection.
    private const string ViolationCategorySql = """
        SET NOCOUNT ON;

        IF OBJECT_ID(N'tempdb..#TMP_PRE', N'U') IS NOT NULL DROP TABLE #TMP_PRE;
        IF OBJECT_ID(N'tempdb..#TMP_PACK', N'U') IS NOT NULL DROP TABLE #TMP_PACK;
        IF OBJECT_ID(N'tempdb..#TMP_GANTRY_CONV', N'U') IS NOT NULL DROP TABLE #TMP_GANTRY_CONV;

        CREATE TABLE #TMP_PACK
        (
            id bigint not null identity(1,1)
            ,RoadWay varchar(50) null
            ,Category varchar(512) null
            ,TripSecondaryCode_Joined varchar(512) null
            ,TotalCount bigint null
            ,TotalAmount float null
        );

        CREATE TABLE #TMP_GANTRY_CONV
        (
            id int not null identity(1,1)
            ,RoadWay varchar(50) not null
            ,OrgID varchar(50) not null
        );

        INSERT INTO #TMP_GANTRY_CONV (RoadWay, OrgID) VALUES
        ('SR112',50),('836-17',55),('836-57',60),('836-97',65),('SR874',80),('SR878',75),('SR924',70);

        SELECT
            cttu.[TransID]
            ,cttu.[OrgID]
            ,tgv.RoadWay
            ,[TollType]
            ,[TransDate]
            ,[LaneNumber]
            ,[Fare]
            ,[FullToll]
            ,[ChargeToll]
            ,[CollectedToll]
            ,[AVCClass]
            ,[AxlesExpected]
            ,[AxlesCounted]
            ,[TagStatusID]
            ,[AgencyVehicleClass]
            ,[RateTableID]
            ,[SendToICD]
            ,amd.AmendmentType
            ,amd.AmendmentTRIPMappingID
            ,atm.TRIPSource
            ,atm.TRIPSourceRule
            ,atm.TRIPPrimaryCode
            ,atm.TRIPSecondaryCode
            ,uai.UFMAmendmentTypeID
            ,uat.AmendmentType UAT_AmendmentType
        INTO #TMP_PRE
        FROM [ICD].[dbo].[tblCoopTagTollUsage] (nolock) cttu
        LEFT OUTER JOIN [ICD].[dbo].[tblAmendments] (nolock) amd
            ON cttu.TransID = amd.TransID
            and Source = 'TRIPAmendmentService'
        LEFT OUTER JOIN [ICD].[dbo].[tblAmendmentTRIPMapping] (nolock) atm
            ON amd.AmendmentTRIPMappingID = atm.ID
        LEFT OUTER JOIN
        (
            SELECT uai1.*
            FROM [ICD].[dbo].[tblUFMAmendmentInfo] (nolock) uai1
            INNER JOIN
            (
                SELECT UFMID, MAX(uai2.SettlementPostingTime) spt
                FROM [ICD].[dbo].[tblUFMAmendmentInfo] (nolock) uai2
                GROUP BY UFMID
            ) uai3
            ON uai1.UFMID = uai3.UFMID and uai1.SettlementPostingTime = uai3.spt
        ) uai
            ON cttu.TransID = uai.UFMID
        LEFT OUTER JOIN [ICD].[dbo].[tblUFMAmendmentType] (nolock) uat
            ON uai.AccountTypeID = uat.AmendmentTypeID
        LEFT OUTER JOIN #TMP_GANTRY_CONV tgv
            ON cttu.OrgID = tgv.OrgID
        WHERE TransDate >= @STDATE
          AND TransDate < @ENDDATE
          AND [TransType] = 'VIOL';

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, TRIPPrimaryCode, STRING_AGG(TRIPSecondaryCode,','), SUM(CountX) TotalCount, Sum(AmountX) TotalAmount
        FROM
        (
            SELECT RoadWay
                ,'CLOSED - EIP disposition code 6, reason code 0100' TRIPPrimaryCode
                ,TRIPSecondaryCode
                ,Count(TransID) CountX
                ,Sum(FullToll) AmountX
            FROM #TMP_PRE
            WHERE TRIPSource ='MIRREJ' AND
                  TRIPSourceRule = 'REVIEW_CODE_OFF' AND
                  TRIPPrimaryCode = 'Recurrent Plate-Visibility'
            GROUP BY RoadWay, TRIPSecondaryCode
        ) a
        GROUP By RoadWay, TRIPPrimaryCode;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, TRIPPrimaryCode, STRING_AGG(TRIPSecondaryCode,','), SUM(CountX) TotalCount, Sum(AmountX) TotalAmount
        FROM
        (
            SELECT RoadWay
                ,'CLOSED - EIP disposition code 6, reason code 0300' TRIPPrimaryCode
                ,TRIPSecondaryCode
                ,Count(TransID) CountX
                ,Sum(FullToll) AmountX
            FROM #TMP_PRE
            WHERE TRIPSource ='MIRREJ' AND
                  TRIPSourceRule = 'REVIEW_CODE_OFF' AND
                  TRIPPrimaryCode = 'Camera Imaging Problem'
            GROUP BY RoadWay, TRIPPrimaryCode, TRIPSecondaryCode
        ) b
        GROUP BY RoadWay, TRIPPrimaryCode;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, TRIPPrimaryCode, STRING_AGG(TRIPSecondaryCode,','), SUM(CountX) TotalCount, Sum(AmountX) TotalAmount
        FROM
        (
            SELECT RoadWay
                ,'WF - UFM Pending' TRIPPrimaryCode
                ,TRIPSecondaryCode
                ,Count(TransID) CountX
                ,Sum(FullToll) AmountX
            FROM #TMP_PRE
            WHERE (TRIPSource ='MIRIDENT' AND TRIPSourceRule = 'REVIEW_IMPROVED') OR
                  (TRIPSource ='AIPIDENT' AND TRIPSourceRule = 'REVIEW')
            GROUP BY RoadWay, TRIPPrimaryCode, TRIPSecondaryCode
        ) c
        GROUP BY RoadWay, TRIPPrimaryCode;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, 'WF - UH video transaction created', '', Count(TransID), Sum(FullToll) AmountX
        FROM #TMP_PRE
        WHERE IsNULL(TRIPSource,'-') ='-' AND IsNULL(TRIPSourceRule,'-') = '-'
        GROUP BY RoadWay;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, 'CLOSED - EIP disposition code 7, black list', '', Count(TransID), Sum(FullToll) AmountX
        FROM #TMP_PRE
        WHERE TRIPSource ='LPNLISTREJ' AND TRIPSourceRule ='LPNBLKLIST'
        GROUP BY RoadWay;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, 'CLOSED - EIP disposition code 6, reason code 0500', '', Count(TransID), Sum(FullToll) AmountX
        FROM #TMP_PRE
        WHERE TRIPSource ='MIRREJ' AND
              TRIPSourceRule ='REVIEW_CODE_OFF' AND
              TRIPPrimaryCode ='Other' AND
              TRIPSecondaryCode ='Emergency Vehicle'
        GROUP BY RoadWay;

        INSERT INTO #TMP_PACK (RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount)
        SELECT RoadWay, 'CLOSED - EIP disposition code 6, no answer', '', Count(TransID), Sum(FullToll) AmountX
        FROM #TMP_PRE
        WHERE TRIPSource ='MIRREJ' AND
              TRIPSourceRule ='REVIEW_CODE_OFF' AND
              TRIPPrimaryCode ='Other' AND
              TRIPSecondaryCode ='Other'
        GROUP BY RoadWay;

        SELECT RoadWay, Category, TripSecondaryCode_Joined, TotalCount, TotalAmount
        FROM #TMP_PACK
        ORDER BY RoadWay, Category, TripSecondaryCode_Joined;
        """;

    private static async Task<object> FetchViolationCategoryRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ViolationCategorySql;
        // The script scans the whole UFM amendment table and takes minutes even for one
        // day, so it gets a much longer timeout than the 10-minute default.
        command.CommandTimeout = Math.Max(settings.CommandTimeoutSeconds, 7200);

        command.Parameters.Add(new SqlParameter("@STDATE", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        // The script's end date is exclusive; the program's end date is inclusive.
        command.Parameters.Add(new SqlParameter("@ENDDATE", System.Data.SqlDbType.Date) { Value = chunkEnd.Date.AddDays(1) });

        var rows = new List<ViolationCategoryRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount != 5)
                continue;

            while (await reader.ReadAsync())
            {
                rows.Add(new ViolationCategoryRow(
                    RoadWay: reader.IsDBNull(0) ? null : reader.GetString(0),
                    Category: reader.IsDBNull(1) ? "" : reader.GetString(1),
                    SecondaryCodes: reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Count: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    Amount: reader.IsDBNull(4) ? 0d : reader.GetDouble(4)));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static long Write6192Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<ViolationCategoryRow>)rowsObj;
        foreach (var row in rows)
        {
            object?[] fields =
            [
                row.RoadWay,
                row.Category,
                row.SecondaryCodes,
                row.Count,
                FormatCurrency((decimal)Math.Round(row.Amount, 2))
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    // ---- 5625: [dbo].[uspr_csc_OTOFileDetails_sql_Sarad_Migration], one CSV line per transaction ----

    private static async Task<object> FetchOtoDetailRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@inStartDate", System.Data.SqlDbType.DateTime) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@inEndDate", System.Data.SqlDbType.DateTime) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inReportType", System.Data.SqlDbType.TinyInt) { Value = (byte)1 });
        command.Parameters.Add(new SqlParameter("@inOrgID", System.Data.SqlDbType.BigInt) { Value = 0L });
        command.Parameters.Add(new SqlParameter("@inGroupBy", System.Data.SqlDbType.TinyInt) { Value = (byte)1 });

        // Each row is formatted to its final CSV line while streaming, so only one string
        // per transaction is kept. The procedure already returns plaza/lane order.
        var lines = new List<string>();
        var line = new StringBuilder(160);

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var plaza = reader.GetOrdinal("PlazaCode");
            var lane = reader.GetOrdinal("LaneNumber");
            var collector = reader.GetOrdinal("TollCollector");
            var laneExit = reader.GetOrdinal("LaneExitDate");
            var transponder = reader.GetOrdinal("TransponderID");
            var agency = reader.GetOrdinal("Agency");
            var state = reader.GetOrdinal("State");
            var collected = reader.GetOrdinal("CollectedAmt");
            var charged = reader.GetOrdinal("ChargedAmt");
            var full = reader.GetOrdinal("FullAmt");
            var lowBattery = reader.GetOrdinal("LowBattery");
            var status = reader.GetOrdinal("TransponderStatus");
            var lowBalance = reader.GetOrdinal("LowBalance");
            var insufficient = reader.GetOrdinal("InsufficentFunds");
            var creditNum = reader.GetOrdinal("CreditListNum");
            var creditUpdated = reader.GetOrdinal("CreditListUpdated");
            var avc = reader.GetOrdinal("AVCClass");
            var transponderClass = reader.GetOrdinal("TransponderClass");
            var revenueType = reader.GetOrdinal("RevenueType");
            var violation = reader.GetOrdinal("ViolationCode");
            var readerConfirmation = reader.GetOrdinal("ReaderConfirmation");
            var amtech = reader.GetOrdinal("AmtechTransponderNumber");
            var speed = reader.GetOrdinal("VehicleSpeed");
            var reject = reader.GetOrdinal("RejectReasonCode");

            string Text(int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal).TrimEnd();

            while (await reader.ReadAsync())
            {
                line.Clear();
                // LaneType (S), SignedBalance (+00000) and AntiPassbackCode (02) are
                // constants in the legacy report; the procedure hardcodes 0 / 000000 / 00.
                AppendCsv(line, Text(plaza), first: true);
                AppendCsv(line, Text(lane));
                AppendCsv(line, "S");
                AppendCsv(line, Text(collector));
                AppendCsv(line, Text(laneExit));
                AppendCsv(line, Text(transponder));
                AppendCsv(line, Text(agency));
                AppendCsv(line, Text(state));
                AppendCsv(line, "+00000");
                AppendCsv(line, FormatDollars(Text(collected)));
                AppendCsv(line, FormatDollars(Text(charged)));
                AppendCsv(line, FormatDollars(Text(full)));
                AppendCsv(line, Text(lowBattery));
                AppendCsv(line, Text(status));
                AppendCsv(line, Text(lowBalance));
                AppendCsv(line, Text(insufficient));
                AppendCsv(line, Text(creditNum));
                AppendCsv(line, Text(creditUpdated));
                AppendCsv(line, "02");
                AppendCsv(line, Text(avc));
                AppendCsv(line, Text(transponderClass));
                AppendCsv(line, Text(revenueType));
                AppendCsv(line, Text(violation));
                AppendCsv(line, Text(readerConfirmation));
                AppendCsv(line, Text(amtech));
                AppendCsv(line, Text(speed));
                AppendCsv(line, Text(reject));
                lines.Add(line.ToString());
            }
        }
        while (await reader.NextResultAsync());

        return lines;
    }

    // "0033" (cents, zero padded) -> "$0.33"
    private static string FormatDollars(string cents) =>
        "$" + (long.Parse(cents, CultureInfo.InvariantCulture) / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static void AppendCsv(StringBuilder sb, string text, bool first = false)
    {
        if (!first)
            sb.Append(',');

        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            sb.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
        else
            sb.Append(text);
    }

    private static long Write5625Rows(object rowsObj, StreamWriter writer)
    {
        var lines = (IReadOnlyList<string>)rowsObj;
        foreach (var line in lines)
        {
            writer.Write(line);
            writer.Write("\r\n");
        }

        return lines.Count;
    }

    // ---- 1220: [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum], straight passthrough ----

    private static async Task<object> FetchTransactionRowsAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        DateTime chunkStart,
        DateTime chunkEnd)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@inOrgID", System.Data.SqlDbType.Int) { Value = 0 });
        command.Parameters.Add(new SqlParameter("@ReportType", System.Data.SqlDbType.Int) { Value = 1 });
        command.Parameters.Add(new SqlParameter("@GroupBy", System.Data.SqlDbType.Int) { Value = 1 });
        command.Parameters.Add(new SqlParameter("@selectVal", System.Data.SqlDbType.NVarChar, -1) { Value = DBNull.Value });
        command.Parameters.Add(new SqlParameter("@inStartDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@inEndDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inDayType", System.Data.SqlDbType.Int) { Value = 2 });
        command.Parameters.Add(new SqlParameter("@inLaneGroupID", System.Data.SqlDbType.VarChar, 10) { Value = "0" });

        var rows = new List<TransactionRow>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var transDateOrdinal = reader.GetOrdinal("TransDate");
            var laneOrdinal = reader.GetOrdinal("Lane");
            var vehClassOrdinal = reader.GetOrdinal("VehClass");
            var tollFullOrdinal = reader.GetOrdinal("TollFull");
            var tollChargedOrdinal = reader.GetOrdinal("TollCharged");
            var tollCollectedOrdinal = reader.GetOrdinal("TollCollected");
            var ivisAxlesOrdinal = reader.GetOrdinal("IVISAxles");
            var collectorAxlesOrdinal = reader.GetOrdinal("CollectorAxles");
            var transTypeOrdinal = reader.GetOrdinal("TransType");
            var orgNameOrdinal = reader.GetOrdinal("OrgName");
            var groupDataOrdinal = reader.GetOrdinal("GroupData");
            var laneGroupIdOrdinal = reader.GetOrdinal("LaneGroupID");
            var laneGroupNameOrdinal = reader.GetOrdinal("LaneGroupName");

            while (await reader.ReadAsync())
            {
                rows.Add(new TransactionRow(
                    TransDate: reader.GetDateTime(transDateOrdinal),
                    Lane: Convert.ToInt32(reader.GetValue(laneOrdinal), CultureInfo.InvariantCulture),
                    VehClass: Convert.ToInt32(reader.GetValue(vehClassOrdinal), CultureInfo.InvariantCulture),
                    TollFull: Convert.ToDecimal(reader.GetValue(tollFullOrdinal), CultureInfo.InvariantCulture),
                    TollCharged: Convert.ToDecimal(reader.GetValue(tollChargedOrdinal), CultureInfo.InvariantCulture),
                    TollCollected: Convert.ToDecimal(reader.GetValue(tollCollectedOrdinal), CultureInfo.InvariantCulture),
                    IvisAxles: Convert.ToInt32(reader.GetValue(ivisAxlesOrdinal), CultureInfo.InvariantCulture),
                    CollectorAxles: Convert.ToInt32(reader.GetValue(collectorAxlesOrdinal), CultureInfo.InvariantCulture),
                    TransType: reader.GetValue(transTypeOrdinal).ToString() ?? "",
                    OrgName: reader.GetValue(orgNameOrdinal).ToString() ?? "",
                    GroupData: reader.IsDBNull(groupDataOrdinal) ? "" : reader.GetValue(groupDataOrdinal).ToString() ?? "",
                    LaneGroupId: Convert.ToInt32(reader.GetValue(laneGroupIdOrdinal), CultureInfo.InvariantCulture),
                    LaneGroupName: reader.GetValue(laneGroupNameOrdinal).ToString() ?? ""));
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    // Mirrors #TMP_GANTRY_CONV in 1220_test.sql. GantryConv lives in TCore_Import
    // on a different server with no linked server, so the RoadWay/GantryNum ->
    // PlazaID lookup is embedded here instead of joined via SQL.
    private static readonly (string RoadWay, string GantryNum, int PlazaId)[] GantryConvTable =
    [
        ("SR112", "20", 107120), ("SR112", "40", 107140), ("SR112", "60", 107160), ("SR112", "80", 107180),
        ("836-17", "36", 107236), ("836-17", "39", 107239), ("836-17", "40", 107240), ("836-17", "41", 107241),
        ("836-17", "42", 107242), ("836-17", "55", 107255), ("836-17", "58", 107258), ("836-17", "60", 107260),
        ("836-57", "27", 107227), ("836-57", "30", 107230), ("836-57", "32", 107232), ("836-57", "63", 107263),
        ("836-57", "64", 107264), ("836-57", "68", 107268), ("836-57", "70", 107270),
        ("836-97", "21", 107221), ("836-97", "23", 107223), ("836-97", "26", 107226), ("836-97", "75", 107275),
        ("836-97", "76", 107276), ("836-97", "79", 107279),
        ("SR874", "20", 107320), ("SR874", "30", 107330), ("SR874", "40", 107340), ("SR874", "60", 107360),
        ("SR874", "70", 107370), ("SR874", "80", 107380),
        ("SR878", "20", 107620), ("SR878", "40", 107640), ("SR878", "60", 107660), ("SR878", "80", 107680),
        ("SR924", "20", 107420), ("SR924", "40", 107440), ("SR924", "60", 107460), ("SR924", "80", 107480)
    ];

    private static readonly Dictionary<(string RoadWay, string GantryNum), int> GantryConvLookup =
        GantryConvTable.ToDictionary(g => (g.RoadWay, g.GantryNum), g => g.PlazaId);

    // Mirrors the SQL join condition in 1220_test.sql:
    // RoadWay = TRIM(SUBSTRING(LaneGroupName,1,CHARINDEX(' Gantry',LaneGroupName))) AND
    // GantryNum = TRIM(SUBSTRING(LaneGroupName,CHARINDEX(' Gantry',LaneGroupName)+8,...))
    private static int? LookupPlazaId(string laneGroupName)
    {
        int spaceIdx = laneGroupName.IndexOf(" Gantry", StringComparison.Ordinal);
        if (spaceIdx < 0)
            return null;

        string roadWay = laneGroupName.Substring(0, spaceIdx).Trim();
        string gantryNum = laneGroupName.Substring(spaceIdx + 8).Trim();

        return GantryConvLookup.TryGetValue((roadWay, gantryNum), out var plazaId) ? plazaId : (int?)null;
    }

    private static long Write1220Rows(object rowsObj, StreamWriter writer)
    {
        var rows = (IReadOnlyList<TransactionRow>)rowsObj;
        foreach (var row in rows)
        {
            int? plazaId = LookupPlazaId(row.LaneGroupName);

            object?[] fields =
            [
                plazaId?.ToString(CultureInfo.InvariantCulture),
                $"{row.TransDate:MM/dd/yyyy}",
                $"{row.TransDate:HH:mm:ss}",
                row.Lane,
                row.VehClass,
                row.TollFull,
                row.TollCharged,
                row.TollCollected,
                row.IvisAxles,
                row.CollectorAxles,
                row.TransType,
                row.OrgName,
                row.GroupData,
                row.LaneGroupId,
                row.LaneGroupName
            ];

            WriteCsvLine(writer, fields);
        }

        return rows.Count;
    }

    private static void WriteCsvLine(StreamWriter writer, object?[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                writer.Write(',');

            writer.Write(CsvField(fields[i]));
        }
        writer.Write("\r\n");
    }

    private static string CsvField(object? value)
    {
        if (value is null)
            return "";

        var text = value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            long l => FormatNumber(l),
            int iVal => FormatNumber(iVal),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + text.Replace("\"", "\"\"") + "\"";

        return text;
    }

    private static string FormatNumber(long value) =>
        Math.Abs(value) > 999
            ? value.ToString("#,##0", CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<(DateTime Start, DateTime End)> SplitIntoWeeklyChunks(
        DateTime startDate, DateTime endDate, int chunkDays = MaxDaysPerCall)
    {
        var chunkStart = startDate.Date;
        var end = endDate.Date;
        while (chunkStart <= end)
        {
            var chunkEnd = chunkStart.AddDays(chunkDays - 1);
            if (chunkEnd > end)
                chunkEnd = end;

            yield return (chunkStart, chunkEnd);

            chunkStart = chunkEnd.AddDays(1);
        }
    }

    private static (DateTime StartDate, DateTime EndDate, List<string> ImportTypes, string OutputFolder) ParseArguments(string[] args)
    {
        string? startArg = null;
        string? endArg = null;
        string? importTypeArg = null;
        string? outputFolder = null;
        var relativeRange = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-sd":
                    startArg = RequireValue(args, ref i, "-sd");
                    break;
                case "-ed":
                    endArg = RequireValue(args, ref i, "-ed");
                    break;
                case "-ra":
                    relativeRange = true;
                    break;
                case "-it":
                    importTypeArg = RequireValue(args, ref i, "-it");
                    break;
                case "-o":
                    outputFolder = RequireValue(args, ref i, "-o");
                    break;
                default:
                    throw new ArgumentException($"Unrecognized argument '{args[i]}'. Usage: -sd <date> -ed <date> | -ra, -it <importType>[,<importType>...] -o <folder>");
            }
        }

        DateTime startDate, endDate;
        if (relativeRange)
        {
            if (startArg is not null || endArg is not null)
                throw new ArgumentException("-ra cannot be combined with -sd/-ed.");

            var today = DateTime.Today;
            startDate = new DateTime(today.Year, today.Month, 1);
            endDate = today.AddDays(-1);
        }
        else
        {
            startDate = ParseOrPromptDate(startArg, "Start date (yyyy-MM-dd)");
            endDate = ParseOrPromptDate(endArg, "End date (yyyy-MM-dd)");
        }

        if (endDate < startDate)
            throw new ArgumentException("End date cannot be before start date.");

        var importTypes = ParseOrPromptImportTypes(importTypeArg);
        var resolvedOutputFolder = ParseOrPromptOutputFolder(outputFolder);

        return (startDate, endDate, importTypes, resolvedOutputFolder);
    }

    private static string ParseOrPromptOutputFolder(string? candidate)
    {
        while (true)
        {
            var input = (candidate ?? PromptFor("Output folder")).Trim();
            candidate = null; // only reuse the command-line value once

            if (!string.IsNullOrWhiteSpace(input))
                return input;

            Console.WriteLine("Output folder is required.");
        }
    }

    // Accepts one or more import types separated by commas, e.g. "-it 1310,1270",
    // so types that share a stored procedure only trigger it once per call.
    private static List<string> ParseOrPromptImportTypes(string? candidate)
    {
        var supported = string.Join(", ", ImportTypes.Keys);
        while (true)
        {
            var input = candidate ?? PromptFor($"Import type(s), comma-separated ({supported})");
            candidate = null; // only reuse the command-line value once

            var requested = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.Length == 0)
            {
                Console.WriteLine("At least one import type is required.");
                continue;
            }

            var unknown = requested.Where(t => !ImportTypes.ContainsKey(t)).ToList();
            if (unknown.Count > 0)
            {
                Console.WriteLine($"Unknown import type(s): {string.Join(", ", unknown)}. Supported types: {supported}");
                continue;
            }

            return requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for '{flag}'.");

        return args[++i];
    }

    private static DateTime ParseOrPromptDate(string? candidate, string prompt)
    {
        while (true)
        {
            var input = candidate ?? PromptFor(prompt);
            candidate = null; // only reuse the command-line value once

            if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                return result.Date;

            Console.WriteLine($"Could not parse '{input}' as a date. Use yyyy-MM-dd.");
        }
    }

    private static string PromptFor(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine() ?? "";
    }

    // <baseFolder>\<importType>\<year>\<MonthName>\Result_<importType>_<start>_<end>_<runDate>_<runTime>.csv
    // Year/month reflect the start of the reporting interval. `now` is captured once
    // per run so every import type's file gets the same run timestamp.
    private static string BuildOutputPath(string baseFolder, string importType, DateTime startDate, DateTime endDate, DateTime now)
    {
        var folder = Path.Combine(
            baseFolder,
            importType,
            startDate.ToString("yyyy", CultureInfo.InvariantCulture),
            startDate.ToString("MMMM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        var fileName = $"Result_{importType}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}_{now:yyyyMMdd}_{now:HHmmss}.csv";
        return Path.Combine(folder, fileName);
    }

    private static AppSettings LoadOrPromptSqlSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        var settings = ReadSettingsFile(settingsPath) ?? new AppSettings();

        // appsettings.local.json is git-ignored and holds real credentials;
        // appsettings.json stays as the blank, checked-in template.
        var localSettings = ReadSettingsFile(localSettingsPath);
        if (localSettings is not null && !string.IsNullOrWhiteSpace(localSettings.Sql.Server))
            settings = localSettings;

        if (string.IsNullOrWhiteSpace(settings.Sql.Server))
        {
            Console.WriteLine("SQL Server connection is not configured.");
            settings.Sql.Server = PromptFor("SQL Server (host\\instance or host,port)");
            settings.Sql.Database = PromptFor("Database name");

            Console.Write("Use Windows integrated security? (Y/n): ");
            var useIntegrated = (Console.ReadLine() ?? "Y").Trim();
            settings.Sql.IntegratedSecurity = useIntegrated.Length == 0 || useIntegrated.StartsWith("y", StringComparison.OrdinalIgnoreCase);

            if (!settings.Sql.IntegratedSecurity)
            {
                settings.Sql.UserId = PromptFor("SQL login user id");
                settings.Sql.Password = PromptForPassword("SQL login password");
            }

            Console.Write("Save these settings to appsettings.local.json for next time? (y/N): ");
            var save = (Console.ReadLine() ?? "N").Trim();
            if (save.StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                WriteSettingsFile(localSettingsPath, settings);
                Console.WriteLine($"Saved to {localSettingsPath} (git-ignored, not checked in).");
            }
        }

        return settings;
    }

    private static AppSettings? ReadSettingsFile(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private static void WriteSettingsFile(string path, AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string PromptForPassword(string label)
    {
        Console.Write($"{label}: ");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        return password.ToString();
    }

    // Logs the run to TCore_Import.dbo.Session. SessionID is generated by the table's
    // own sequence default; we just read it back via OUTPUT.
    private static async Task<long> InsertSessionStartAsync(SqlSettings sessionSql)
    {
        await using var connection = new SqlConnection(BuildConnectionString(sessionSql));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.[Session] (SessionStart, SessionHost, SessionUser) " +
            "OUTPUT INSERTED.SessionID " +
            "VALUES (@SessionStart, @SessionHost, @SessionUser);";
        command.Parameters.Add(new SqlParameter("@SessionStart", System.Data.SqlDbType.DateTime) { Value = DateTime.Now });
        command.Parameters.Add(new SqlParameter("@SessionHost", System.Data.SqlDbType.NVarChar, 256) { Value = Environment.MachineName });
        command.Parameters.Add(new SqlParameter("@SessionUser", System.Data.SqlDbType.NVarChar, 256) { Value = Environment.UserName });

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UpdateSessionEndAsync(SqlSettings sessionSql, long sessionId, int filesProcessed)
    {
        await using var connection = new SqlConnection(BuildConnectionString(sessionSql));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.[Session] SET SessionEnd = @SessionEnd, FilesProcessed = @FilesProcessed " +
            "WHERE SessionID = @SessionID;";
        command.Parameters.Add(new SqlParameter("@SessionEnd", System.Data.SqlDbType.DateTime) { Value = DateTime.Now });
        command.Parameters.Add(new SqlParameter("@FilesProcessed", System.Data.SqlDbType.Int) { Value = filesProcessed });
        command.Parameters.Add(new SqlParameter("@SessionID", System.Data.SqlDbType.BigInt) { Value = sessionId });

        await command.ExecuteNonQueryAsync();
    }

    // Logs one output file to TCore_Import.dbo.OutFiles, keyed by (SessionID, OutputFileName).
    // id is an identity column the app never sets.
    private static async Task InsertOutFileStartAsync(
        SqlSettings sessionSql, long sessionId, string fileType, string outputFileName, DateTime beginDate, DateTime endDate)
    {
        await using var connection = new SqlConnection(BuildConnectionString(sessionSql));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.[OutFiles] (SessionID, FileType, OutputFileName, ProcessStart, BeginDate, EndDate) " +
            "VALUES (@SessionID, @FileType, @OutputFileName, @ProcessStart, @BeginDate, @EndDate);";
        command.Parameters.Add(new SqlParameter("@SessionID", System.Data.SqlDbType.BigInt) { Value = sessionId });
        command.Parameters.Add(new SqlParameter("@FileType", System.Data.SqlDbType.NVarChar, 64) { Value = fileType });
        command.Parameters.Add(new SqlParameter("@OutputFileName", System.Data.SqlDbType.NVarChar, 256) { Value = outputFileName });
        command.Parameters.Add(new SqlParameter("@ProcessStart", System.Data.SqlDbType.DateTime2) { Value = DateTime.Now });
        command.Parameters.Add(new SqlParameter("@BeginDate", System.Data.SqlDbType.Date) { Value = beginDate.Date });
        command.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = endDate.Date });

        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateOutFileEndAsync(
        SqlSettings sessionSql, long sessionId, string outputFileName, long totalLines, long? fileSize)
    {
        await using var connection = new SqlConnection(BuildConnectionString(sessionSql));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dbo.[OutFiles] SET ProcessEnd = @ProcessEnd, FileSize = @FileSize, TotalLines = @TotalLines " +
            "WHERE SessionID = @SessionID AND OutputFileName = @OutputFileName;";
        command.Parameters.Add(new SqlParameter("@ProcessEnd", System.Data.SqlDbType.DateTime2) { Value = DateTime.Now });
        command.Parameters.Add(new SqlParameter("@FileSize", System.Data.SqlDbType.BigInt) { Value = (object?)fileSize ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@TotalLines", System.Data.SqlDbType.BigInt) { Value = totalLines });
        command.Parameters.Add(new SqlParameter("@SessionID", System.Data.SqlDbType.BigInt) { Value = sessionId });
        command.Parameters.Add(new SqlParameter("@OutputFileName", System.Data.SqlDbType.NVarChar, 256) { Value = outputFileName });

        await command.ExecuteNonQueryAsync();
    }

    private static string BuildConnectionString(SqlSettings sql)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = sql.Server,
            InitialCatalog = sql.Database,
            TrustServerCertificate = sql.TrustServerCertificate,
            ConnectTimeout = 30
        };

        if (sql.IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = sql.UserId;
            builder.Password = sql.Password;
        }

        return builder.ConnectionString;
    }
}
