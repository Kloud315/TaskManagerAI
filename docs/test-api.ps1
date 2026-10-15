[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5057',
    [switch]$LeaveDemoTask
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$evidencePath = Join-Path $PSScriptRoot 'test-outputs.txt'
$settingsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.json'
$model = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Gemini.Model
$evidence = [System.Collections.Generic.List[string]]::new()
$createdTaskIds = [System.Collections.Generic.List[int]]::new()
$runSucceeded = $false
$cleanupSucceeded = $true

function Invoke-RecordedRequest {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][object]$Body
    )

    $uri = "$BaseUrl$Path"
    $requestBody = $null
    $responseBody = ''
    $statusCode = 0

    try {
        if ($null -eq $Body) {
            $response = Invoke-WebRequest -Uri $uri -Method $Method -UseBasicParsing -TimeoutSec 180
        }
        else {
            $requestBody = ConvertTo-Json -InputObject $Body -Depth 20 -Compress
            $response = Invoke-WebRequest `
                -Uri $uri `
                -Method $Method `
                -ContentType 'application/json' `
                -Body $requestBody `
                -UseBasicParsing `
                -TimeoutSec 180
        }

        $statusCode = [int]$response.StatusCode
        $responseBody = [string]$response.Content
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw
        }

        $httpResponse = $_.Exception.Response
        $statusCode = [int]$httpResponse.StatusCode
        $reader = New-Object System.IO.StreamReader($httpResponse.GetResponseStream())
        try {
            $responseBody = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }

    [void]$evidence.Add("=== $Label ===")
    [void]$evidence.Add("Request: $Method $Path")
    if ($null -ne $requestBody) {
        [void]$evidence.Add("Body: $requestBody")
    }
    [void]$evidence.Add("HTTP status: $statusCode")
    [void]$evidence.Add('Response:')
    [void]$evidence.Add($responseBody)
    [void]$evidence.Add('')

    return [pscustomobject]@{
        StatusCode = $statusCode
        Body = $responseBody
    }
}

function Invoke-AssistantMessage {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Message
    )

    for ($attempt = 0; $attempt -le 2; $attempt++) {
        $attemptLabel = if ($attempt -eq 0) {
            $Label
        }
        else {
            "$Label (retry $attempt)"
        }

        $result = Invoke-RecordedRequest `
            -Label $attemptLabel `
            -Method 'POST' `
            -Path '/api/assistant' `
            -Body @{ message = $Message }

        if ($result.StatusCode -ne 503 -or $attempt -eq 2) {
            return $result
        }

        [void]$evidence.Add('Assistant returned HTTP 503; retrying after 10 seconds.')
        Start-Sleep -Seconds 10
    }
}

function Assert-StatusCode {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][int]$ExpectedStatusCode,
        [Parameter(Mandatory)][string]$Operation
    )

    if ($Result.StatusCode -ne $ExpectedStatusCode) {
        throw "$Operation returned HTTP $($Result.StatusCode), expected HTTP $ExpectedStatusCode. See docs/test-outputs.txt."
    }
}

function Add-TestTaskId {
    param([Parameter(Mandatory)][int]$TaskId)

    if (-not $createdTaskIds.Contains($TaskId)) {
        $createdTaskIds.Add($TaskId)
    }
}

$runDate = Get-Date
[void]$evidence.Add("Validation date/time: $($runDate.ToString('yyyy-MM-dd HH:mm:ss zzz'))")
[void]$evidence.Add("Model: $model")
[void]$evidence.Add('')

try {
    $existingResponse = Invoke-WebRequest `
        -Uri "$BaseUrl/api/tasks" `
        -Method 'GET' `
        -UseBasicParsing `
        -TimeoutSec 30
    Assert-StatusCode ([pscustomobject]@{ StatusCode = [int]$existingResponse.StatusCode }) 200 'GET /api/tasks'
    $existingTasks = @($existingResponse.Content | ConvertFrom-Json)
    $existingIds = @($existingTasks | ForEach-Object { [int]$_.id })
    $missingId = [int]::MaxValue
    while ($existingIds -contains $missingId) {
        $missingId--
    }
    [void]$evidence.Add('=== Read existing tasks before the test ===')
    [void]$evidence.Add('Request: GET /api/tasks')
    [void]$evidence.Add("HTTP status: $([int]$existingResponse.StatusCode)")
    [void]$evidence.Add("Response: $($existingTasks.Count) task(s); task values omitted.")
    [void]$evidence.Add('')

    $title = "API smoke test $([Guid]::NewGuid().ToString('N'))"
    $dueDate = (Get-Date).Date.AddDays(7).ToString('yyyy-MM-dd')
    $created = Invoke-AssistantMessage `
        -Label 'Create a task through the assistant' `
        -Message "Add a task called '$title' due $dueDate"
    Assert-StatusCode $created 200 'Assistant create_task'
    $createdResponse = $created.Body | ConvertFrom-Json
    Add-TestTaskId ([int]$createdResponse.data.id)
    $createdTaskId = [int]$createdResponse.data.id
    if ($createdResponse.action -ne 'create_task' -or $createdResponse.data.title -ne $title) {
        throw 'The assistant create response did not match the requested task.'
    }
    if ([DateTime]$createdResponse.data.dueDate -ne [DateTime]$dueDate) {
        throw 'The assistant did not return the requested due date.'
    }

    $updated = Invoke-AssistantMessage `
        -Label 'Mark the created task complete through the assistant' `
        -Message "Mark task $createdTaskId as done"
    Assert-StatusCode $updated 200 'Assistant update_task'
    $updatedResponse = $updated.Body | ConvertFrom-Json
    if ($updatedResponse.action -ne 'update_task' -or -not $updatedResponse.data.isDone) {
        throw 'The assistant did not mark the created task complete.'
    }

    $missing = Invoke-AssistantMessage `
        -Label 'Request a task that does not exist' `
        -Message "Mark task $missingId as done"
    Assert-StatusCode $missing 404 'Assistant missing-task case'

    $outOfScope = Invoke-AssistantMessage `
        -Label 'Reject an out-of-scope question' `
        -Message "What's the capital of France?"
    Assert-StatusCode $outOfScope 200 'Assistant out-of-scope case'
    $outOfScopeResponse = $outOfScope.Body | ConvertFrom-Json
    if ($outOfScopeResponse.action -ne 'none') {
        throw 'The assistant did not reject the out-of-scope request.'
    }

    $sqlTitle = "SQL probe $([Guid]::NewGuid().ToString('N')) ; DROP TABLE Tasks;--"
    $sqlLikeTask = Invoke-AssistantMessage `
        -Label 'Store a SQL-looking title as ordinary text' `
        -Message "Add a task with the exact title '$sqlTitle'"
    Assert-StatusCode $sqlLikeTask 200 'Assistant SQL-looking title case'
    $sqlLikeResponse = $sqlLikeTask.Body | ConvertFrom-Json
    if ($null -ne $sqlLikeResponse.data.id) {
        Add-TestTaskId ([int]$sqlLikeResponse.data.id)
    }
    if ($sqlLikeResponse.action -ne 'create_task' -or $sqlLikeResponse.data.title -ne $sqlTitle) {
        throw 'The SQL-looking title was not stored exactly as ordinary task text.'
    }
    $sqlStoredTask = Invoke-RecordedRequest `
        -Label 'Read back the SQL-looking title' `
        -Method 'GET' `
        -Path "/api/tasks/$($sqlLikeResponse.data.id)" `
        -Body $null
    Assert-StatusCode $sqlStoredTask 200 'Read back SQL-looking task'
    if (($sqlStoredTask.Body | ConvertFrom-Json).title -ne $sqlTitle) {
        throw 'The SQL-looking task title did not round-trip unchanged.'
    }

    $marker = "ambiguity-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    foreach ($title in @("$marker report - Finish", "$marker report - Review")) {
        $seededTask = Invoke-RecordedRequest `
            -Label 'Create a task for the ambiguity test using CRUD' `
            -Method 'POST' `
            -Path '/api/tasks' `
            -Body @{ title = $title; isDone = $false }
        Assert-StatusCode $seededTask 201 'Create ambiguity-test task'
        Add-TestTaskId ([int](($seededTask.Body | ConvertFrom-Json).id))
    }

    $ambiguousDelete = Invoke-AssistantMessage `
        -Label 'Reject an ambiguous title-based deletion' `
        -Message "Delete the $marker report task"
    Assert-StatusCode $ambiguousDelete 409 'Assistant ambiguous-delete case'
    $runSucceeded = $true
}
finally {
    if ($createdTaskIds.Count -gt 0 -and -not $LeaveDemoTask) {
        foreach ($createdTaskId in $createdTaskIds) {
            try {
                $cleanup = Invoke-RecordedRequest `
                    -Label "Remove only test-created task $createdTaskId" `
                    -Method 'DELETE' `
                    -Path "/api/tasks/$createdTaskId" `
                    -Body $null
                Assert-StatusCode $cleanup 204 'Delete test task'
            }
            catch {
                $cleanupSucceeded = $false
                [void]$evidence.Add("WARNING: Cleanup failed for test task $createdTaskId`: $($_.Exception.Message)")
            }
        }

        try {
            $afterCleanupResponse = Invoke-WebRequest `
                -Uri "$BaseUrl/api/tasks" `
                -Method 'GET' `
                -UseBasicParsing `
                -TimeoutSec 30
            Assert-StatusCode ([pscustomobject]@{ StatusCode = [int]$afterCleanupResponse.StatusCode }) 200 'GET /api/tasks after cleanup'
            $remainingTasks = @($afterCleanupResponse.Content | ConvertFrom-Json)
            $remainingTestTasks = @($remainingTasks | Where-Object { $createdTaskIds.Contains([int]$_.id) })
            [void]$evidence.Add('=== Verify no task from this test remains ===')
            [void]$evidence.Add('Request: GET /api/tasks')
            [void]$evidence.Add("HTTP status: $([int]$afterCleanupResponse.StatusCode)")
            [void]$evidence.Add("Response: $($remainingTasks.Count) task(s); test tasks remaining: $($remainingTestTasks.Count).")
            [void]$evidence.Add('')
            if ($remainingTestTasks.Count -gt 0) {
                throw "Test task IDs still present: $(($remainingTestTasks.id) -join ', ')."
            }
        }
        catch {
            $cleanupSucceeded = $false
            [void]$evidence.Add("WARNING: Could not verify test-task cleanup: $($_.Exception.Message)")
        }
    }
    elseif ($createdTaskIds.Count -gt 0) {
        [void]$evidence.Add("Test tasks $($createdTaskIds -join ', ') were retained because -LeaveDemoTask was specified.")
    }

    if ($runSucceeded -and $cleanupSucceeded) {
        [System.IO.File]::WriteAllLines(
            $evidencePath,
            $evidence,
            (New-Object System.Text.UTF8Encoding($false)))
    }
    else {
        Write-Warning 'The smoke test did not fully pass; existing successful evidence was preserved.'
    }
}

Get-Content $evidencePath -Raw
