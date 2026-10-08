namespace FishPack3._0.Data;

public sealed record FlightSummary(
    int FlightId,
    string FlightNumber,
    DateTime? Departure,
    string From,
    string To,
    string Airline,
    string DeadlineWeight,
    bool Archived);

public sealed record BuildUpSummary(
    int UldId,
    string Flight,
    string PhysicalId,
    string Awb,
    string Destination,
    string PalletType,
    double? TotalBoxCount,
    bool BuildComplete);

public sealed record UldReadinessSummary(
    int UldId,
    string Flight,
    string PhysicalId,
    string PalletType,
    double? WeightTotal,
    double? TareWeight,
    double? TotalBoxCount,
    bool BuildComplete,
    bool WeighingComplete);

public sealed record AwbSummary(
    string AwbNumber,
    string OrderNumber,
    string Destination,
    double? AmountOrdered,
    string Comment);

public sealed record UldOperationsSummary(
    int UldId,
    string Flight,
    string PhysicalId,
    string Awb,
    string Destination,
    string PalletType,
    string Contour,
    double? WeightTotal,
    double? TareWeight,
    double? TotalBoxCount,
    bool BuildComplete,
    bool WeighingComplete,
    bool ReadyForShipment,
    bool Shipped);

public sealed record FlightOperations(
    FlightSummary Flight,
    IReadOnlyList<AwbSummary> Awbs,
    IReadOnlyList<UldOperationsSummary> Ulds);

public sealed record OtmAutomationResult(
    int OtmRowsConsidered,
    int FlightsCreated,
    int AwbsCreated,
    int UldsCreated,
    int RowsSkippedWithoutAwb,
    int ExistingAwbsSkipped);

public sealed record OtmStagingSummary(
    int Id,
    string Pk,
    string AwbNumber,
    string OrderNumber,
    string Destination,
    string Routing,
    string StartTime,
    string Status,
    bool HasAwb);

public sealed record FishPackDashboard(
    IReadOnlyList<FlightSummary> Flights,
    IReadOnlyList<BuildUpSummary> BuildUps,
    IReadOnlyList<UldReadinessSummary> ReadyForWeighing,
    IReadOnlyList<UldReadinessSummary> ReadyForCarriage);