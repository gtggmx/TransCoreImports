using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace TransCoreLoader;

// Morning loader: copies one day of [RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]
// from the CPC server into GmxRaw.dbo.Data_1220, the same table the legacy CSV
// importer (TestCsv / SP_DATA_1220_INSERT) fills. The row transformation mirrors that
// stored procedure exactly (PlazaID from GmxParameters.GantryLaneConversionTable,
// UFM_CALC from PlazaDefinitions.ORG_ID + the UTC->Eastern timestamp, rows with no
// gantry match are dropped), but is done set-wise in the app + SqlBulkCopy because the
// procedure is one round trip per row and a day is ~1.7M rows.
internal static class Program
{
    private const string FileType = "1220";
    private const string LoaderUser = "TransCoreLoader";   // ProcUser marker for rows this program loaded
    private const int StatusLoading = 1;
    private const int StatusDone = 2;
    private const int StatusFailed = 98;
    private const int StatusSuperseded = 99;
    private const int BatchRows = 100_000;

    private sealed record Options(
        DateTime StartDate, DateTime EndDate, bool DryRun, bool ForceReplaceLegacy, double MaxUnmatchedPercent);

    private sealed record GantryConvRow(string Plaza, string LaneGroup, int Gantry, int PlazaId, string? CoreRoad);

    private static StreamWriter? _log;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            OpenLog();
            var settings = LoadSettings();

            var failed = 0;
            for (var day = options.StartDate; day <= options.EndDate; day = day.AddDays(1))
            {
                try
                {
                    await LoadDayAsync(settings, options, day);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"FAILED {day:yyyy-MM-dd}: {ex.Message}");
                }
            }

            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            return 1;
        }
        finally
        {
            _log?.Dispose();
        }
    }

    // ---------------------------------------------------------------- one day

    private static async Task LoadDayAsync(LoaderSettings settings, Options options, DateTime day)
    {
        var fileName = $"1220 Transaction Detail Prl_{day:yyyy}_{day:MMdd}.csv";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Log($"=== {day:yyyy-MM-dd}  ({fileName}){(options.DryRun ? "  DRY RUN" : "")}");

        await using var target = new SqlConnection(BuildConnectionString(settings.Target));
        await target.OpenAsync();

        if (!options.DryRun)
            await EnsureNewColumnsExistAsync(target);

        var conv = await ReadGantryConvAsync(target);
        var orgByPlaza = await ReadPlazaOrgsAsync(target);
        Log($"Lookups: {conv.Count} gantry/lane-group rows, {orgByPlaza.Count} plazas");
        var matcher = new PlazaMatcher(conv);

        // Existing, non-failed revisions of this day's file.
        var previous = await ReadRevisionsAsync(target, fileName);
        var live = previous.Where(p => p.Status is StatusDone or StatusLoading).ToList();
        var foreign = live.Where(p => !string.Equals(p.ProcUser, LoaderUser, StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0 && !options.ForceReplaceLegacy)
        {
            var f = foreign[0];
            var message =
                $"'{fileName}' was already loaded by '{f.ProcUser}' on '{f.ProcHost}' (FileID {f.FileId}, rev {f.Rev}). " +
                "Not replacing another importer's data; pass --force-replace-legacy to do so.";
            if (!options.DryRun)
                throw new InvalidOperationException(message);
            Log("NOTE (a real run would stop here): " + message);
        }

        if (options.DryRun)
        {
            var stats = new Stats();
            await StreamSourceAsync(settings, day, matcher, orgByPlaza, 0, stats, sink: null);
            stats.Print(Log);
            return;
        }

        var host = Environment.MachineName;
        var sessionId = await CreateSessionAsync(target, host, LoaderUser);
        Log($"Session ID {sessionId}");

        long newFileId = 0;
        var inserted = 0L;
        var unmatched = 0L;
        var committed = false;
        try
        {
            await InsertFileRowAsync(target, sessionId, fileName, day, host, StatusLoading);
            newFileId = await ReadLatestFileIdAsync(target, fileName);
            Log($"ImportFileID {newFileId}");

            await using (var tx = (SqlTransaction)await target.BeginTransactionAsync())
            {
                try
                {
                    var stats = new Stats();
                    using var bulk = new SqlBulkCopy(target, SqlBulkCopyOptions.TableLock, tx)
                    {
                        DestinationTableName = "dbo.Data_1220",
                        BatchSize = BatchRows,
                        BulkCopyTimeout = settings.Target.CommandTimeoutSeconds
                    };
                    foreach (DataColumn c in BuildTable().Columns)
                        bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);

                    await StreamSourceAsync(settings, day, matcher, orgByPlaza, newFileId, stats,
                        sink: async table => await bulk.WriteToServerAsync(table));

                    inserted = stats.Inserted;
                    unmatched = stats.Unmatched;
                    stats.Print(Log);

                    var allowed = Math.Max(0, (long)Math.Ceiling((inserted + unmatched) * options.MaxUnmatchedPercent / 100.0));
                    if (unmatched > allowed)
                        throw new InvalidOperationException(
                            $"{unmatched} of {inserted + unmatched} rows have no gantry/plaza match " +
                            $"(limit {options.MaxUnmatchedPercent}% = {allowed}); rolled back.");
                    if (inserted == 0)
                        throw new InvalidOperationException("Source returned no rows for this day; rolled back.");

                    await tx.CommitAsync();
                    committed = true;
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            }

            // The new revision is committed -- now drop the superseded revisions' rows
            // ("always replace the day"). Batched so the log never balloons.
            foreach (var old in previous.Where(p => p.FileId != newFileId && p.FileId.HasValue))
            {
                var removed = await DeleteOldRowsAsync(target, old.FileId!.Value);
                Log($"Removed {removed} row(s) of superseded FileID {old.FileId} (rev {old.Rev})");
            }

            await FinishFileRowAsync(target, sessionId, fileName, day, host, StatusDone, inserted, unmatched);
            await FinishSessionAsync(target, sessionId, 1);
            Log($"Done: {inserted} row(s) loaded, {unmatched} skipped (no gantry match) in {stopwatch.Elapsed.TotalMinutes:F1} min");
        }
        catch
        {
            if (newFileId != 0)
            {
                try
                {
                    // Mark ours failed and give the previous revision's status back
                    // (SP_INS_IMPORT_FILES flipped it to 99 when ours was created).
                    await RestoreAfterFailureAsync(target, newFileId, previous, committed);
                }
                catch (Exception ex)
                {
                    Log($"Could not restore ProcessedFiles status: {ex.Message}");
                }
            }
            try { await FinishSessionAsync(target, sessionId, 0); } catch { /* best effort */ }
            throw;
        }
    }

    // ------------------------------------------------------------ source -> rows

    private sealed class Stats
    {
        public long Read, Inserted, Unmatched, MissingUfm;
        public readonly SortedDictionary<int, long> ByShift = new();
        public readonly SortedDictionary<int, long> ByPlaza = new();
        public readonly Dictionary<string, long> UnmatchedKeys = new();
        public decimal SumExp, SumColl;

        public void Print(Action<string> log)
        {
            log($"Source rows read: {Read}  loaded: {Inserted}  skipped (no gantry match): {Unmatched}  without UFM_CALC: {MissingUfm}");
            log($"Sum TollExp {SumExp:F2}  Sum TollColl {SumColl:F2}");
            log("By shift: " + string.Join(", ", ByShift.Select(kv => $"{kv.Key}={kv.Value}")));
            log("By PlazaID: " + string.Join(", ", ByPlaza.Select(kv => $"{kv.Key}={kv.Value}")));
            foreach (var kv in UnmatchedKeys.OrderByDescending(k => k.Value).Take(20))
                log($"  unmatched: {kv.Key} x{kv.Value}");
        }
    }

    private static DataTable BuildTable()
    {
        var t = new DataTable();
        t.Columns.Add("ImportFileID", typeof(long));
        t.Columns.Add("ImportFileLineNumber", typeof(int));
        t.Columns.Add("TransDate", typeof(DateTime));
        t.Columns.Add("Plaza", typeof(string));
        t.Columns.Add("LaneGroup", typeof(string));
        t.Columns.Add("Gantry", typeof(int));
        t.Columns.Add("Lane", typeof(int));
        t.Columns.Add("PlazaID", typeof(int));
        t.Columns.Add("Shift", typeof(int));
        t.Columns.Add("Class", typeof(int));
        t.Columns.Add("TollExp", typeof(double));
        t.Columns.Add("TollColl", typeof(double));
        t.Columns.Add("TollType", typeof(string));
        t.Columns.Add("Axel", typeof(int));
        t.Columns.Add("UFM_CALC", typeof(string));
        t.Columns.Add("TollFull", typeof(double));
        t.Columns.Add("TollDay", typeof(DateTime));
        t.Columns.Add("IVISAxles", typeof(int));
        return t;
    }

    private static async Task StreamSourceAsync(
        LoaderSettings settings, DateTime day, PlazaMatcher matcher, Dictionary<int, int> orgByPlaza,
        long fileId, Stats stats, Func<DataTable, Task>? sink)
    {
        var eastern = FindEasternZone();

        await using var source = new SqlConnection(BuildConnectionString(settings.Source));
        await source.OpenAsync();

        await using var command = source.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]";
        command.CommandTimeout = settings.Source.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@inOrgID", SqlDbType.Int) { Value = 0 });
        command.Parameters.Add(new SqlParameter("@ReportType", SqlDbType.Int) { Value = 1 });
        command.Parameters.Add(new SqlParameter("@GroupBy", SqlDbType.Int) { Value = 1 });
        command.Parameters.Add(new SqlParameter("@selectVal", SqlDbType.NVarChar, -1) { Value = DBNull.Value });
        command.Parameters.Add(new SqlParameter("@inStartDate", SqlDbType.Date) { Value = day.Date });
        command.Parameters.Add(new SqlParameter("@inEndDate", SqlDbType.Date) { Value = day.Date });
        command.Parameters.Add(new SqlParameter("@inDayType", SqlDbType.Int) { Value = 2 });
        command.Parameters.Add(new SqlParameter("@inLaneGroupID", SqlDbType.VarChar, 10) { Value = "0" });

        var table = BuildTable();
        var lineNumber = 0;

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (!HasColumn(reader, "LaneGroupName"))
                continue;   // some procedures emit informational result sets first

            var oTrans = reader.GetOrdinal("TransDate");
            var oShift = reader.GetOrdinal("Shift");
            var oLane = reader.GetOrdinal("Lane");
            var oClass = reader.GetOrdinal("VehClass");
            var oFull = reader.GetOrdinal("TollFull");
            var oCharged = reader.GetOrdinal("TollCharged");
            var oColl = reader.GetOrdinal("TollCollected");
            var oIvis = reader.GetOrdinal("IVISAxles");
            var oCollAx = reader.GetOrdinal("CollectorAxles");
            var oType = reader.GetOrdinal("TransType");
            var oOrg = reader.GetOrdinal("OrgName");
            var oTollDay = reader.GetOrdinal("TollDay");
            var oLgName = reader.GetOrdinal("LaneGroupName");

            while (await reader.ReadAsync())
            {
                stats.Read++;

                var transDate = reader.GetDateTime(oTrans);
                var plaza = CleanText(reader.GetValue(oOrg).ToString());
                var (laneGroup, gantry) = ParseLaneGroup(reader.GetValue(oLgName).ToString() ?? "");

                var plazaId = matcher.Find(plaza, laneGroup, gantry);
                if (plazaId is null)
                {
                    stats.Unmatched++;
                    var key = $"plaza='{plaza}' lanegroup='{laneGroup}' gantry={gantry}";
                    stats.UnmatchedKeys[key] = stats.UnmatchedKeys.GetValueOrDefault(key) + 1;
                    continue;
                }

                var lane = ToInt(reader.GetValue(oLane)) ?? 0;
                var shift = reader.IsDBNull(oShift) ? ShiftFor(transDate) : ToInt(reader.GetValue(oShift))!.Value;
                var charged = ToDouble(reader.GetValue(oCharged));
                var collected = ToDouble(reader.GetValue(oColl));

                var ufm = BuildUfm(orgByPlaza, plazaId.Value, lane, transDate, eastern);
                if (ufm is null) stats.MissingUfm++;

                lineNumber++;
                stats.Inserted++;
                stats.ByShift[shift] = stats.ByShift.GetValueOrDefault(shift) + 1;
                stats.ByPlaza[plazaId.Value] = stats.ByPlaza.GetValueOrDefault(plazaId.Value) + 1;
                stats.SumExp += (decimal)(charged ?? 0);
                stats.SumColl += (decimal)(collected ?? 0);

                if (sink is null)
                    continue;

                var row = table.NewRow();
                row["ImportFileID"] = fileId;
                row["ImportFileLineNumber"] = lineNumber;
                row["TransDate"] = transDate;
                row["Plaza"] = plaza;
                row["LaneGroup"] = laneGroup;
                row["Gantry"] = gantry;
                row["Lane"] = lane;
                row["PlazaID"] = plazaId.Value;
                row["Shift"] = shift;
                row["Class"] = (object?)ToInt(reader.GetValue(oClass)) ?? DBNull.Value;
                row["TollExp"] = (object?)charged ?? DBNull.Value;
                row["TollColl"] = (object?)collected ?? DBNull.Value;
                row["TollType"] = CleanText(reader.GetValue(oType).ToString());
                row["Axel"] = (object?)ToInt(reader.GetValue(oCollAx)) ?? DBNull.Value;
                row["UFM_CALC"] = (object?)ufm ?? DBNull.Value;
                row["TollFull"] = (object?)ToDouble(reader.GetValue(oFull)) ?? DBNull.Value;
                row["TollDay"] = reader.IsDBNull(oTollDay) ? DBNull.Value : reader.GetDateTime(oTollDay);
                row["IVISAxles"] = (object?)ToInt(reader.GetValue(oIvis)) ?? DBNull.Value;
                table.Rows.Add(row);

                if (table.Rows.Count >= BatchRows)
                {
                    await sink(table);
                    table.Clear();
                    Log($"  ... {stats.Inserted} rows loaded");
                }
            }
        }
        while (await reader.NextResultAsync());

        if (sink is not null && table.Rows.Count > 0)
            await sink(table);
    }

    private static bool HasColumn(SqlDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int? ToInt(object value) =>
        value is DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static double? ToDouble(object value) =>
        value is DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    // The legacy C++ importer's clean_doubleSpace: trim + collapse runs of spaces.
    private static string CleanText(string? value) =>
        Regex.Replace((value ?? "").Trim(), @"\s{2,}", " ");

    // "SR924 Gantry 40" -> ("SR924", 40); "836-97 Gantry 21" -> ("836-97", 21).
    // No "Gantry" -> gantry -1, which can never match and so falls into the skipped bucket.
    private static (string LaneGroup, int Gantry) ParseLaneGroup(string laneGroupName)
    {
        var idx = laneGroupName.IndexOf("Gantry", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return (CleanText(laneGroupName), -1);

        var laneGroup = CleanText(laneGroupName[..idx]);
        var rest = laneGroupName[(idx + "Gantry".Length)..].Trim();
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return (laneGroup, digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : -1);
    }

    // Fallback only (the procedure returns Shift itself): matches legacy data --
    // shift 1 05:00-13:30, shift 2 13:30-21:30, shift 3 the rest.
    private static int ShiftFor(DateTime t)
    {
        var m = t.Hour * 60 + t.Minute;
        if (m >= 5 * 60 && m < 13 * 60 + 30) return 1;
        if (m >= 13 * 60 + 30 && m < 21 * 60 + 30) return 2;
        return 3;
    }

    private static TimeZoneInfo FindEasternZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
    }

    // Same string SP_DATA_1220_INSERT builds with FORMAT(...):
    // ORG_ID(0#) Lane(0#) yy(0#) MM(0#) dd(0#) HH(0#) mm(0#) ss(0#) fff(00#)
    // where the time is TransDate read as UTC and converted to Eastern.
    private static string? BuildUfm(
        Dictionary<int, int> orgByPlaza, int plazaId, int lane, DateTime transDate, TimeZoneInfo eastern)
    {
        if (!orgByPlaza.TryGetValue(plazaId, out var orgId))
            return null;

        var utc = DateTime.SpecifyKind(transDate, DateTimeKind.Utc);
        var e = TimeZoneInfo.ConvertTimeFromUtc(utc, eastern);
        var ci = CultureInfo.InvariantCulture;
        return orgId.ToString("0#", ci) + lane.ToString("0#", ci) +
               (e.Year - 2000).ToString("0#", ci) + e.Month.ToString("0#", ci) + e.Day.ToString("0#", ci) +
               e.Hour.ToString("0#", ci) + e.Minute.ToString("0#", ci) + e.Second.ToString("0#", ci) +
               e.Millisecond.ToString("00#", ci);
    }

    // Mirrors the PlazaID lookup in SP_DATA_1220_INSERT: same gantry AND (same plaza
    // name OR same lane group OR the road code found in the lane group / plaza name
    // equals the table's CoreRoad). Case-insensitive like the server's collation.
    private sealed class PlazaMatcher
    {
        private readonly List<GantryConvRow> _rows;
        private readonly Dictionary<(string, string, int), int?> _cache = new();

        public PlazaMatcher(List<GantryConvRow> rows) => _rows = rows;

        public int? Find(string plaza, string laneGroup, int gantry)
        {
            var key = (plaza.ToUpperInvariant(), laneGroup.ToUpperInvariant(), gantry);
            if (_cache.TryGetValue(key, out var hit))
                return hit;

            var lgRoad = RoadCode(laneGroup);
            var plazaRoad = RoadCode(plaza);
            var ids = _rows
                .Where(r => r.Gantry == gantry &&
                    (Eq(plaza, r.Plaza) || Eq(laneGroup, r.LaneGroup) ||
                     (lgRoad is not null && Eq(lgRoad, r.CoreRoad)) ||
                     (plazaRoad is not null && Eq(plazaRoad, r.CoreRoad))))
                .Select(r => r.PlazaId)
                .Distinct()
                .ToList();

            if (ids.Count > 1)
                Log($"WARNING: ambiguous PlazaID for plaza='{plaza}' lanegroup='{laneGroup}' gantry={gantry}: {string.Join(",", ids)} (using {ids[0]})");

            int? result = ids.Count > 0 ? ids[0] : null;
            _cache[key] = result;
            return result;
        }

        private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string? RoadCode(string s) =>
            s.Contains("924") ? "SR924" :
            s.Contains("112") ? "SR112" :
            s.Contains("874") ? "SR874" :
            s.Contains("836") ? "SR836" :
            s.Contains("878") ? "SR878" : null;
    }

    // ------------------------------------------------------------ target-side helpers

    private static async Task EnsureNewColumnsExistAsync(SqlConnection target)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Data_1220') " +
            "AND name IN ('TollFull','TollDay','IVISAxles')";
        var found = (int)(await cmd.ExecuteScalarAsync())!;
        if (found != 3)
            throw new InvalidOperationException(
                "dbo.Data_1220 is missing TollFull/TollDay/IVISAxles -- run Data_1220_alter.sql first.");
    }

    private static async Task<List<GantryConvRow>> ReadGantryConvAsync(SqlConnection target)
    {
        var rows = new List<GantryConvRow>();
        await using var cmd = target.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT Plaza, LaneGroup, Gantry, PlazaID, CoreRoad " +
            "FROM GmxParameters.dbo.GantryLaneConversionTable (NOLOCK)";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new GantryConvRow(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    private static async Task<Dictionary<int, int>> ReadPlazaOrgsAsync(SqlConnection target)
    {
        var map = new Dictionary<int, int>();
        await using var cmd = target.CreateCommand();
        cmd.CommandText =
            "SELECT PLAZA_ID, ORG_ID FROM GmxParameters.dbo.PlazaDefinitions (NOLOCK) " +
            "WHERE PLAZA_ID IS NOT NULL AND ORG_ID IS NOT NULL";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var plaza = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
            var org = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
            if (map.TryGetValue(plaza, out var existing) && existing != org)
                Log($"WARNING: PlazaDefinitions has conflicting ORG_ID for PLAZA_ID {plaza} ({existing} vs {org})");
            map[plaza] = org;
        }
        return map;
    }

    private sealed record FileRev(long? FileId, int Rev, int Status, string? ProcHost, string? ProcUser);

    private static async Task<List<FileRev>> ReadRevisionsAsync(SqlConnection target, string fileName)
    {
        var list = new List<FileRev>();
        await using var cmd = target.CreateCommand();
        cmd.CommandText =
            "SELECT FileID, Revision, ISNULL(Status,0), ProcHost, ProcUser FROM dbo.ProcessedFiles (NOLOCK) " +
            "WHERE ImportFileName = @n AND FileType = @t ORDER BY Revision";
        cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 512) { Value = fileName });
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 128) { Value = FileType });
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new FileRev(
                r.IsDBNull(0) ? null : r.GetInt64(0), r.GetInt32(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    private static async Task<long> CreateSessionAsync(SqlConnection target, string host, string user)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.SP_CREATE_IMPORT_SESSION";
        cmd.Parameters.Add(new SqlParameter("@SESSION_HOST", SqlDbType.NVarChar, 256) { Value = host });
        cmd.Parameters.Add(new SqlParameter("@SESSION_USER", SqlDbType.NVarChar, 256) { Value = user });
        var ret = new SqlParameter("@ret", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        cmd.Parameters.Add(ret);
        await cmd.ExecuteNonQueryAsync();
        // The procedure RETURNs SessionID as an int, same as the legacy importer reads it.
        return Convert.ToInt64(ret.Value, CultureInfo.InvariantCulture);
    }

    private static async Task FinishSessionAsync(SqlConnection target, long sessionId, int files)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.SP_UPDATE_IMPORT_SESSION";
        cmd.Parameters.Add(new SqlParameter("@SESSION_ID", SqlDbType.BigInt) { Value = sessionId });
        cmd.Parameters.Add(new SqlParameter("@NOF_FILES", SqlDbType.Int) { Value = files });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertFileRowAsync(
        SqlConnection target, long sessionId, string fileName, DateTime day, string host, int status)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.SP_INS_IMPORT_FILES";
        AddFileParameters(cmd, sessionId, fileName, day, host, status, 0, 0);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task FinishFileRowAsync(
        SqlConnection target, long sessionId, string fileName, DateTime day, string host,
        int status, long linesRead, long writeErrors)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.SP_UPDATE_IMP_FILES";
        AddFileParameters(cmd, sessionId, fileName, day, host, status, linesRead, writeErrors);
        cmd.Parameters.Add(new SqlParameter("@OldStatus", SqlDbType.Int) { Value = StatusLoading });
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddFileParameters(
        SqlCommand cmd, long sessionId, string fileName, DateTime day, string host,
        int status, long linesRead, long writeErrors)
    {
        cmd.Parameters.Add(new SqlParameter("@SessionID", SqlDbType.BigInt) { Value = sessionId });
        cmd.Parameters.Add(new SqlParameter("@ImportFileName", SqlDbType.NVarChar, 256) { Value = fileName });
        cmd.Parameters.Add(new SqlParameter("@FileType", SqlDbType.NVarChar, 64) { Value = FileType });
        cmd.Parameters.Add(new SqlParameter("@FileSize", SqlDbType.BigInt) { Value = 0L });
        cmd.Parameters.Add(new SqlParameter("@IntervalBegin", SqlDbType.DateTime2) { Value = day.Date });
        cmd.Parameters.Add(new SqlParameter("@IntervalEnd", SqlDbType.DateTime2) { Value = day.Date.AddDays(1).AddSeconds(-1) });
        cmd.Parameters.Add(new SqlParameter("@LinesRead", SqlDbType.Int) { Value = (int)linesRead });
        cmd.Parameters.Add(new SqlParameter("@ProcessStart", SqlDbType.DateTime2) { Value = DateTime.Now });
        cmd.Parameters.Add(new SqlParameter("@ProcessEnd", SqlDbType.DateTime2) { Value = DateTime.Now });
        cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.Int) { Value = status });
        cmd.Parameters.Add(new SqlParameter("@ProcHost", SqlDbType.NVarChar, 256) { Value = host });
        cmd.Parameters.Add(new SqlParameter("@ProcUser", SqlDbType.NVarChar, 256) { Value = LoaderUser });
        cmd.Parameters.Add(new SqlParameter("@ReadErrors", SqlDbType.Int) { Value = 0 });
        cmd.Parameters.Add(new SqlParameter("@WriteErrors", SqlDbType.Int) { Value = (int)writeErrors });
        cmd.Parameters.Add(new SqlParameter("@FileLastUpdate_UTC", SqlDbType.BigInt) { Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        cmd.Parameters.Add(new SqlParameter("@ImportFilePath", SqlDbType.NVarChar, 256) { Value = "CPC:[RPrl].[uspr_trn_CPC_TransactionDetails_withETCNum]" });
    }

    // Same read-back the legacy importer does: FileID of the highest revision.
    private static async Task<long> ReadLatestFileIdAsync(SqlConnection target, string fileName)
    {
        await using var cmd = target.CreateCommand();
        cmd.CommandText =
            "SELECT TOP 1 FileID FROM dbo.ProcessedFiles WHERE ImportFileName = @n AND FileType = @t " +
            "ORDER BY Revision DESC";
        cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.NVarChar, 512) { Value = fileName });
        cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 128) { Value = FileType });
        var id = await cmd.ExecuteScalarAsync();
        if (id is null or DBNull)
            throw new InvalidOperationException("Could not read back the new ImportFileID.");
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    private static async Task<long> DeleteOldRowsAsync(SqlConnection target, long fileId)
    {
        long total = 0;
        while (true)
        {
            await using var cmd = target.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "DELETE TOP (200000) FROM dbo.Data_1220 WHERE ImportFileID = @id";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = fileId });
            var n = await cmd.ExecuteNonQueryAsync();
            total += n;
            if (n == 0)
                return total;
        }
    }

    private static async Task RestoreAfterFailureAsync(
        SqlConnection target, long newFileId, List<FileRev> previous, bool dataCommitted)
    {
        // If the data had already been committed, the day is loaded; just don't leave
        // the file row flagged "loading".
        await using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "UPDATE dbo.ProcessedFiles SET Status = @s WHERE FileID = @id";
            cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.Int) { Value = dataCommitted ? StatusDone : StatusFailed });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = newFileId });
            await cmd.ExecuteNonQueryAsync();
        }

        if (dataCommitted)
            return;

        // The rollback left the previous revision's rows untouched, so put its status back.
        foreach (var prev in previous.Where(p => p.FileId.HasValue && p.Status != StatusSuperseded))
        {
            await using var cmd = target.CreateCommand();
            cmd.CommandText = "UPDATE dbo.ProcessedFiles SET Status = @s WHERE FileID = @id AND Status = @sup";
            cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.Int) { Value = prev.Status });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = prev.FileId!.Value });
            cmd.Parameters.Add(new SqlParameter("@sup", SqlDbType.Int) { Value = StatusSuperseded });
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------ arguments / settings / log

    private static Options ParseArguments(string[] args)
    {
        DateTime? sd = null, ed = null;
        var dryRun = false;
        var force = false;
        var maxUnmatched = 0.1;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-d": sd = ed = ParseDate(args, ++i); break;
                case "-sd": sd = ParseDate(args, ++i); break;
                case "-ed": ed = ParseDate(args, ++i); break;
                case "--dry-run": dryRun = true; break;
                case "--force-replace-legacy": force = true; break;
                case "--max-unmatched-percent":
                    maxUnmatched = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[i]}'. Usage: TransCoreLoader [-d yyyy-MM-dd | -sd yyyy-MM-dd -ed yyyy-MM-dd] " +
                        "[--dry-run] [--force-replace-legacy] [--max-unmatched-percent 0.1]  (default: yesterday)");
            }
        }

        if (sd is null && ed is null)
            sd = ed = DateTime.Today.AddDays(-1);
        sd ??= ed;
        ed ??= sd;
        if (ed < sd)
            throw new ArgumentException("End date cannot be before start date.");

        return new Options(sd!.Value.Date, ed!.Value.Date, dryRun, force, maxUnmatched);
    }

    private static DateTime ParseDate(string[] args, int i)
    {
        if (i >= args.Length)
            throw new ArgumentException("Missing date value.");
        return DateTime.ParseExact(args[i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static LoaderSettings LoadSettings()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var settings = Read(Path.Combine(AppContext.BaseDirectory, "appsettings.json")) ?? new LoaderSettings();

        // appsettings.local.json is git-ignored and holds the real credentials.
        var local = Read(Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"));
        if (local is not null && !string.IsNullOrWhiteSpace(local.Source.Server))
            settings = local;

        if (string.IsNullOrWhiteSpace(settings.Source.Server) || string.IsNullOrWhiteSpace(settings.Target.Server))
            throw new InvalidOperationException("Source/Target SQL settings are not configured (appsettings.local.json).");
        return settings;

        LoaderSettings? Read(string path) =>
            File.Exists(path) ? JsonSerializer.Deserialize<LoaderSettings>(File.ReadAllText(path), options) : null;
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

    private static void OpenLog()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        _log = new StreamWriter(
            Path.Combine(dir, $"TransCoreLoader_{DateTime.Now:yyyyMMdd}.log"), append: true, Encoding.UTF8)
        { AutoFlush = true };
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        Console.WriteLine(line);
        _log?.WriteLine(line);
    }
}
