using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TransCoreImports;

internal static class Program
{
    // Distinct OrgIDs pulled from #TMP_GANTRY_CONV in 1310_Proc_Result.sql.
    // The production script loops over these with a T-SQL cursor; we do the
    // same loop here in C# instead.
    private static readonly int[] OrgIds = { 50, 55, 60, 65, 70, 75, 80 };

    private const int MaxDaysPerCall = 7;

    // Each import type has its own stored procedure and CSV column structure. Only 1310
    // is implemented today; add new entries here as other import types are defined.
    // Output path for every type is <-o folder>\<importType>\<year>\<MonthName>\...
    private sealed record ImportTypeConfig(string ProcedureName, string HeaderRow);

    private static readonly Dictionary<string, ImportTypeConfig> ImportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1310"] = new ImportTypeConfig(
            ProcedureName: "[RPrl].[uspr_trn_HourlyTraffic_1315b_1320b]",
            HeaderRow: "Facility,LaneGroup,Date,Hour,VIOL2,VIOL3,VIOL4,VIOL5,VIOL6_9,VIOL," +
                       "ETC2,ETC3,ETC4,ETC5,ETC6_9,ETC_Sum,AR,CardNonRev,PassNR,NRETC,NR_TOTAL,GrandTotal")
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var (startDate, endDate, importType, outputFolder) = ParseArguments(args);
            var importConfig = ImportTypes[importType];

            var settings = LoadOrPromptSqlSettings();
            var connectionString = BuildConnectionString(settings.Sql);

            var outputPath = BuildOutputPath(outputFolder, importType, startDate, endDate);

            var sessionId = await InsertSessionStartAsync(settings.SessionSql);

            Console.WriteLine($"Session ID : {sessionId}");
            Console.WriteLine($"Import type: {importType}");
            Console.WriteLine($"Date range : {startDate:yyyy-MM-dd} .. {endDate:yyyy-MM-dd}");
            Console.WriteLine($"Output file: {Path.GetFullPath(outputPath)}");
            Console.WriteLine();

            var outputFileName = Path.GetFileName(outputPath);
            var filesProcessed = 0;
            long totalRows = 0;
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var failures = new List<string>();

                await InsertOutFileStartAsync(settings.SessionSql, sessionId, importType, outputFileName, startDate, endDate);

                await using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
                    writer.Write(importConfig.HeaderRow);
                    writer.Write("\r\n");

                    foreach (var (chunkStart, chunkEnd) in SplitIntoWeeklyChunks(startDate, endDate))
                    {
                        foreach (var orgId in OrgIds)
                        {
                            Console.WriteLine($"OrgID {orgId,3}  {chunkStart:yyyy-MM-dd} .. {chunkEnd:yyyy-MM-dd} ...");
                            try
                            {
                                var rows = await RunOneCallAsync(connection, settings.Sql, importConfig.ProcedureName, orgId, chunkStart, chunkEnd, writer);
                                totalRows += rows;
                                Console.WriteLine($"    {rows} row(s)");
                            }
                            catch (Exception ex)
                            {
                                var message = $"OrgID {orgId} {chunkStart:yyyy-MM-dd}..{chunkEnd:yyyy-MM-dd}: {ex.Message}";
                                failures.Add(message);
                                Console.Error.WriteLine($"    FAILED: {ex.Message}");
                            }
                        }
                    }
                }

                filesProcessed = 1;
                stopwatch.Stop();

                Console.WriteLine();
                Console.WriteLine($"Done. {totalRows} row(s) written in {stopwatch.Elapsed.TotalSeconds:F1} sec.");
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
                var fileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : (long?)null;
                await UpdateOutFileEndAsync(settings.SessionSql, sessionId, outputFileName, totalRows, fileSize);
                await UpdateSessionEndAsync(settings.SessionSql, sessionId, filesProcessed);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<long> RunOneCallAsync(
        SqlConnection connection,
        SqlSettings settings,
        string procedureName,
        int orgId,
        DateTime chunkStart,
        DateTime chunkEnd,
        StreamWriter writer)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedureName;
        command.CommandTimeout = settings.CommandTimeoutSeconds;

        command.Parameters.Add(new SqlParameter("@OrgID", System.Data.SqlDbType.VarChar, 50) { Value = orgId.ToString(CultureInfo.InvariantCulture) });
        command.Parameters.Add(new SqlParameter("@StartDate", System.Data.SqlDbType.Date) { Value = chunkStart.Date });
        command.Parameters.Add(new SqlParameter("@EndDate", System.Data.SqlDbType.Date) { Value = chunkEnd.Date });
        command.Parameters.Add(new SqlParameter("@inDayType", System.Data.SqlDbType.VarChar, 10) { Value = "0" });
        command.Parameters.Add(new SqlParameter("@inLaneGroupID", System.Data.SqlDbType.VarChar, 10) { Value = "0" });

        // Source column layout matches #TMP_EXECRESULT in 1310_Proc_Result.sql:
        // 0 OrgName, 1 LaneNumber, 2 LaneGroupName, 3 TransDate, 4 TransHour,
        // 5..23 the 19 numeric measures, 24 TollDay, 25 LaneGroupID.
        var groups = new Dictionary<GantryHourKey, long[]>();

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                var orgName = reader.GetString(0);
                var laneGroupName = reader.GetString(2);
                var transDate = reader.GetDateTime(3).Date;
                var transHour = reader.GetInt32(4);
                var tollDay = reader.GetDateTime(24);
                var laneGroupId = reader.GetInt32(25);

                var key = new GantryHourKey(orgName, laneGroupName, transDate, transHour, tollDay, laneGroupId);
                if (!groups.TryGetValue(key, out var sums))
                {
                    sums = new long[MeasureColumnCount];
                    groups[key] = sums;
                }

                for (var m = 0; m < MeasureColumnCount; m++)
                    sums[m] += reader.IsDBNull(SourceFirstMeasureColumn + m) ? 0 : Convert.ToInt64(reader.GetValue(SourceFirstMeasureColumn + m), CultureInfo.InvariantCulture);
            }
        }
        while (await reader.NextResultAsync());

        // TransHour is no longer printed, so write rows out in a stable,
        // deterministic order (Dictionary enumeration order is not guaranteed).
        foreach (var pair in groups
            .OrderBy(p => p.Key.OrgName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.LaneGroupName, StringComparer.Ordinal)
            .ThenBy(p => p.Key.TransDate)
            .ThenBy(p => p.Key.TransHour))
        {
            WriteCsvRow(writer, pair.Key, pair.Value);
        }

        return groups.Count;
    }

    private const int SourceFirstMeasureColumn = 5; // index into the SP's result set (still has LaneNumber at 1)
    private const int MeasureColumnCount = 19; // VIOL2 .. ETCIVISAxles (source order)

    // Measures, in source order: 0 VIOL2, 1 VIOL3, 2 VIOL4, 3 VIOL5, 4 VIOL6_9, 5 VIOL,
    // 6 VIOLIndAxles, 7 VIOLIVISAxles, 8 AR, 9 CardNonRev, 10 PassNR, 11 ETC2, 12 ETC3,
    // 13 ETC4, 14 ETC5, 15 ETC6_9, 16 NRETC, 17 ETCIndAxles, 18 ETCIVISAxles.
    private const int SrcAR = 8, SrcCardNonRev = 9, SrcPassNR = 10;
    private const int SrcETC2 = 11, SrcETC3 = 12, SrcETC4 = 13, SrcETC5 = 14, SrcETC6_9 = 15;
    private const int SrcNRETC = 16;

    private sealed record GantryHourKey(string OrgName, string LaneGroupName, DateTime TransDate, int TransHour, DateTime TollDay, int LaneGroupId);

    private static void WriteCsvRow(StreamWriter writer, GantryHourKey key, long[] sums)
    {
        var etcSum = sums[SrcETC2] + sums[SrcETC3] + sums[SrcETC4] + sums[SrcETC5] + sums[SrcETC6_9]; // P
        var nrTotal = sums[SrcAR] + sums[SrcCardNonRev] + sums[SrcPassNR] + sums[SrcNRETC]; // U: NR_TOTAL = Q+R+S+T
        var grandTotal = sums[5] + etcSum + nrTotal; // V: J (VIOL) + P + U

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
            sums[0], sums[1], sums[2], sums[3], sums[4], sums[5], // VIOL2, VIOL3, VIOL4, VIOL5, VIOL6_9, VIOL
            sums[SrcETC2], sums[SrcETC3], sums[SrcETC4], sums[SrcETC5], sums[SrcETC6_9],
            etcSum,
            sums[SrcAR], sums[SrcCardNonRev], sums[SrcPassNR],
            sums[SrcNRETC],
            nrTotal,
            grandTotal
        ];

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

    private static (DateTime StartDate, DateTime EndDate, string ImportType, string OutputFolder) ParseArguments(string[] args)
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
                    throw new ArgumentException($"Unrecognized argument '{args[i]}'. Usage: -sd <date> -ed <date> | -ra, -it <importType> -o <folder>");
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

        var importType = ParseOrPromptImportType(importTypeArg);
        var resolvedOutputFolder = ParseOrPromptOutputFolder(outputFolder);

        return (startDate, endDate, importType, resolvedOutputFolder);
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

    private static string ParseOrPromptImportType(string? candidate)
    {
        var supported = string.Join(", ", ImportTypes.Keys);
        while (true)
        {
            var input = (candidate ?? PromptFor($"Import type ({supported})")).Trim();
            candidate = null; // only reuse the command-line value once

            if (ImportTypes.ContainsKey(input))
                return input;

            Console.WriteLine($"Unknown import type '{input}'. Supported types: {supported}");
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
    // Year/month reflect the start of the reporting interval.
    private static string BuildOutputPath(string baseFolder, string importType, DateTime startDate, DateTime endDate)
    {
        var folder = Path.Combine(
            baseFolder,
            importType,
            startDate.ToString("yyyy", CultureInfo.InvariantCulture),
            startDate.ToString("MMMM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        var now = DateTime.Now;
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
