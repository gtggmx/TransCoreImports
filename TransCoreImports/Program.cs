using System.Globalization;
using System.Text;
using System.Text.Json;
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

    // Each import type has its own CSV column structure and aggregation, but types
    // that share a ProcedureName reuse one fetch of the raw rows. WriteRows groups
    // the raw rows its own way, writes them to the file, and returns the row count.
    private sealed record ImportTypeConfig(
        string ProcedureName,
        string HeaderRow,
        Func<IReadOnlyList<RawRow>, StreamWriter, long> WriteRows);

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
            WriteRows: Write1310Rows),

        // Same source rows as 1310, aggregated per lane per day instead of per gantry
        // per hour (lanes are NOT summed together here).
        ["1270"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_HourlyTraffic_1315b_1320b]",
            HeaderRow: "OrgName,Date,Location,Lane,SunPass,Spec Evnt,NR,Viol",
            WriteRows: Write1270Rows)
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

                    foreach (var (chunkStart, chunkEnd) in SplitIntoWeeklyChunks(startDate, endDate))
                    {
                        foreach (var procGroup in byProcedure)
                        {
                            var typeNames = string.Join("+", procGroup.Select(t => t.Name));
                            Console.WriteLine($"[{typeNames}]  {chunkStart:yyyy-MM-dd} .. {chunkEnd:yyyy-MM-dd} ...");
                            try
                            {
                                var rawRows = await FetchRawRowsAsync(connection, settings.Sql, procGroup.Key, chunkStart, chunkEnd);
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

    private static async Task<List<RawRow>> FetchRawRowsAsync(
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

    private static long Write1310Rows(IReadOnlyList<RawRow> rows, StreamWriter writer)
    {
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

    private static long Write1270Rows(IReadOnlyList<RawRow> rows, StreamWriter writer)
    {
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

    private static IEnumerable<(DateTime Start, DateTime End)> SplitIntoWeeklyChunks(DateTime startDate, DateTime endDate)
    {
        var chunkStart = startDate.Date;
        var end = endDate.Date;
        while (chunkStart <= end)
        {
            var chunkEnd = chunkStart.AddDays(MaxDaysPerCall - 1);
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
