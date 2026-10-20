<#
    Deploys the D2D gateway (ASP.NET Core, out-of-process) to the root application of https://gateway.drivetodev.online
    (FTP folder gateway.drivetodev.online). Same pattern as JodDaily's deploy: tests, publish, app_offline, upload, online, health checks.

    What it never does:
      - upload appsettings.Production.json, the JWT key or the Data Protection keys: they live on the server only;
      - delete anything on the server (no pruning): the sub-applications (joddaily, payyet, booksabuy ...) are folders below the
        site root and must stay untouched. Stale files from older versions are left in place.
    Dry run (connects read-only, changes nothing remote): $env:D2D_DEPLOY_DRY_RUN = "1"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Server,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Username,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Password,

    # FTP path relative to the FTP login root. Pinned below on purpose.
    [string]$RemotePath = "gateway.drivetodev.online",
    [string]$PublicBaseUrl = "https://gateway.drivetodev.online",

    [string]$Runtime = "win-x86",     # the shared app pool is 32-bit
    [switch]$FrameworkDependent,
    [switch]$StdoutLog,               # ANCM stdout logs in logs\ on the server (first deploy / debugging)
    [switch]$SkipTests,

    [ValidateRange(0, 600)] [int]$OfflineDrainSeconds = 20,
    [ValidateRange(1, 100)] [int]$HealthCheckAttempts = 12,
    [ValidateRange(1, 300)] [int]$HealthCheckDelaySeconds = 5
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$dryRun = $env:D2D_DEPLOY_DRY_RUN -eq "1"

# ---- 0. Guard rails ---------------------------------------------------------------------------------------
$target = $RemotePath.Trim('/').ToLowerInvariant()
if ($target -ne "gateway.drivetodev.online") {
    throw "Refusing to deploy: RemotePath must be 'gateway.drivetodev.online' (got '$RemotePath')."
}
$PublicBaseUrl = $PublicBaseUrl.TrimEnd('/')
if ($PublicBaseUrl -ne "https://gateway.drivetodev.online") {
    throw "Refusing to deploy: PublicBaseUrl must be 'https://gateway.drivetodev.online' (got '$PublicBaseUrl')."
}

$pwsh = if (Get-Command powershell -ErrorAction SilentlyContinue) { "powershell" } else { "pwsh" }
$uploader = Join-Path $repoRoot "upload-ftp.ps1"
$dryArgs = @(); if ($dryRun) { $dryArgs = @("-DryRun") }

function Assert-RemoteFolder([string]$Path) {
    try {
        $req = [System.Net.FtpWebRequest]::Create("ftp://$Server/")
        $req.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory
        $req.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
        $req.UsePassive = $true
        $resp = $req.GetResponse()
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $names = $reader.ReadToEnd() -split "`r?`n" | ForEach-Object { ($_.Trim().Replace('\', '/').TrimEnd('/') -split '/')[-1] }
        $reader.Close(); $resp.Close()
    }
    catch { throw "Cannot list ftp://$Server/ : $($_.Exception.Message)" }
    if ($names -notcontains $Path) { throw "Remote folder '$Path' does not exist on ftp://$Server/." }
}

# dotnet runs from a neutral folder: global.json in the repo pins SDK 8.0.x, which is not installed on every machine
# (the projects target net8.0 and build with any newer SDK).
function Invoke-Dotnet([string[]]$DotnetArgs) {
    Push-Location ([IO.Path]::GetTempPath())
    try { & dotnet @DotnetArgs; if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs[0]) failed (exit code $LASTEXITCODE)." } }
    finally { Pop-Location }
}

# ---- 1. Tests ---------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "1. Running tests..." -ForegroundColor Yellow
    Invoke-Dotnet @("test", "$repoRoot\D2D.sln", "-c", "Release", "--nologo", "-v", "q")
}

Assert-RemoteFolder $target

# ---- 2. Publish -------------------------------------------------------------------------------------------
$stage = Join-Path $repoRoot "deploy"
$appDir = Join-Path $stage "app"
$offlineDir = Join-Path $stage "offline"
foreach ($d in @($appDir, $offlineDir)) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d | Out-Null
}

$mode = if ($FrameworkDependent) { "framework-dependent" } else { "$Runtime self-contained" }
Write-Host "2. Publishing ($mode)..." -ForegroundColor Yellow
$publishArgs = @("publish", "$repoRoot\src\D2D.Api\D2D.Api.csproj", "-c", "Release", "-o", $appDir, "--nologo", "-v", "q")
if (-not $FrameworkDependent) { $publishArgs += @("-r", $Runtime, "--self-contained", "true") }
Invoke-Dotnet $publishArgs

# Secrets and server-only state must never travel with the package.
foreach ($name in @("appsettings.Production.json", "appsettings.Development.json")) {
    Remove-Item -LiteralPath (Join-Path $appDir $name) -Force -ErrorAction SilentlyContinue
}

$webConfigPath = Join-Path $appDir "web.config"
if (-not (Test-Path -LiteralPath $webConfigPath)) { throw "web.config missing from the publish output." }
[xml]$webConfig = Get-Content -LiteralPath $webConfigPath
$aspNetCore = $webConfig.SelectSingleNode("//aspNetCore")
if (-not $aspNetCore) { throw "aspNetCore element not found in web.config." }
$aspNetCore.SetAttribute("hostingModel", "outofprocess")
$aspNetCore.SetAttribute("stdoutLogEnabled", $(if ($StdoutLog) { "true" } else { "false" }))
if (-not $webConfig.SelectSingleNode("//location[@inheritInChildApplications='false']")) {
    throw "web.config lost inheritInChildApplications=false: the sub-applications (joddaily ...) would inherit the gateway's settings."
}
$webConfig.Save($webConfigPath)

New-Item -ItemType Directory -Path (Join-Path $appDir "logs") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDir "logs\README.txt") -Value "ANCM stdout logs are written here when stdoutLogEnabled=true."

# ---- 3. Maintenance page ----------------------------------------------------------------------------------
$offlinePath = Join-Path $offlineDir "app_offline.htm"
$templatePath = Join-Path $repoRoot "deploy\templates\app_offline.template.htm"
if (-not (Test-Path -LiteralPath $templatePath)) { throw "Missing maintenance page asset: $templatePath" }
Copy-Item -LiteralPath $templatePath -Destination $offlinePath -Force

$manifestPath = Join-Path $repoRoot ".deploy-cache\gateway-manifest.json"
function Send-Offline {
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $offlineDir -RemotePath $target @dryArgs
}

$deployed = $false
try {
    Write-Host "3. Taking the gateway offline..." -ForegroundColor Yellow
    Send-Offline
    if ($LASTEXITCODE -ne 0) { throw "Taking the application offline failed (exit code $LASTEXITCODE)." }
    if (-not $dryRun) { Write-Host "Waiting $OfflineDrainSeconds s for IIS to release files..."; Start-Sleep -Seconds $OfflineDrainSeconds }

    Write-Host "4. Uploading application files to ftp://$Server/$target (no pruning) ..." -ForegroundColor Yellow
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $appDir -RemotePath $target -ManifestPath $manifestPath @dryArgs
    if ($LASTEXITCODE -ne 0) { throw "Uploading application files failed (exit code $LASTEXITCODE). The gateway stays in maintenance mode; fix and rerun." }

    if ($dryRun) { Write-Host "Dry run complete. No remote files were changed." -ForegroundColor Cyan; return }

    Write-Host "5. Bringing the gateway back online..." -ForegroundColor Yellow
    $del = [System.Net.FtpWebRequest]::Create("ftp://$Server/$target/app_offline.htm")
    $del.Method = [System.Net.WebRequestMethods+Ftp]::DeleteFile
    $del.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $del.UsePassive = $true
    $del.GetResponse().Close()

    # ---- 6. Health checks -----------------------------------------------------------------------------------
    $ok = $false; $last = "no response"
    for ($i = 1; $i -le $HealthCheckAttempts; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "$PublicBaseUrl/health" -UseBasicParsing -TimeoutSec 30
            $last = "HTTP $([int]$r.StatusCode)"
            if ([int]$r.StatusCode -eq 200) { $ok = $true; break }
        }
        catch { $last = if ($_.Exception.Response) { "HTTP $([int]$_.Exception.Response.StatusCode)" } else { $_.Exception.Message } }
        if ($i -lt $HealthCheckAttempts) { Write-Host "Health check not ready ($last), attempt $i/$HealthCheckAttempts..." -ForegroundColor Gray; Start-Sleep -Seconds $HealthCheckDelaySeconds }
    }
    if (-not $ok) {
        Write-Warning "Health check failed ($last). Restoring maintenance mode."
        Send-Offline
        throw "$PublicBaseUrl/health did not return HTTP 200 after $HealthCheckAttempts attempts (last: $last). With -StdoutLog the reason is in $target/logs/ on the server."
    }

    $problems = @()
    foreach ($path in @("/.well-known/jwks.json", "/.well-known/openid-configuration")) {
        try { Invoke-WebRequest -Uri "$PublicBaseUrl$path" -UseBasicParsing -TimeoutSec 30 | Out-Null }
        catch { $problems += "$path -> $($_.Exception.Message)" }
    }
    try {   # the app credentials endpoint must refuse anonymous calls (and exist: it is new)
        Invoke-WebRequest -Uri "$PublicBaseUrl/api/v1/line/credentials" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "/api/v1/line/credentials answered an anonymous request"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($code -ne 401) { $problems += "/api/v1/line/credentials (anonymous) returned HTTP $code instead of 401 (is the new endpoint deployed?)" }
    }
    if ($problems.Count -gt 0) {
        Write-Warning ("Post-deploy checks reported problems:`n - " + ($problems -join "`n - "))
        throw "Deployment is up but not healthy. Fix the items above."
    }

    $deployed = $true
    Write-Host "Deployment verified: $PublicBaseUrl (health, JWKS, OIDC discovery, credentials endpoint guarded)." -ForegroundColor Green
}
catch {
    Write-Host "Deployment stopped: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    Remove-Item -LiteralPath $offlinePath -Force -ErrorAction SilentlyContinue
    Set-Location $repoRoot
}
if ($deployed) { Write-Host "--- Deployment complete ---" -ForegroundColor Cyan }
