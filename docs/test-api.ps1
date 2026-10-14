[CmdletBinding()]
param(
    [switch]$Case5Only
)

$ErrorActionPreference = 'Stop'

$baseUrl = 'http://localhost:5057'
$evidencePath = Join-Path $PSScriptRoot 'test-outputs.txt'
$settingsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.json'
$model = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Gemini.Model
$runDate = Get-Date

$runHeader = @(
    "Date/time: $($runDate.ToString('yyyy-MM-dd HH:mm:ss zzz'))"
    "Model (appsettings.json): $model"
    ''
)
if (Test-Path $evidencePath) {
    $runHeader | Add-Content -Path $evidencePath -Encoding UTF8
} else {
    $runHeader | Set-Content -Path $evidencePath -Encoding UTF8
}

function Add-EvidenceEntry {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Request,
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Response
    )

    @(
        "=== $Label ==="
        "Request: $Request"
        "HTTP status: $Status"
        'Response body:'
        $Response
        ''
    ) | Add-Content -Path $evidencePath -Encoding UTF8
}

function Invoke-RecordedRequest {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][object]$Body
    )

    $requestFile = [System.IO.Path]::GetTempFileName()
    $responseFile = [System.IO.Path]::GetTempFileName()
    $requestText = ''

    try {
        $curlArgs = @(
            '--silent',
            '--show-error',
            '--output', $responseFile,
            '--write-out', '%{http_code}',
            '--request', $Method,
            '--header', 'Content-Type: application/json'
        )

        if ($null -ne $Body) {
            $requestText = $Body | ConvertTo-Json -Depth 20 -Compress
            [System.IO.File]::WriteAllText(
                $requestFile,
                $requestText,
                (New-Object System.Text.UTF8Encoding($false)))
            $curlArgs += @('--data-binary', "@$requestFile")
        }

        $uri = "$baseUrl$Path"
        $curlArgs += $uri
        $curlOutput = & curl.exe @curlArgs 2>&1
        $curlExitCode = $LASTEXITCODE
        $responseText = [System.IO.File]::ReadAllText($responseFile)
        $status = ($curlOutput -join "`n").Trim()

        if ($curlExitCode -ne 0) {
            $responseText = "curl.exe exit code $curlExitCode`r`n$($curlOutput -join "`n")`r`n$responseText"
            if (-not $status) {
                $status = 'curl failed; no HTTP status received'
            }
        }

        $requestDescription = "$Method $uri"
        if ($null -ne $Body) {
            $requestDescription += "`r`nRequest body: $requestText"
        }

        Add-EvidenceEntry -Label $Label -Request $requestDescription -Status $status -Response $responseText
        [pscustomobject]@{
            Status = $status
            Body = $responseText
            CurlExitCode = $curlExitCode
        }
    }
    finally {
        Remove-Item -LiteralPath $requestFile, $responseFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-AssistantMessage {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Message
    )

    Invoke-RecordedRequest -Label $Label -Method 'POST' -Path '/api/assistant' -Body @{ message = $Message }
}

function Get-ConfirmationToken {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ResponseBody
    )

    try {
        $parsedResponse = $ResponseBody | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Add-EvidenceEntry -Label $Label -Request 'Parse the preceding API response as JSON and read confirmationToken' `
            -Status 'PARSE FAILED' -Response "JSON parse error: $($_.Exception.Message)"
        return $null
    }

    return $parsedResponse.confirmationToken
}

if ($Case5Only) {
    Invoke-AssistantMessage -Label 'Case 5: show all tasks (invalid API key)' `
        -Message 'Show me all tasks'
    return
}

$localDbConnection = New-Object System.Data.SqlClient.SqlConnection `
    'Server=(localdb)\MSSQLLocalDB;Database=TaskManagerAI;Integrated Security=True;'
$localDbResetSucceeded = $false
try {
    $localDbConnection.Open()
    $localDbCommand = $localDbConnection.CreateCommand()
    $localDbCommand.CommandText = 'TRUNCATE TABLE dbo.Tasks;'
    $localDbCommand.ExecuteNonQuery() | Out-Null
    $localDbResetSucceeded = $true
    Add-EvidenceEntry -Label 'Requested LocalDB reset' `
        -Request 'TRUNCATE TABLE dbo.Tasks on (localdb)\MSSQLLocalDB, database TaskManagerAI' `
        -Status 'SUCCEEDED' -Response 'TRUNCATE TABLE dbo.Tasks completed.'
}
catch {
    Add-EvidenceEntry -Label 'Requested LocalDB reset' `
        -Request 'TRUNCATE TABLE dbo.Tasks on (localdb)\MSSQLLocalDB, database TaskManagerAI' `
        -Status 'FAILED' -Response "LOCALDB_CONNECTION_FAILED: $($_.Exception.Message)"
}
finally {
    $localDbConnection.Dispose()
}

$sqlConnection = $null
if (-not $localDbResetSucceeded) {
    $sqlConnection = New-Object System.Data.SqlClient.SqlConnection `
        'Server=KLOUD-LAPTOP\SQLEXPRESS;Database=TaskManagerAI;Integrated Security=True;TrustServerCertificate=True;'
    try {
        $sqlConnection.Open()
        $sqlCommand = $sqlConnection.CreateCommand()
        $sqlCommand.CommandText = @'
TRUNCATE TABLE dbo.Tasks;
SELECT CONCAT(
    'TRUNCATE TABLE dbo.Tasks succeeded; row_count=',
    COUNT(*),
    '; identity_current=',
    COALESCE(CONVERT(varchar(30), IDENT_CURRENT('dbo.Tasks')), 'NULL')
)
FROM dbo.Tasks;
'@
        $resetResult = [string]$sqlCommand.ExecuteScalar()
        Add-EvidenceEntry -Label 'Authorized SQLEXPRESS database reset' `
            -Request 'TRUNCATE TABLE dbo.Tasks on KLOUD-LAPTOP\SQLEXPRESS, database TaskManagerAI' `
            -Status 'SUCCEEDED' -Response $resetResult
    }
    catch {
        $failure = "SQLEXPRESS_RESET_FAILED: $($_.Exception.Message)"
        Add-EvidenceEntry -Label 'Authorized SQLEXPRESS database reset' `
            -Request 'TRUNCATE TABLE dbo.Tasks on KLOUD-LAPTOP\SQLEXPRESS, database TaskManagerAI' `
            -Status 'FAILED' -Response $failure
        throw $failure
    }
    finally {
        if ($null -ne $sqlConnection) {
            $sqlConnection.Dispose()
        }
    }
}

$today = (Get-Date).Date
$daysUntilFriday = (5 - [int]$today.DayOfWeek + 7) % 7
if ($daysUntilFriday -eq 0) {
    $daysUntilFriday = 7
}
$nextFriday = $today.AddDays($daysUntilFriday)
$happyPathResult = Invoke-AssistantMessage -Label 'Happy path a: create task due next Friday' `
    -Message "Add a task called 'Call dentist' due next Friday"

$reportedDueDate = $null
$dateParseError = $null
try {
    $happyResponse = $happyPathResult.Body | ConvertFrom-Json -ErrorAction Stop
    $reportedDueDate = $happyResponse.data.dueDate
} catch {
    $dateParseError = $_.Exception.Message
}
$dueDateMatches = $false
if ($reportedDueDate) {
    try {
        $dueDateMatches = ([datetime]$reportedDueDate).Date -eq $nextFriday
    } catch {
        $dateParseError = $_.Exception.Message
    }
}
Add-EvidenceEntry -Label 'Happy path a: date verification' `
    -Request "Get-Date; calculate the next Friday strictly after $($today.ToString('yyyy-MM-dd')); compare with the dueDate in the preceding API response" `
    -Status 'CHECKED' `
    -Response "Today=$($today.ToString('yyyy-MM-dd')); nextFriday=$($nextFriday.ToString('yyyy-MM-dd')); API_dueDate=$reportedDueDate; matches=$dueDateMatches; parse_error=$dateParseError"

Invoke-AssistantMessage -Label 'Happy path b: mark task 1 done' -Message 'Mark task 1 as done' | Out-Null
Invoke-AssistantMessage -Label 'Happy path c: list tasks not done' -Message "Show me everything that's not done yet" | Out-Null

Invoke-AssistantMessage -Label 'Error 1: out of scope' -Message "What's the capital of France?" | Out-Null
Invoke-AssistantMessage -Label 'Error 2: nonexistent task' -Message 'Mark task 999 as done' | Out-Null

Invoke-AssistantMessage -Label 'Error 3: create Finish report' -Message "Add a task called 'Finish report'" | Out-Null
Invoke-AssistantMessage -Label 'Error 3: create Review report' -Message "Add a task called 'Review report'" | Out-Null
Invoke-AssistantMessage -Label 'Error 3: ambiguous delete' -Message 'Delete the report task' | Out-Null
Invoke-RecordedRequest -Label 'Error 3: verify tasks remain after ambiguous delete' `
    -Method 'GET' -Path '/api/tasks' -Body $null | Out-Null

Invoke-AssistantMessage -Label 'Error 4a: missing title - Add a task' -Message 'Add a task' | Out-Null
Invoke-AssistantMessage -Label 'Error 4b: missing title - no name' -Message 'Create a task with no name' | Out-Null
Invoke-AssistantMessage -Label 'Error 4c: missing title - no title' -Message 'Add a task with no title' | Out-Null

Invoke-AssistantMessage -Label 'Error 6: SQL-looking title' `
    -Message "Add a task called '); DROP TABLE Tasks;--" | Out-Null
Invoke-RecordedRequest -Label 'Error 6: verify tasks table and stored title' `
    -Method 'GET' -Path '/api/tasks' -Body $null | Out-Null

Invoke-AssistantMessage -Label 'Delete confirmation: create target task' `
    -Message "Add a task called 'Temporary delete confirmation target'" | Out-Null
$deleteRequest = Invoke-AssistantMessage -Label 'Delete confirmation: request delete and capture token' `
    -Message "Delete task called 'Temporary delete confirmation target'"
$deleteToken = Get-ConfirmationToken -Label 'Delete confirmation: parse confirmation token' `
    -ResponseBody $deleteRequest.Body
if ($deleteToken) {
    $confirmation = @{ confirmationToken = $deleteToken; confirm = $true }
    Invoke-RecordedRequest -Label 'Delete confirmation: confirm true' `
        -Method 'POST' -Path '/api/assistant/confirm-delete' -Body $confirmation | Out-Null
    Invoke-RecordedRequest -Label 'Delete confirmation: reuse consumed token' `
        -Method 'POST' -Path '/api/assistant/confirm-delete' -Body $confirmation | Out-Null
} else {
    Add-EvidenceEntry -Label 'Delete confirmation: token-dependent steps' `
        -Request 'Use confirmationToken from the preceding delete request' `
        -Status 'NOT RUN' -Response 'The preceding API response did not contain a confirmationToken.'
}

Invoke-AssistantMessage -Label 'Delete confirmation: create cancellation target' `
    -Message "Add a task called 'Keep this task after cancellation'" | Out-Null
$cancelRequest = Invoke-AssistantMessage -Label 'Delete confirmation: request delete for cancellation' `
    -Message "Delete task called 'Keep this task after cancellation'"
$cancelToken = Get-ConfirmationToken -Label 'Delete confirmation: parse cancellation token' `
    -ResponseBody $cancelRequest.Body
if ($cancelToken) {
    $cancellation = @{ confirmationToken = $cancelToken; confirm = $false }
    Invoke-RecordedRequest -Label 'Delete confirmation: confirm false' `
        -Method 'POST' -Path '/api/assistant/confirm-delete' -Body $cancellation | Out-Null
} else {
    Add-EvidenceEntry -Label 'Delete confirmation: confirm false step' `
        -Request 'Use confirmationToken from the preceding cancellation delete request with confirm=false' `
        -Status 'NOT RUN' -Response 'The preceding API response did not contain a confirmationToken.'
}

Get-Content $evidencePath -Raw
