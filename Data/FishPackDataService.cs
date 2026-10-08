using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FishPack3._0.Data;

public sealed class FishPackDataService
{
    private readonly string connectionString;

    public FishPackDataService(IConfiguration configuration)
    {
        connectionString = configuration.GetConnectionString("FishPack")
            ?? throw new InvalidOperationException("The FishPack SQL connection string is not configured.");
    }

    public async Task<FishPackDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var flights = await ReadFlightsAsync(connection, cancellationToken);
        var buildUps = await ReadBuildUpsAsync(connection, cancellationToken);
        var readyForWeighing = await ReadReadinessAsync(connection, "QryRedyForWeighing", cancellationToken);
        var readyForCarriage = await ReadReadinessAsync(connection, "QryRedyForCarrage", cancellationToken);

        return new FishPackDashboard(flights, buildUps, readyForWeighing, readyForCarriage);
    }

    public async Task<FlightOperations?> GetFlightOperationsAsync(int flightId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var flight = await ReadFlightAsync(connection, flightId, cancellationToken);
        if (flight is null)
        {
            return null;
        }

        var awbs = await ReadAwbsAsync(connection, flightId, cancellationToken);
        var ulds = await ReadUldsAsync(connection, flightId, cancellationToken);
        return new FlightOperations(flight, awbs, ulds);
    }

    public async Task<OtmAutomationResult> CreateMissingFromOtmAsync(
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var otmRows = await ReadOtmRowsAsync(connection, transaction, cancellationToken);
            var existingAwbs = await ReadExistingAwbsAsync(connection, transaction, cancellationToken);
            var existingFlights = await ReadExistingFlightsAsync(connection, transaction, cancellationToken);
            var flightsByKey = existingFlights.ToDictionary(item => item.Number, item => item.Id, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var flightsCreated = 0;
            var awbsCreated = 0;
            var uldsCreated = 0;
            var skippedWithoutAwb = 0;
            var existingAwbsSkipped = 0;

            foreach (var row in otmRows.Where(item => !string.IsNullOrWhiteSpace(item.AwbNumber)))
            {
                if (existingAwbs.Contains(row.AwbNumber))
                {
                    existingAwbsSkipped++;
                    continue;
                }

                var flightNumber = GetGeneratedFlightNumber(row.AirRouting, row.StartTime);
                if (!flightsByKey.TryGetValue(flightNumber, out var flightId))
                {
                    flightId = await InsertGeneratedFlightAsync(connection, transaction, row, flightNumber, now, createdBy, cancellationToken);
                    flightsByKey[flightNumber] = flightId;
                    flightsCreated++;
                }

                var awbId = await InsertGeneratedAwbAsync(connection, transaction, row, flightId, now, createdBy, cancellationToken);
                existingAwbs.Add(row.AwbNumber);
                awbsCreated++;

                var boxCount = Math.Max(0, (int)Math.Ceiling(row.BoxCount));
                var uldCount = Math.Max(1, (int)Math.Ceiling(boxCount / 161d));
                for (var index = 0; index < uldCount; index++)
                {
                    var boxes = index == uldCount - 1 ? boxCount - (161 * index) : 161;
                    await InsertGeneratedUldAsync(connection, transaction, row, flightId, boxes, now, createdBy, cancellationToken);
                    uldsCreated++;
                }
            }

            skippedWithoutAwb = otmRows.Count(item => string.IsNullOrWhiteSpace(item.AwbNumber));
            await transaction.CommitAsync(cancellationToken);
            return new OtmAutomationResult(otmRows.Count, flightsCreated, awbsCreated, uldsCreated, skippedWithoutAwb, existingAwbsSkipped);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<OtmStagingSummary>> GetOtmStagingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT TOP (500) ID, PK, [AWB Number], [Order #], [Destination Location ID], [AIR Routing], [Start Time], STATUS
            FROM dbo.OTM_Import
            ORDER BY ID DESC
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OtmStagingSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var awb = GetString(reader, "AWB Number");
            rows.Add(new OtmStagingSummary(
                reader.GetInt32(reader.GetOrdinal("ID")),
                GetString(reader, "PK"),
                awb,
                GetString(reader, "Order #"),
                GetString(reader, "Destination Location ID"),
                GetString(reader, "AIR Routing"),
                GetString(reader, "Start Time"),
                GetString(reader, "STATUS"),
                !string.IsNullOrWhiteSpace(awb)));
        }

        return rows;
    }

    private static async Task<List<OtmRow>> ReadOtmRowsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT [AWB Number], [Order #], [BOX], [Destination City], [Destination Location ID],
                   [AIR Routing], [Start Time], [Comment]
            FROM dbo.OTM_Import
            ORDER BY ID
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OtmRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OtmRow(
                GetString(reader, "AWB Number"),
                GetString(reader, "Order #"),
                ParseNumber(GetString(reader, "BOX")),
                GetString(reader, "Destination City"),
                GetString(reader, "Destination Location ID"),
                GetString(reader, "AIR Routing"),
                GetString(reader, "Start Time"),
                GetString(reader, "Comment")));
        }

        return rows;
    }

    private static async Task<HashSet<string>> ReadExistingAwbsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT AWB_NUMBER FROM dbo.AWB WHERE AWB_NUMBER IS NOT NULL", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) { values.Add(GetString(reader, "AWB_NUMBER")); }
        return values;
    }

    private static async Task<List<(string Number, int Id)>> ReadExistingFlightsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT FLIGHT_ID, FLIGHT FROM dbo.FLIGHT WHERE FLIGHT IS NOT NULL", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<(string Number, int Id)>();
        while (await reader.ReadAsync(cancellationToken)) { values.Add((GetString(reader, "FLIGHT"), reader.GetInt32(reader.GetOrdinal("FLIGHT_ID")))); }
        return values;
    }

    private static async Task<int> InsertGeneratedFlightAsync(SqlConnection connection, SqlTransaction transaction, OtmRow row, string flightNumber, DateTime now, string createdBy, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.FLIGHT (AIRLINE, FLIGHT, DEPARTURE, FLIGHT_FROM, FLIGHT_TO, COMENT,
                                    RAIN, ULD_FINALIZED_REPORT, FLIGHT_FINISHED_REPORT, FLIGHT_LIST_REPORT,
                                    DEADLINE_WEIGHT, Archived, Modified)
            OUTPUT INSERTED.FLIGHT_ID
            VALUES (@airline, @flight, @departure, 'OSL', @destination, 'Created from OTM staging',
                    0, 0, 0, 0, @deadline, 0, @modified)
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@airline", GetCarrier(flightNumber));
        command.Parameters.AddWithValue("@flight", flightNumber);
        command.Parameters.AddWithValue("@departure", ParseOtmDate(row.StartTime) ?? now);
        command.Parameters.AddWithValue("@destination", row.DestinationLocationId);
        command.Parameters.AddWithValue("@deadline", ParseOtmDate(row.StartTime)?.ToString("HH:mm") ?? "00:00");
        command.Parameters.AddWithValue("@modified", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertGeneratedAwbAsync(SqlConnection connection, SqlTransaction transaction, OtmRow row, int flightId, DateTime now, string createdBy, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.AWB (AWB_NUMBER, AMOUNT_ORDERED, DESTINATION, COMMENT, [SHIP DATE], ORDERNUMBER, REMARKS, FLIGHT, Archived, Modified)
            OUTPUT INSERTED.AWB_ID
            VALUES (@awb, @amount, @destination, @comment, @shipDate, @orderNumber, 'Created from OTM staging', @flight, 0, @modified)
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@awb", row.AwbNumber);
        command.Parameters.AddWithValue("@amount", row.BoxCount);
        command.Parameters.AddWithValue("@destination", row.DestinationLocationId);
        command.Parameters.AddWithValue("@comment", (object?)row.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("@shipDate", ParseOtmDate(row.StartTime) ?? now);
        command.Parameters.AddWithValue("@orderNumber", row.OrderNumber);
        command.Parameters.AddWithValue("@flight", flightId);
        command.Parameters.AddWithValue("@modified", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task InsertGeneratedUldAsync(SqlConnection connection, SqlTransaction transaction, OtmRow row, int flightId, int boxes, DateTime now, string createdBy, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ULD (FLIGHT_ID, AwbOnULD, DESTINATION, CONTOUR, Order_1, Box_Order1,
                                 AMOUNT_ORDERED, PALLET_TYPE_ID, READY_FOR_SHIPMENT, RAIN, BIRD_COVER,
                                 TRANSFER_MARKER, [Weighing Compleate], BUILD_COMPLATE, TotalBoxCount,
                                 REMARKS, TAG_Printed, SHIPPED, Archived, Modified)
            VALUES (@flight, @awb, @destination, 'P', @orderNumber, @boxes, @amount, 1, 0, 0, 0,
                    0, 0, 0, @boxes, 'Created from OTM staging', 0, 0, 0, @modified)
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@flight", flightId);
        command.Parameters.AddWithValue("@awb", row.AwbNumber);
        command.Parameters.AddWithValue("@destination", row.DestinationLocationId);
        command.Parameters.AddWithValue("@orderNumber", row.OrderNumber);
        command.Parameters.AddWithValue("@boxes", boxes);
        command.Parameters.AddWithValue("@amount", boxes);
        command.Parameters.AddWithValue("@modified", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetGeneratedFlightNumber(string routing, string startTime)
    {
        var match = Regex.Match(routing ?? string.Empty, "(?<carrier>[A-Z0-9]+)0*(?<number>\\d+)[/]0*(?<day>\\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) { return $"OTM-{ParseOtmDate(startTime)?.ToString("yyyyMMdd") ?? "UNKNOWN"}"; }
        return $"{match.Groups["carrier"].Value.ToUpperInvariant()}{match.Groups["number"].Value}-{match.Groups["day"].Value.PadLeft(2, '0')}";
    }

    private static string GetCarrier(string flightNumber) => Regex.Match(flightNumber, "^[A-Z0-9]+(?=\\d)").Value;
    private static double ParseNumber(string value) => double.TryParse(value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static DateTime? ParseOtmDate(string value)
    {
        var datePart = value?.Length >= 16 ? value[..16] : value;
        return DateTime.TryParseExact(datePart, "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : null;
    }

    private sealed record OtmRow(string AwbNumber, string OrderNumber, double BoxCount, string DestinationCity, string DestinationLocationId, string AirRouting, string StartTime, string Comment);

    private static async Task<FlightSummary?> ReadFlightAsync(SqlConnection connection, int flightId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FLIGHT_ID, FLIGHT, DEPARTURE, FLIGHT_FROM, FLIGHT_TO, AIRLINE, DEADLINE_WEIGHT, Archived
            FROM dbo.FLIGHT
            WHERE FLIGHT_ID = @flightId
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@flightId", flightId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FlightSummary(
            reader.GetInt32(reader.GetOrdinal("FLIGHT_ID")),
            GetString(reader, "FLIGHT"),
            GetNullableDateTime(reader, "DEPARTURE"),
            GetString(reader, "FLIGHT_FROM"),
            GetString(reader, "FLIGHT_TO"),
            GetString(reader, "AIRLINE"),
            GetString(reader, "DEADLINE_WEIGHT"),
            GetBoolean(reader, "Archived"));
    }

    private static async Task<List<AwbSummary>> ReadAwbsAsync(SqlConnection connection, int flightId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT AWB_NUMBER, ORDERNUMBER, DESTINATION, AMOUNT_ORDERED, COMMENT
            FROM dbo.AWB
            WHERE CONVERT(int, FLIGHT) = @flightId AND ISNULL(Archived, 0) = 0
            ORDER BY AWB_NUMBER
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@flightId", flightId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var awbs = new List<AwbSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            awbs.Add(new AwbSummary(
                GetString(reader, "AWB_NUMBER"),
                GetString(reader, "ORDERNUMBER"),
                GetString(reader, "DESTINATION"),
                GetNullableDouble(reader, "AMOUNT_ORDERED"),
                GetString(reader, "COMMENT")));
        }

        return awbs;
    }

    private static async Task<List<UldOperationsSummary>> ReadUldsAsync(SqlConnection connection, int flightId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT u.ULD_ID, u.PHYSICAL_ID, u.AwbOnULD, u.DESTINATION, p.NAME AS PALLET_TYPE,
                   u.CONTOUR, u.WeightTotal_TAG, u.TaraWeight, u.TotalBoxCount,
                   u.BUILD_COMPLATE, u.[Weighing Compleate], u.READY_FOR_SHIPMENT, u.SHIPPED
            FROM dbo.ULD AS u
            LEFT JOIN dbo.PALLET_TYPES AS p ON p.PALLET_TYPE_ID = CONVERT(int, u.PALLET_TYPE_ID)
            WHERE CONVERT(int, u.FLIGHT_ID) = @flightId
            ORDER BY u.ULD_ID
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@flightId", flightId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ulds = new List<UldOperationsSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ulds.Add(new UldOperationsSummary(
                reader.GetInt32(reader.GetOrdinal("ULD_ID")),
                GetString(reader, "FLIGHT"),
                GetString(reader, "PHYSICAL_ID"),
                GetString(reader, "AwbOnULD"),
                GetString(reader, "DESTINATION"),
                GetString(reader, "PALLET_TYPE"),
                GetString(reader, "CONTOUR"),
                GetNullableDouble(reader, "WeightTotal_TAG"),
                GetNullableDouble(reader, "TaraWeight"),
                GetNullableDouble(reader, "TotalBoxCount"),
                GetBoolean(reader, "BUILD_COMPLATE"),
                GetBoolean(reader, "Weighing Compleate"),
                GetBoolean(reader, "READY_FOR_SHIPMENT"),
                GetBoolean(reader, "SHIPPED")));
        }

        return ulds;
    }

    private static async Task<List<FlightSummary>> ReadFlightsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FLIGHT_ID, FLIGHT, DEPARTURE, FLIGHT_FROM, FLIGHT_TO, AIRLINE, DEADLINE_WEIGHT, Archived
            FROM dbo.FLIGHT
            WHERE ISNULL(Archived, 0) = 0
            ORDER BY DEPARTURE, FLIGHT
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var flights = new List<FlightSummary>();

        while (await reader.ReadAsync(cancellationToken))
        {
            flights.Add(new FlightSummary(
                reader.GetInt32(reader.GetOrdinal("FLIGHT_ID")),
                GetString(reader, "FLIGHT"),
                GetNullableDateTime(reader, "DEPARTURE"),
                GetString(reader, "FLIGHT_FROM"),
                GetString(reader, "FLIGHT_TO"),
                GetString(reader, "AIRLINE"),
                GetString(reader, "DEADLINE_WEIGHT"),
                GetBoolean(reader, "Archived")));
        }

        return flights;
    }

    private static async Task<List<BuildUpSummary>> ReadBuildUpsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                        SELECT f.FLIGHT, u.ULD_ID, u.PHYSICAL_ID, u.AwbOnULD, u.DESTINATION,
                                     p.NAME, u.TotalBoxCount, u.BUILD_COMPLATE
                        FROM dbo.ULD AS u
                        INNER JOIN dbo.FLIGHT AS f ON f.FLIGHT_ID = CONVERT(int, u.FLIGHT_ID)
                        INNER JOIN dbo.PALLET_TYPES AS p ON p.PALLET_TYPE_ID = CONVERT(int, u.PALLET_TYPE_ID)
                        WHERE ISNULL(u.BUILD_COMPLATE, 0) = 0
                            AND ISNULL(f.Archived, 0) = 0
                        ORDER BY f.DEADLINE_WEIGHT, f.FLIGHT, u.ULD_ID
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buildUps = new List<BuildUpSummary>();

        while (await reader.ReadAsync(cancellationToken))
        {
            buildUps.Add(new BuildUpSummary(
                reader.GetInt32(reader.GetOrdinal("ULD_ID")),
                GetString(reader, "FLIGHT"),
                GetString(reader, "PHYSICAL_ID"),
                GetString(reader, "AwbOnULD"),
                GetString(reader, "DESTINATION"),
                GetString(reader, "NAME"),
                GetNullableDouble(reader, "TotalBoxCount"),
                GetBoolean(reader, "BUILD_COMPLATE")));
        }

        return buildUps;
    }

    private static async Task<List<UldReadinessSummary>> ReadReadinessAsync(
        SqlConnection connection,
        string viewName,
        CancellationToken cancellationToken)
    {
        var readinessPredicate = viewName == "QryRedyForWeighing"
            ? "u.[Weighing Compleate] = 0 AND u.BUILD_COMPLATE = 1"
            : "u.[Weighing Compleate] = 1 AND u.BUILD_COMPLATE = 1";

        var sql = $"""
            SELECT u.ULD_ID, f.FLIGHT, u.PHYSICAL_ID, p.NAME AS PALLET_TYPES_NAME,
                   u.WeightTotal_TAG, u.TaraWeight, u.TotalBoxCount,
                   u.BUILD_COMPLATE, u.[Weighing Compleate]
            FROM dbo.ULD AS u
            INNER JOIN dbo.FLIGHT AS f ON f.FLIGHT_ID = CONVERT(int, u.FLIGHT_ID)
            INNER JOIN dbo.PALLET_TYPES AS p ON p.PALLET_TYPE_ID = CONVERT(int, u.PALLET_TYPE_ID)
            WHERE ISNULL(f.Archived, 0) = 0 AND {readinessPredicate}
            ORDER BY f.FLIGHT, u.ULD_ID
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var readiness = new List<UldReadinessSummary>();

        while (await reader.ReadAsync(cancellationToken))
        {
            readiness.Add(new UldReadinessSummary(
                reader.GetInt32(reader.GetOrdinal("ULD_ID")),
                GetString(reader, "FLIGHT"),
                GetString(reader, "PHYSICAL_ID"),
                GetString(reader, "PALLET_TYPES_NAME"),
                GetNullableDouble(reader, "WeightTotal_TAG"),
                GetNullableDouble(reader, "TaraWeight"),
                GetNullableDouble(reader, "TotalBoxCount"),
                GetBoolean(reader, "BUILD_COMPLATE"),
                GetBoolean(reader, "Weighing Compleate")));
        }

        return readiness;
    }

    private static string GetString(SqlDataReader reader, string columnName) =>
        reader[columnName] is DBNull ? string.Empty : Convert.ToString(reader[columnName]) ?? string.Empty;

    private static bool GetBoolean(SqlDataReader reader, string columnName) =>
        reader[columnName] is not DBNull && Convert.ToBoolean(reader[columnName]);

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string columnName) =>
        reader[columnName] is DBNull ? null : Convert.ToDateTime(reader[columnName]);

    private static double? GetNullableDouble(SqlDataReader reader, string columnName) =>
        reader[columnName] is DBNull ? null : Convert.ToDouble(reader[columnName]);
}