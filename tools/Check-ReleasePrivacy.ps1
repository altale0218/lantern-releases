#requires -Version 7.0
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\','/')
$identifiers=@($env:USERNAME,$env:USER,$env:COMPUTERNAME)|Where-Object {$_ -and $_.Length -ge 4 -and $_ -notmatch '^(runner|runneradmin|administrator|root|user|default|system)$'}|Select-Object -Unique
$failures=[Collections.Generic.List[object]]::new();$count=0
function Inspect([byte[]]$Bytes,[string]$Name){
    $text=[Text.Encoding]::UTF8.GetString($Bytes)+[Text.Encoding]::Unicode.GetString($Bytes)
    # These public repository endpoints are part of the updater. The account name contains a local identifier as a substring.
    $text=$text.Replace('https://api.github.com/repos/altale0218/lantern-releases/releases?per_page=30','[public release endpoint]')
    $text=$text.Replace('https://api.github.com/repos/altale0218/lantern-releases/releases','[public release endpoint]')
    $text=$text.Replace('https://raw.githubusercontent.com/altale0218/lantern-releases/lantern-update-catalog/','[public release endpoint]')
    $text=$text.Replace('https://github.com/altale0218/lantern-releases/releases/download/','[public release endpoint]')
    foreach($id in $identifiers){if($text.IndexOf($id,[StringComparison]::OrdinalIgnoreCase) -ge 0){$failures.Add(@{file=$Name;kind='local-build-identity'});break}}
    # Public vendor build paths are not treated as a maintainer's identity.
}
Add-Type -AssemblyName System.IO.Compression
foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force){
    $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
    if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Release cannot contain links'}
    if($file.Extension -in @('.pdb','.keystore','.jks','.pfx','.p12','.key','.pem')){$failures.Add(@{file=$relative;kind='debug-or-private-material'});continue}
    if($file.Extension -in @('.dll','.exe','.so','.dex','.json','.xml','.config')){Inspect ([IO.File]::ReadAllBytes($file.FullName)) $relative;$count++}
    if($file.Extension -eq '.apk'){
        $zip=[IO.Compression.ZipFile]::OpenRead($file.FullName)
        try{foreach($entry in $zip.Entries){
            if($entry.FullName.EndsWith('.pdb',[StringComparison]::OrdinalIgnoreCase)){$failures.Add(@{file=$relative+'/'+$entry.FullName;kind='packaged-debug-symbol'})}
            if($entry.Length -gt 256MB){throw 'Unreviewed oversized APK entry'}
            $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
            try{$stream.CopyTo($memory);Inspect ($memory.ToArray()) ($relative+'/'+$entry.FullName);$count++}finally{$memory.Dispose();$stream.Dispose()}
        }}finally{$zip.Dispose()}
    }
}
if($failures.Count){$failures|ForEach-Object {[PSCustomObject]$_}|Format-Table -AutoSize;throw 'Release privacy candidates require review; no matching identifier values printed'}
Write-Output "PASS: $count binary/metadata entries reviewed; no current local identity or standalone debug/private material found"
Write-Output 'Scope: known local build identity and visible artifact metadata; does not certify all compressed data or publisher anonymity.'
