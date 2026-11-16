#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){2}$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('stable','beta')][string]$Channel,
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][string]$NotesFile,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference='Stop'
$packages=(Resolve-Path -LiteralPath $PackagesDirectory).Path
$notes=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path).Trim()
$notesCode=[regex]::Matches($notes,'\bversionCode[ \t]+[1-9][0-9]{0,8}\b')
if($notes.Length -gt 6000 -or $notesCode.Count -ne 1){throw 'Release notes must provide exactly one Android versionCode and fit the update view'}
$names=@('Lantern-Android.apk','Lantern-Windows.zip','SHA256SUMS.txt')
$checksums=@(Get-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt') | Where-Object {$_ -match '\S'})
if($checksums.Count -ne 2){throw 'Expected exactly two current installation package checksums'}
$assets=foreach($name in $names){
    $file=Get-Item -LiteralPath (Join-Path $packages $name)
    if($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt 256MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw "Invalid release asset $name"
    }
    $digest=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if($name -ne 'SHA256SUMS.txt' -and @($checksums -cmatch ('^'+$digest+'[ \t]+\*?'+[regex]::Escape($name)+'$')).Count -ne 1){
        throw "Release checksum mismatch for $name"
    }
    @{name=$name;size=$file.Length;digest="sha256:$digest"}
}
$origins=[ordered]@{
    github='https://github.com/altale0218/lantern-releases/releases/download/'
    gitee='https://gitee.com/altalatic/lantern-releases/releases/download/'
}
$output=[IO.Path]::GetFullPath($Destination)
$encoding=[Text.UTF8Encoding]::new($false)
foreach($source in $origins.Keys){
    $directory=(New-Item -ItemType Directory -Path (Join-Path $output $source) -Force).FullName
    $entry=[ordered]@{
        tag_name="v$Version"
        draft=$false
        prerelease=$Channel -eq 'beta'
        body=$notes
        assets=@(foreach($asset in $assets){
            [ordered]@{name=$asset.name;size=$asset.size;digest=$asset.digest;browser_download_url=$origins[$source]+"v$Version/"+$asset.name}
        })
    }
    $catalog=Join-Path $directory "$Channel.json"
    if(Test-Path -LiteralPath $catalog){
        $previous=@(Get-Content -LiteralPath $catalog -Raw | ConvertFrom-Json)
        if($previous.Count -gt 1 -or ($previous.Count -eq 1 -and [version]$previous[0].tag_name.TrimStart('v') -gt [version]$Version)){
            throw 'A channel cannot move backward'
        }
    }
    [IO.File]::WriteAllText($catalog,(ConvertTo-Json -InputObject @($entry) -Depth 6)+[Environment]::NewLine,$encoding)
    $otherChannel=if($Channel -eq 'beta'){'stable'}else{'beta'}
    $other=Join-Path $directory "$otherChannel.json"
    if(!(Test-Path -LiteralPath $other)){[IO.File]::WriteAllText($other,'[]'+[Environment]::NewLine,$encoding)}
    Write-Output "$source $Channel catalog generated for v$Version"
}
