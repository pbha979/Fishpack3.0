$ErrorActionPreference = 'Stop'

$connectionString = 'Server=GDC-SLO-P-VMS02\SQLEXPRESS;Database=FishPack3.0;Integrated Security=True;TrustServerCertificate=True'
$root = 'C:\Users\per.bang\Downloads'

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
$connection.Open()
$transaction = $connection.BeginTransaction()

function Get-Value($row, [string]$name) {
    $value = $row.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value) -or $value -eq '#NULL!') { return $null }
    return ([string]$value).Trim().Trim('"')
}

function To-Number($value) {
    if ($null -eq $value) { return $null }
    $normalized = ([string]$value).Replace(([string][char]0xA0), '').Replace(' ', '').Replace(',', '.')
    return [double]::Parse($normalized, [Globalization.CultureInfo]::InvariantCulture)
}

function To-Integer($value) {
    if ($null -eq $value) { return $null }
    return [int](To-Number $value)
}

function To-Bit($value) {
    if ($null -eq $value) { return $null }
    return [int](([string]$value) -in @('Sant', 'True', '1', 'Yes'))
}

function To-Date($value) {
    if ($null -eq $value) { return $null }
    foreach ($format in @("dd'.'MM'.'yyyy HH':'mm", "dd'.'MM'.'yyyy", "yyyy'-'MM'-'dd HH':'mm':'ss")) {
        try { return [datetime]::ParseExact($value, $format, [Globalization.CultureInfo]::InvariantCulture) } catch { }
    }
    throw "Unsupported date value: $value"
}

function Add-ImportRows([string]$table, [string[]]$columns, [object[]]$rows, [scriptblock]$values, [bool]$identity) {
    if ($identity) { Invoke-Sql ('SET IDENTITY_INSERT dbo.[' + $table + '] ON') }
    try {
        $columnSql = ($columns | ForEach-Object { '[' + $_ + ']' }) -join ', '
        $parameterSql = (1..$columns.Count | ForEach-Object { '@p' + $_ }) -join ', '
        foreach ($row in $rows) {
            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = "INSERT INTO dbo.[$table] ($columnSql) VALUES ($parameterSql)"
            $rowValues = & $values $row
            for ($index = 0; $index -lt $columns.Count; $index++) {
                $column = $columns[$index]
                $sqlType = [System.Data.SqlDbType]::NVarChar
                if ($column -in @('ID', 'FLIGHT_ID', 'AWB_ID', 'ULD_ID', 'PALLET_TYPE_ID')) { $sqlType = [System.Data.SqlDbType]::Int }
                elseif ($column -in @('AMOUNT_ORDERED', 'Box_Order1', 'Box_Order2', 'Box_Order3', 'Weight_1', 'Weight_2', 'Weight_3', 'TaraWeight', 'WeightTotal_TAG', 'TotalBoxCount')) { $sqlType = [System.Data.SqlDbType]::Float }
                elseif ($column -in @('RAIN', 'ULD_FINALIZED_REPORT', 'FLIGHT_FINISHED_REPORT', 'FLIGHT_LIST_REPORT', 'Archived', 'READY_FOR_SHIPMENT', 'BIRD_COVER', 'TRANSFER_MARKER', 'Weighing Compleate', 'BUILD_COMPLATE', 'TAG_Printed', 'SHIPPED', 'NetChange')) { $sqlType = [System.Data.SqlDbType]::Bit }
                elseif ($column -like '*TIME' -or $column -in @('DEPARTURE', 'SHIP DATE', 'Modified')) { $sqlType = [System.Data.SqlDbType]::DateTime2 }
                $parameter = $command.Parameters.Add('@p' + ($index + 1), $sqlType)
                if ($null -eq $rowValues[$index]) { $parameter.Value = [DBNull]::Value } else { $parameter.Value = $rowValues[$index] }
            }
            [void]$command.ExecuteNonQuery()
        }
    }
    finally {
        if ($identity) { Invoke-Sql ('SET IDENTITY_INSERT dbo.[' + $table + '] OFF') }
    }
    Write-Host "$table imported: $($rows.Count)"
}

function Invoke-Sql([string]$sql) {
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()
}

try {
    $palletTypes = Import-Csv (Join-Path $root 'PALLET_TYPES.csv')
    Add-ImportRows 'PALLET_TYPES' @('PALLET_TYPE_ID', 'NAME', 'DEFAULT_TARE_KG') $palletTypes {
        param($row)
        ,@((To-Integer (Get-Value $row 'PALLET_TYPE_ID')), (Get-Value $row 'NAME'), (To-Number (Get-Value $row 'DEFAULT_TARE_KG')))
    } $true

    $airlines = Import-Csv (Join-Path $root 'AIRLINES.csv')
    Add-ImportRows 'AIRLINES' @('AIRLINE_ID', 'CODE', 'NAME', 'ULD_REPORT_MAIL_ADDRESS') $airlines {
        param($row)
        ,@((Get-Value $row 'AIRLINE_ID'), (Get-Value $row 'CODE'), (Get-Value $row 'Navn'), (Get-Value $row 'ULD_REPORT_MAIL_ADDRESS'))
    } $false

    $airports = Import-Csv (Join-Path $root 'Airports.csv') | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Code) }
    Add-ImportRows 'Airports' @('Code', 'Airport', 'Country') $airports {
        param($row)
        ,@((Get-Value $row 'Code'), (Get-Value $row 'Airport'), (Get-Value $row 'Country'))
    } $false

    $flights = Import-Csv (Join-Path $root 'FLIGHT.csv')
    Add-ImportRows 'FLIGHT' @('FLIGHT_ID', 'AIRLINE', 'FLIGHT', 'DEPARTURE', 'FLIGHT_FROM', 'FLIGHT_TO', 'COMENT', 'RAIN', 'ULD_FINALIZED_REPORT', 'FLIGHT_FINISHED_REPORT', 'FLIGHT_LIST_REPORT', 'DEADLINE_WEIGHT', 'FLIGHT_FINISHED_TIME', 'Archived') $flights {
        param($row)
        ,@((To-Integer (Get-Value $row 'FLIGHT_ID')), (Get-Value $row 'AIRLINE'), (Get-Value $row 'FLIGHT'), (To-Date (Get-Value $row 'DEPARTURE')), (Get-Value $row 'FLIGHT_FROM'), (Get-Value $row 'FLIGHT_TO'), (Get-Value $row 'COMENT'), (To-Bit (Get-Value $row 'RAIN')), (To-Bit (Get-Value $row 'ULD_FINALIZED_REPORT')), (To-Bit (Get-Value $row 'FLIGHT_FINISHED_REPORT')), (To-Bit (Get-Value $row 'FLIGHT_LIST_REPORT')), (Get-Value $row 'DEADLINE_WEIGHT'), (To-Date (Get-Value $row 'FLIGHT_FINISHED_TIME')), (To-Bit (Get-Value $row 'Archived')))
    } $true

    $awbs = Import-Csv (Join-Path $root 'AWB.csv')
    Add-ImportRows 'AWB' @('AWB_ID', 'AWB_NUMBER', 'AMOUNT_ORDERED', 'DESTINATION', 'COMMENT', 'SHIP DATE', 'ORDERNUMBER', 'REMARKS', 'FLIGHT', 'Archived') $awbs {
        param($row)
        ,@((To-Integer (Get-Value $row 'AWB_ID')), (Get-Value $row 'AWB_NUMBER'), (To-Number (Get-Value $row 'AMOUNT_ORDERED')), (Get-Value $row 'DESTINATION'), (Get-Value $row 'COMMENT'), (To-Date (Get-Value $row 'SHIP DATE')), (Get-Value $row 'ORDERNUMBER'), (Get-Value $row 'REMARKS'), (To-Number (Get-Value $row 'FLIGHT')), (To-Bit (Get-Value $row 'Archived')))
    } $true

    $ulds = Import-Csv (Join-Path $root 'ULD.csv')
    Add-ImportRows 'ULD' @('ULD_ID', 'FLIGHT_ID', 'AwbOnULD', 'DESTINATION', 'TRANSFER1', 'TRANSFER1_FLIGHT_NO', 'TRANSFER2', 'TRANSFER2_FLIGHT_NO', 'CONTOUR', 'Order_1', 'Box_Order1', 'AMOUNT_ORDERED', 'Order_2', 'Box_Order2', 'Order_3', 'Box_Order3', 'PHYSICAL_ID', 'PALLET_TYPE_ID', 'READY_FOR_SHIPMENT', 'RAIN', 'BIRD_COVER', 'TRANSFER_MARKER', 'COMENT', 'Weight_1', 'Weight_2', 'Weight_3', 'Weighing Compleate', 'TaraWeight', 'BUILD_COMPLATE', 'REMARKS', 'TAG_Printed', 'Loaded_At', 'WeightTotal_TAG', 'TotalBoxCount', 'BuiltBy', 'WeighedBy', 'ControlledBy', 'BUILD_COMPLATE_TIME', 'SHIPPED', 'Weighing_Complate_Time', 'SHIPPED_Time', 'Weighed_Time', 'Archived', 'NetChange', 'Modified') $ulds {
        param($row)
        ,@((To-Integer (Get-Value $row 'ULD_ID')), (To-Number (Get-Value $row 'FLIGHT_ID')), (Get-Value $row 'AwbOnULD'), (Get-Value $row 'DESTINATION'), (Get-Value $row 'TRANSFER1'), (Get-Value $row 'TRANSFER1_FLIGHT_NO'), (Get-Value $row 'TRANSFER2'), (Get-Value $row 'TRANSFER2_FLIGHT_NO'), (Get-Value $row 'CONTOUR'), (Get-Value $row 'Order_1'), (To-Number (Get-Value $row 'Box_Order1')), (To-Number (Get-Value $row 'AMOUNT_ORDERED')), (Get-Value $row 'Order_2'), (To-Number (Get-Value $row 'Box_Order2')), (Get-Value $row 'Order_3'), (To-Number (Get-Value $row 'Box_Order3')), (Get-Value $row 'PHYSICAL_ID'), (To-Number (Get-Value $row 'PALLET_TYPE_ID')), (To-Bit (Get-Value $row 'READY_FOR_SHIPMENT')), (To-Bit (Get-Value $row 'RAIN')), (To-Bit (Get-Value $row 'BIRD_COVER')), (To-Bit (Get-Value $row 'TRANSFER_MARKER')), (Get-Value $row 'COMENT'), (To-Number (Get-Value $row 'Weight_1')), (To-Number (Get-Value $row 'Weight_2')), (To-Number (Get-Value $row 'Weight_3')), (To-Bit (Get-Value $row 'Weighing Compleate')), (To-Number (Get-Value $row 'TaraWeight')), (To-Bit (Get-Value $row 'BUILD_COMPLATE')), (Get-Value $row 'REMARKS'), (To-Bit (Get-Value $row 'TAG_Printed')), (Get-Value $row 'Loaded_At'), (To-Number (Get-Value $row 'WeightTotal_TAG')), (To-Number (Get-Value $row 'TotalBoxCount')), (Get-Value $row 'BuiltBy'), (Get-Value $row 'WeighedBy'), (Get-Value $row 'ControlledBy'), (To-Date (Get-Value $row 'BUILD_COMPLATE_TIME')), (To-Bit (Get-Value $row 'SHIPPED')), (To-Date (Get-Value $row 'Weighing_Complate_Time')), (To-Date (Get-Value $row 'SHIPPED_Time')), (To-Date (Get-Value $row 'Weighed_Time')), (To-Bit (Get-Value $row 'Archived')), (To-Bit (Get-Value $row 'NetChange')), (To-Date (Get-Value $row '_Created')))
    } $true

    $buildUpCharges = Import-Csv (Join-Path $root 'TblChargeBuildUpTo.csv')
    Add-ImportRows 'TblChargeBuildUpTo' @('ID', 'Suffix', 'Airline', 'Prefix', 'Hvem bygger?', 'REMARKS', 'Faktureres', 'epost2', 'epost1') $buildUpCharges {
        param($row)
        ,@((To-Integer (Get-Value $row 'ID')), (Get-Value $row 'Suffix'), (Get-Value $row 'Airline'), (Get-Value $row 'Prefix'), (Get-Value $row 'Hvem bygger?'), (Get-Value $row 'REMARKS'), (Get-Value $row 'Faktureres'), (Get-Value $row 'epost2'), (Get-Value $row 'epost1'))
    } $true

    $transaction.Commit()
    Write-Host 'Import committed successfully.'
}
catch {
    $transaction.Rollback()
    Write-Error "Import rolled back: $($_.Exception.Message)"
    exit 1
}
finally {
    $connection.Close()
}