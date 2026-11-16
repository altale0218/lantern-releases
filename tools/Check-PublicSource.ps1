#requires -Version 7.0
param([string]$Root=(Join-Path $PSScriptRoot '..'),[switch]$StrictExport)
$ErrorActionPreference='Stop'
$rootPath=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
if((Get-Item -LiteralPath $rootPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Source root must not be a link'}
$generated='(^|/)(bin|obj|artifacts|dist|node_modules|\.test-results|\.nuget|\.vs)(/|$)'
$private='(^|/)(Keys|WebView|Inbox|OtaCache|Updates|LanternData|work|outputs)(/|$)'
$blockedExtensions='\.(apk|aab|exe|dll|so|pdb|dmp|zip|7z|keystore|jks|pfx|p12|pem|key|cer|crt|db|sqlite\d*|bin|lnk|log)$'
$blockedNames='(^|/)(\.env(\..*)?|settings\.json|receive-location\.json|history\.json|conversations\.bin|.*crash.*\.txt|startup-stage\.txt|startup-launch-stage\.txt|local\.properties|.*\.local\.(props|targets))$'
$rules=[ordered]@{
    'personal-home-path'='(?i)\b[A-Z]:[\\/]+Users[\\/]+[^\\/\s]+|/(?:Users|home)/[^/\s]+/'
    'private-key'='-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----'
    'access-token'='\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}|\bAKIA[A-Z0-9]{16}|\bsk-(?:proj-)?[A-Za-z0-9_-]{24,}'
    'clipboard-attachment'='codex-clipboard-[0-9a-f-]{20,}'
}
$localIdentifiers=@($env:USERNAME,$env:USER,$env:COMPUTERNAME)|Where-Object {$_ -and $_.Length -ge 4 -and $_ -notmatch '^(runner|runneradmin|administrator|root|user|default|public|system)$'}|Select-Object -Unique
$failures=[Collections.Generic.List[object]]::new();$count=0;$skipped=0
foreach($item in Get-ChildItem -LiteralPath $rootPath -Recurse -Force){
    $relative=$item.FullName.Substring($rootPath.Length+1).Replace('\','/')
    if($relative -match '(^|/)\.git(/|$)'){if($StrictExport){$failures.Add(@{file=$relative;kind='git-metadata'})};continue} # History requires its own review before publication.
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$failures.Add(@{file=$relative;kind='link'});continue}
    if($relative -match $generated){if($StrictExport){$failures.Add(@{file=$relative;kind='generated-output'})};$skipped++;continue}
    if($relative -match $private -or $relative -match $blockedNames -or (!$item.PSIsContainer -and $relative -match $blockedExtensions)){$failures.Add(@{file=$relative;kind='excluded-file'});continue}
    if($item.PSIsContainer){continue};$count++
    $bytes=[IO.File]::ReadAllBytes($item.FullName)
    $text=if($item.Extension -eq '.ico'){[Text.Encoding]::ASCII.GetString($bytes)+[Text.Encoding]::Unicode.GetString($bytes)}else{[Text.Encoding]::UTF8.GetString($bytes)}
    foreach($rule in $rules.GetEnumerator()){if($text -match $rule.Value){$failures.Add(@{file=$relative;kind=$rule.Key})}}
    foreach($identifier in $localIdentifiers){if($text -match ('(?i)(?<![\p{L}\p{N}_])'+[regex]::Escape($identifier)+'(?![\p{L}\p{N}_])')){$failures.Add(@{file=$relative;kind='local-identity'})}}
    # Dependency license notices contain their own public authors and must remain intact.
    if($relative -notlike 'licenses/*' -and $text -match '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'){$failures.Add(@{file=$relative;kind='email-address'})}
}
if($failures.Count){$failures|ForEach-Object {[PSCustomObject]$_}|Format-Table -AutoSize;throw "Public source review found $($failures.Count) candidate(s); inspect them without printing secret values"}
Write-Output "PASS: $count files reviewed; $skipped generated entries skipped; no candidate local identity, home path, token or private material found"
Write-Output 'Scope: static working-directory checks only; does not certify Git history, account anonymity or future changes.'
