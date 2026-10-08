$ErrorActionPreference = 'Stop'

$connectionString = 'Server=GDC-SLO-P-VMS02\SQLEXPRESS;Database=FishPack3.0;Integrated Security=True;TrustServerCertificate=True'
$workbookPath = 'C:\Users\per.bang\Downloads\1788515702940.xlsx'

Add-Type -AssemblyName System.Data
$connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
$connection.Open()
$transaction = $connection.BeginTransaction()
$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$book = $null

function Get-CellText($sheet, [int]$row, [int]$column) {
    $value = [string]$sheet.Cells.Item($row, $column).Text
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

try {
    $check = $connection.CreateCommand()
    $check.Transaction = $transaction
    $check.CommandText = 'SELECT COUNT(*) FROM dbo.OTM_Import'
    if ([int]$check.ExecuteScalar() -ne 0) { throw 'dbo.OTM_Import is not empty; import was refused.' }

    $book = $excel.Workbooks.Open($workbookPath, $null, $true)
    $sheet = $book.Worksheets.Item(1)
    $range = $sheet.UsedRange
    $headers = @{}
    for ($column = 1; $column -le $range.Columns.Count; $column++) {
        $headers[[string]$sheet.Cells.Item(1, $column).Text] = $column
    }

    $columns = @('Title', 'Tender', 'AWB Number', 'Order #', 'STATUS', 'User Defined Image 1 Description', 'BOX', 'Service Provider ID', 'Destination City', 'Destination Location ID', 'AIR Routing', 'Start Time', 'End Time', 'Comment', 'Comment 2', 'Comment 3', 'DLV Status', 'Pallets', 'Enroute', 'Mode', 'Domain Name', 'PK')
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    $columnSql = ($columns | ForEach-Object { '[' + $_ + ']' }) -join ', '
    $parameterSql = (1..$columns.Count | ForEach-Object { '@p' + $_ }) -join ', '
    $command.CommandText = "INSERT INTO dbo.OTM_Import ($columnSql) VALUES ($parameterSql)"

    for ($row = 2; $row -le $range.Rows.Count; $row++) {
        $values = @(
            (Get-CellText $sheet $row $headers['PK']),
            (Get-CellText $sheet $row $headers['Tender']),
            (Get-CellText $sheet $row $headers['AWB Number']),
            (Get-CellText $sheet $row $headers['Order #']),
            (Get-CellText $sheet $row $headers['STATUS']),
            (Get-CellText $sheet $row $headers['User Defined Image 1 Description']),
            (Get-CellText $sheet $row $headers['BOX']),
            (Get-CellText $sheet $row $headers['Service Provider ID']),
            (Get-CellText $sheet $row $headers['Destination City']),
            (Get-CellText $sheet $row $headers['Destination Location ID']),
            (Get-CellText $sheet $row $headers['AIR Routing']),
            (Get-CellText $sheet $row $headers['Start Time']),
            (Get-CellText $sheet $row $headers['End Time']),
            (Get-CellText $sheet $row $headers['Comment']),
            (Get-CellText $sheet $row $headers['Comment 2']),
            (Get-CellText $sheet $row $headers['Comment 3']),
            (Get-CellText $sheet $row $headers['DLV Status']),
            (Get-CellText $sheet $row $headers['Pallets']),
            (Get-CellText $sheet $row $headers['Enroute']),
            (Get-CellText $sheet $row $headers['Mode']),
            (Get-CellText $sheet $row $headers['Domain Name']),
            (Get-CellText $sheet $row $headers['PK'])
        )

        $command.Parameters.Clear()
        for ($index = 0; $index -lt $values.Count; $index++) {
            $parameter = $command.Parameters.Add('@p' + ($index + 1), [System.Data.SqlDbType]::NVarChar, -1)
            if ($null -eq $values[$index]) { $parameter.Value = [DBNull]::Value } else { $parameter.Value = $values[$index] }
        }
        [void]$command.ExecuteNonQuery()
    }

    $transaction.Commit()
    Write-Host "OTM staging rows imported: $($range.Rows.Count - 1)"
}
catch {
    $transaction.Rollback()
    Write-Error "OTM import rolled back: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($book) { $book.Close($false) }
    $excel.Quit()
    if ($sheet) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($sheet) }
    if ($book) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($book) }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
    $connection.Close()
}