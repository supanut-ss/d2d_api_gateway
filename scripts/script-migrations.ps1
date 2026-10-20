<#
.SYNOPSIS
  Writes the migrations of one provider as a SQL script a DBA can review and run (safe to run more than once).

.EXAMPLE
  ./scripts/script-migrations.ps1 SqlServer d2d-master.sql

Use this instead of MasterDb:AutoMigrate when you do not want the application to change the schema itself.
#>
param(
    [Parameter(Mandatory)][ValidateSet('SqlServer', 'PostgreSql', 'MySql', 'Sqlite')][string]$Provider,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet tool restore | Out-Null
    dotnet build D2D.sln -nologo -v q
    if ($LASTEXITCODE) { throw 'Build failed' }

    $project = "src/D2D.Migrations.$Provider"
    dotnet ef migrations script --project $project --startup-project $project --context MasterDbContext --idempotent --no-build --output $Output
    if ($LASTEXITCODE) { throw 'Generating the script failed' }
    Write-Host "Wrote $Output"
}
finally { Pop-Location }
