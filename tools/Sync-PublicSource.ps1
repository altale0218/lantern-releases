#requires -Version 7.0
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination,[switch]$Verify)
$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path.TrimEnd('\','/')
$publicRoot=(Resolve-Path -LiteralPath $Destination).Path.TrimEnd('\','/')
if($sourceRoot.Equals($publicRoot,[StringComparison]::OrdinalIgnoreCase) -or $sourceRoot.StartsWith($publicRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $publicRoot.StartsWith($sourceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Source and public directory must be distinct, non-nested directories'}
foreach($root in @($sourceRoot,$publicRoot)){if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Repository root cannot be a link'}}
if(!(Test-Path -LiteralPath "$sourceRoot/src/Lantern.Core/Lantern.Core.csproj") -or !(Test-Path -LiteralPath "$publicRoot/LICENSE")){throw 'Expected application source and prepared public repository'}
# Check the existing export before any cleanup or copy.
& "$publicRoot/tools/Check-PublicSource.ps1" -StrictExport
$allowed=@('.cs','.csproj','.java','.xml','.manifest','.js','.cjs','.css','.html','.svg','.ico','.ps1','.md','.txt')
$reviewedAssets=@('tests/ui/package.json','tests/ui/package-lock.json','src/Lantern.iOS/Info.plist',
    'src/Lantern.iOS/PrivacyInfo.xcprivacy','src/Lantern.iOS/Assets.xcassets/Contents.json',
    'src/Lantern.iOS/Assets.xcassets/AppIcon.appiconset/Contents.json','src/Lantern.iOS/Assets.xcassets/AppIcon.appiconset/Lantern.png')
$expected=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$owned=@('src','ui','tests','licenses')
if(Get-ChildItem -LiteralPath $publicRoot -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Public repository contains a link'}
foreach($dir in $owned){
    foreach($item in Get-ChildItem -LiteralPath (Join-Path $sourceRoot $dir) -Recurse -Force){
        $relative=$item.FullName.Substring($sourceRoot.Length+1).Replace('\','/')
        if($relative -match '(^|/)(bin|obj|\.git|\.vs|node_modules)(/|$)'){continue}
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Source link is not publishable'}
        if($item.PSIsContainer){continue}
        if($item.Extension -notin $allowed -and $relative -notin $reviewedAssets){throw "Unreviewed source type: $relative"}
        $expected.Add($relative,$item.FullName)
    }
}
foreach($file in @('global.json','Directory.Build.props','THIRD_PARTY_NOTICES.md','tools/Update-Windows.ps1','tools/Sync-PublicSource.ps1','tools/Check-ReleasePrivacy.ps1')){$expected.Add($file,(Join-Path $sourceRoot $file))}
$changes=[Collections.Generic.List[string]]::new()
foreach($dir in $owned){
    foreach($item in Get-ChildItem -LiteralPath (Join-Path $publicRoot $dir) -Recurse -File -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Public link is not allowed'}
        $relative=$item.FullName.Substring($publicRoot.Length+1).Replace('\','/')
        if(!$expected.ContainsKey($relative)){
            if($item.Extension -notin $allowed -and $relative -notin $reviewedAssets){throw "Unexpected public file requires manual review: $relative"}
            $changes.Add("remove $relative")
            if(!$Verify){$full=[IO.Path]::GetFullPath($item.FullName);if(!$full.StartsWith($publicRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Delete outside public repository'};Remove-Item -LiteralPath $full -Force}
        }
    }
}
foreach($entry in $expected.GetEnumerator()){
    $target=[IO.Path]::GetFullPath((Join-Path $publicRoot $entry.Key))
    if(!$target.StartsWith($publicRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Destination escaped repository'}
    if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $entry.Value).Hash){
        $changes.Add("update $($entry.Key)")
        if(!$Verify){New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null;Copy-Item -LiteralPath $entry.Value -Destination $target -Force}
    }
}
$release=[IO.File]::ReadAllText("$sourceRoot/src/Lantern.AppLogic/OtaUpdate.cs")
$version=[regex]::Match($release,'const string Version="([^"]+)"').Groups[1].Value
$code=[regex]::Match($release,'const int AndroidCode=(\d+)').Groups[1].Value
if(!$version -or !$code){throw 'Application release metadata missing'}
if($Verify){
    if($changes.Count){$changes|Write-Output;throw 'Public source is out of sync'}
    if(![IO.File]::ReadAllText("$publicRoot/README.md").Contains("**$version**")){throw 'Public README version is out of sync'}
    Write-Output "PASS: $($expected.Count) owned files match public source, version $version"
    return
}
$readme=[IO.File]::ReadAllText("$publicRoot/README.md")
$readme=[regex]::Replace($readme,'当前源码版本 \*\*[^*]+\*\*，Android versionCode \*\*\d+\*\*',"当前源码版本 **$version**，Android versionCode **$code**")
[IO.File]::WriteAllText("$publicRoot/README.md",$readme,[Text.UTF8Encoding]::new($false))
$encoded=[regex]::Match($release,'const string Notes=("(?:\\.|[^"\\])*")').Groups[1].Value
$notes=ConvertFrom-Json $encoded
$section="## $version`n"+(($notes -split "`n"|Select-Object -Skip 1) -join "`n")+"`n`n"
$changelog=[IO.File]::ReadAllText("$publicRoot/CHANGELOG.md")
$pattern='(?ms)^## '+[regex]::Escape($version)+'\r?\n.*?(?=^## |\z)'
if([regex]::IsMatch($changelog,$pattern)){$changelog=[regex]::Replace($changelog,$pattern,[Text.RegularExpressions.MatchEvaluator]{param($m)$section})}
else{$first=$changelog.IndexOf('## ');if($first -ge 0){$changelog=$changelog.Insert($first,$section)}else{$changelog+="`n$section"}}
[IO.File]::WriteAllText("$publicRoot/CHANGELOG.md",$changelog,[Text.UTF8Encoding]::new($false))
& "$publicRoot/tools/Check-PublicSource.ps1" -StrictExport
$manifestPath=Join-Path $publicRoot 'SOURCE-MANIFEST.json'
$files=@(Get-ChildItem -LiteralPath $publicRoot -Recurse -File -Force|Where-Object {$_.FullName -ne $manifestPath}|Sort-Object FullName)
$manifest=[ordered]@{kind='reviewed-source-snapshot';version=$version;license='MIT (prepared for maintainer review)';includesGitHistory=$false;includesRuntimeData=$false;includesSignedBinaries=$false;files=@($files|ForEach-Object {[ordered]@{path=$_.FullName.Substring($publicRoot.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 6)+"`n",[Text.UTF8Encoding]::new($false))
Write-Output "Synchronized $($expected.Count) owned source files, $($changes.Count) changes, version $version; public documentation preserved"
