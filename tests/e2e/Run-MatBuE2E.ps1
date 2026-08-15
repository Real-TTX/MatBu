param(
    [switch]$KeepStack
)

$ErrorActionPreference = "Stop"
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ComposeFile = Join-Path $ProjectRoot "docker-compose.e2e.yml"
$BaseUrl = "http://localhost:9294"
$ComposeArgs = @("compose", "-p", "matbu-e2e", "-f", $ComposeFile)
$Session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & docker @ComposeArgs @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker compose schlug fehl: $($Arguments -join ' ')" }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Wait-Health {
    $deadline = (Get-Date).AddMinutes(2)
    do {
        try {
            $health = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 3
            if ($health.status -eq "Healthy") { return }
        } catch { Start-Sleep -Milliseconds 500 }
    } while ((Get-Date) -lt $deadline)
    throw "Die isolierte Primary wurde nicht rechtzeitig gesund."
}

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body)
    $parameters = @{
        Uri = "$BaseUrl$Path"
        Method = $Method
        WebSession = $Session
        TimeoutSec = 30
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }
    $result = Invoke-RestMethod @parameters
    if ($result -is [System.Array]) {
        foreach ($entry in $result) { Write-Output $entry }
        return
    }
    Write-Output $result
}

function Register-Secondary {
    $page = Invoke-WebRequest "$BaseUrl/Instances/Edit" -WebSession $Session -UseBasicParsing -TimeoutSec 10
    $match = [regex]::Match($page.Content, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"')
    Assert-True $match.Success "Antiforgery-Token der Instanzseite fehlt."
    $form = @{
        "__RequestVerificationToken" = $match.Groups[1].Value
        "Input.Name" = "E2E Secondary"
        "Input.Role" = "Secondary"
        "Input.Endpoint" = "http://primary:9293"
        "Input.Enabled" = "true"
        "InstanceToken" = "matbu-e2e-secondary-token"
    }
    Invoke-WebRequest "$BaseUrl/Instances/Edit" -Method Post -WebSession $Session -Body $form -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 10 | Out-Null
}

function New-ObjectEntry {
    param([string]$Name, [string]$Direction, [string]$Location, [long]$InstanceId)
    Invoke-Api "Post" "/api/objects" @{
        name = $Name
        kind = "LocalFolder"
        direction = $Direction
        location = $Location
        detail = "E2E"
        instanceId = $InstanceId
    }
}

function New-BackupTask {
    param(
        [string]$Name,
        [long]$SourceId,
        [long]$TargetId,
        [string]$ConsistencyMode = "None",
        [string]$Containers = ""
    )
    Invoke-Api "Post" "/api/tasks" @{
        name = $Name
        sourceId = $SourceId
        targetId = $TargetId
        method = "Full"
        compression = "None"
        consistencyMode = $ConsistencyMode
        consistencyContainerNames = $Containers
        preBackupCommand = ""
        postBackupCommand = ""
        consistencyTimeoutSeconds = 30
        sourceSelectionJson = "[]"
        chunkSizeMiB = 8
        schedule = "Alle 1 Stunden"
        retention = "30 Tage"
        maxRetryAttempts = 3
        retryDelayMinutes = 10
        enabled = $false
        labelIds = @()
    }
}

function Start-Task {
    param([long]$TaskId)
    Invoke-Api "Post" "/api/tasks/$TaskId/run" $null | Out-Null
}

function Wait-Job {
    param([long]$TaskId, [string[]]$States, [int]$TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $job = $null
        foreach ($candidate in (Invoke-Api "Get" "/api/transfer-jobs" $null)) {
            if ($candidate.taskId -eq $TaskId -and ($null -eq $job -or [long]$candidate.id -gt [long]$job.id)) {
                $job = $candidate
            }
        }
        $state = if ($null -eq $job) { "" } else { [string]$job.state }
        if ($States -contains $state) { return $job }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $lastState = if ($null -eq $job) { "kein Job" } else { [string]$job.state }
    throw "Task $TaskId erreichte [$($States -join ', ')] nicht; letzter Zustand: $lastState."
}

function Wait-ContainerPauseState {
    param([bool]$Paused, [int]$TimeoutSeconds = 30)
    $expected = $Paused.ToString().ToLowerInvariant()
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $actual = (& docker inspect --format "{{.State.Paused}}" matbu-e2e-consistency-helper 2>$null).Trim()
        if ($actual -eq $expected) { return }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw "Consistency-Container erreichte Paused=$expected nicht."
}

try {
    Write-Host "[E2E] Stack neu bauen und isoliert auf Port 9294 starten"
    Invoke-Compose "up" "-d" "--build" "--force-recreate"
    Wait-Health
    Invoke-Api "Post" "/api/auth/login" @{ userName = "admin"; password = "admin" } | Out-Null
    Register-Secondary

    Write-Host "[E2E] Secondary-Quelle vorbereiten und Reverse-Verbindung prüfen"
    Invoke-Compose "exec" "-T" "secondary" "sh" "-c" "mkdir -p /data/e2e/source && dd if=/dev/urandom of=/data/e2e/source/payload.bin bs=1M count=32 status=none"
    Invoke-Compose "exec" "-T" "primary" "sh" "-c" "mkdir -p /data/e2e/gateway-target /data/e2e/local-source /data/e2e/local-target"
    $secondarySource = New-ObjectEntry "E2E Secondary Source" "Source" "/data/e2e/source" 2
    $primaryTarget = New-ObjectEntry "E2E Primary Target" "Target" "/data/e2e/gateway-target" 1

    $connected = $false
    $deadline = (Get-Date).AddSeconds(45)
    do {
        try {
            $test = Invoke-Api "Post" "/api/objects/$($secondarySource.id)/test" $null
            $connected = $test.success
        } catch { $connected = $false }
        if (-not $connected) { Start-Sleep -Seconds 1 }
    } while (-not $connected -and (Get-Date) -lt $deadline)
    Assert-True $connected "Die Secondary hat ihre ausgehende Verbindung zur Primary nicht aufgebaut."

    Write-Host "[E2E] Backup Secondary -> Primary ausführen"
    $gatewayTask = New-BackupTask "E2E Secondary nach Primary" $secondarySource.id $primaryTarget.id
    Start-Task $gatewayTask.id
    $completed = Wait-Job $gatewayTask.id @("Completed") 90
    Assert-True ($completed.bytesTransferred -gt 0) "Gateway-Backup meldet keine übertragenen Bytes."
    Invoke-Compose "exec" "-T" "primary" "sh" "-c" "tar -tf /data/e2e/gateway-target/task-$($gatewayTask.id)-$($completed.id).tar | grep -q payload.bin"

    Write-Host "[E2E] Secondary-Ausfall muss zeitlich begrenzt fehlschlagen und danach fortsetzen"
    Invoke-Compose "stop" "secondary"
    Start-Task $gatewayTask.id
    $failed = Wait-Job $gatewayTask.id @("Fehler") 30
    Assert-True ($failed.error -match "keinen Fortschritt") "Der erwartete Inaktivitätsfehler wurde nicht protokolliert."
    Invoke-Compose "start" "secondary"
    $reconnected = $false
    $deadline = (Get-Date).AddSeconds(45)
    do {
        try {
            $test = Invoke-Api "Post" "/api/objects/$($secondarySource.id)/test" $null
            $reconnected = $test.success
        } catch { $reconnected = $false }
        if (-not $reconnected) { Start-Sleep -Seconds 1 }
    } while (-not $reconnected -and (Get-Date) -lt $deadline)
    Assert-True $reconnected "Die Secondary wurde nach dem simulierten Ausfall nicht wieder erreichbar."

    Write-Host "[E2E] Worker-Neustart während Docker Pause und automatische Freigabe"
    Invoke-Compose "exec" "-T" "primary" "sh" "-c" "dd if=/dev/urandom of=/data/e2e/local-source/large.bin bs=1M count=192 status=none"
    $localSource = New-ObjectEntry "E2E Local Source" "Source" "/data/e2e/local-source" 1
    $localTarget = New-ObjectEntry "E2E Local Target" "Target" "/data/e2e/local-target" 1
    $restartTask = New-BackupTask "E2E Worker Recovery" $localSource.id $localTarget.id "DockerPause" "matbu-e2e-consistency-helper"
    Start-Task $restartTask.id
    Wait-ContainerPauseState $true 45
    Invoke-Compose "kill" "worker"
    Invoke-Compose "up" "-d" "worker"
    Wait-ContainerPauseState $false 30
    $recovered = Wait-Job $restartTask.id @("Completed") 120
    Assert-True ($recovered.attempt -ge 2) "Der Worker-Job wurde nach dem Neustart nicht fortgesetzt."

    Write-Host "[E2E] ERFOLG: Gateway, Ausfall-Timeout, Resume und Docker-Cleanup sind grün."
}
finally {
    if (-not $KeepStack) {
        Write-Host "[E2E] Isolierten Stack und ausschließlich dessen Volumes entfernen"
        & docker @ComposeArgs down -v --remove-orphans
    } else {
        Write-Host "[E2E] Stack bleibt für Diagnose auf $BaseUrl aktiv."
    }
}
