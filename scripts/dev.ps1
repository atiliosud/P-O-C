<#
.SYNOPSIS
  Starts the Case API (with the AI backend in the same process), the web app and, with -Cosmos, the MCP server.
.EXAMPLE
  scripts\dev.ps1 -Seed
#>
[CmdletBinding()]
param(
    [switch]$Shadow,
    [switch]$Seed,
    [switch]$Cosmos
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$apiPort = 5080
$mcpPort = 5090
# Host port of the emulator (docker-compose.yml uses the same variable).
$cosmosPort = if ($env:COSMOS_EMULATOR_PORT) { $env:COSMOS_EMULATOR_PORT } else { '8081' }
$children = New-Object System.Collections.ArrayList

if ($Cosmos -and [string]::IsNullOrEmpty($env:CASEAGENTS_COSMOS_CONNECTION_STRING)) {
    Write-Error 'Cosmos mode needs the emulator connection string in CASEAGENTS_COSMOS_CONNECTION_STRING (see the Cosmos DB emulator docs).'
    exit 1
}

function Start-Child([string]$file, [string]$arguments) {
    $p = Start-Process -FilePath $file -ArgumentList $arguments -NoNewWindow -PassThru
    [void]$children.Add($p)
}

try {
    # Random 48-char signing key for this run.
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $key = -join (1..48 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Auth__Mode = 'Dev'
    $env:Auth__Dev__SigningKey = $key
    $env:AiBackend__ShadowMode = $Shadow.IsPresent.ToString().ToLowerInvariant()

    if ($Cosmos) {
        $env:Storage__Mode = 'Cosmos'
        $env:Storage__Cosmos__ConnectionString = $env:CASEAGENTS_COSMOS_CONNECTION_STRING
        $env:Storage__Cosmos__EmulatorMode = 'true'
        $env:Storage__Cosmos__EnsureCreated = 'true'
        Write-Host 'Starting Cosmos DB emulator (docker compose up -d)...'
        docker compose up -d
        if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }

        # curl.exe (Windows 10+) with -k accepts the emulator's self-signed certificate. A PowerShell certificate
        # callback does not work here: Windows PowerShell runs it on a thread without a runspace and the TLS call fails.
        Write-Host 'Waiting for the emulator (up to 180 s)...'
        $ready = $false
        for ($i = 0; $i -lt 90 -and -not $ready; $i++) {
            curl.exe -ksf -o NUL --max-time 3 "https://localhost:$cosmosPort/_explorer/emulator.pem"
            if ($LASTEXITCODE -eq 0) { $ready = $true } else { Start-Sleep -Seconds 2 }
        }
        if (-not $ready) { throw "Cosmos emulator did not answer on https://localhost:$cosmosPort." }
    } else {
        $env:Storage__Mode = 'InMemory'
    }

    if (-not (Test-Path (Join-Path $root 'web\node_modules'))) {
        Write-Host 'Installing web dependencies...'
        npm.cmd --prefix web ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }

    Write-Host 'Building solution once...'
    dotnet build CaseAgents.sln -nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    Start-Child 'dotnet' "run --project src/CaseAgents.Api --no-build --no-launch-profile --urls http://localhost:$apiPort"
    if ($Cosmos) {
        Start-Child 'dotnet' "run --project src/CaseAgents.Mcp --no-build --no-launch-profile --urls http://localhost:$mcpPort"
    }
    Start-Child 'npm.cmd' '--prefix web run dev'

    if ($Seed) {
        $ok = $false
        for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
            try {
                Invoke-WebRequest -Uri "http://localhost:$apiPort/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
                $ok = $true
            } catch {
                Start-Sleep -Seconds 1
            }
        }
        if (-not $ok) { throw 'API did not become healthy within 60 s.' }
        node scripts/seed.mjs --api "http://localhost:$apiPort"
        if ($LASTEXITCODE -ne 0) { throw 'Seeding failed.' }
    }

    Write-Host "API http://localhost:$apiPort | web http://localhost:5173 | Ctrl+C to stop"
    if ($Cosmos) { Write-Host "MCP server http://localhost:$mcpPort/mcp" }
    Write-Host 'Log in as: A. Silva / Reviewer (analyst), alice / Submitter'
    while ($true) {
        Start-Sleep -Seconds 1
        foreach ($c in $children) {
            if ($c.HasExited) { throw "Process $($c.Id) exited with code $($c.ExitCode)." }
        }
    }
}
finally {
    Write-Host 'Stopping child processes...'
    foreach ($c in $children) {
        if (-not $c.HasExited) { & taskkill.exe /T /F /PID $c.Id | Out-Null }
    }
}
