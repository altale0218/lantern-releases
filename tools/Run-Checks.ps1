#requires -Version 7.0
param([string]$Dotnet='dotnet',[string]$ArtifactsDirectory,[switch]$Network)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$ArtifactsDirectory){$ArtifactsDirectory=Join-Path ([IO.Path]::GetTempPath()) ('LanternChecks-'+[Guid]::NewGuid().ToString('N'))}
$ArtifactsDirectory=[IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force|Out-Null
function Build([string]$project){
    & $Dotnet build (Join-Path $root "$project/$([IO.Path]::GetFileName($project)).csproj") -c Release --artifacts-path $ArtifactsDirectory
    if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
}
function Run([string]$name,[string[]]$Arguments=@()){
    Build "tests/$name"
    $dll=Join-Path $ArtifactsDirectory "bin/$name/release/$name.dll"
    if(!(Test-Path -LiteralPath $dll)){throw "Test output not found: $name"}
    & $Dotnet $dll @Arguments
    if($LASTEXITCODE -ne 0){throw "Test failed: $name"}
}
Push-Location $root
try {
    Build 'src/Lantern.Windows'
    Run 'Lantern.NotificationTests'
    Run 'Lantern.StorageTests' @((Join-Path $ArtifactsDirectory 'fixtures/storage'))
    foreach($script in @('app.js','dom.js','theme.js')){& node --check (Join-Path $root "ui/$script");if($LASTEXITCODE -ne 0){throw "JS syntax failed: $script"}}
    & "$PSScriptRoot/Test-NativeStartup.ps1" -OutputDirectory (Join-Path $ArtifactsDirectory 'java')
    if($Network){
        Run 'Lantern.SpeedTests'
        Run 'Lantern.SecurityTests'
        Run 'Lantern.ChatTests' @((Join-Path $ArtifactsDirectory 'fixtures/chat'))
        Run 'Lantern.PairingTests' @((Join-Path $ArtifactsDirectory 'fixtures/pairing'))
        Run 'Lantern.OtaTests' @('--preferences',(Join-Path $ArtifactsDirectory 'fixtures/preferences'))
    }
}finally{Pop-Location}
Write-Output 'PASS: selected build and regression checks. Generated output remains outside the source directory by default.'
