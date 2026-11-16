#requires -Version 7.0
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path ([IO.Path]::GetTempPath()) ('LanternStartupTests-'+[Guid]::NewGuid().ToString('N'))}
$classes=Join-Path $OutputDirectory 'classes';New-Item -ItemType Directory -Path $classes -Force|Out-Null
$suffix=if($IsWindows){'.exe'}else{''}
if($env:JAVA_HOME){$javac=Join-Path $env:JAVA_HOME "bin/javac$suffix";$java=Join-Path $env:JAVA_HOME "bin/java$suffix"}
else{$javac=(Get-Command javac -ErrorAction Stop).Source;$java=(Get-Command java -ErrorAction Stop).Source}
$tests=Join-Path $root 'tests/NativeStartup';$source=Join-Path $root 'src/Lantern.Android/Native'
$files=@(Get-ChildItem -LiteralPath "$tests/WindowStyle" -Recurse -File -Filter '*.java'|Select-Object -ExpandProperty FullName)
& $javac -encoding UTF-8 -d $classes @files "$source/StartupWindowStyle.java" "$source/CrashJournal.java" "$tests/CrashJournalTest.java"
if($LASTEXITCODE -ne 0){throw 'Startup test compilation failed'}
& $java -cp $classes StartupWindowStyleTest
if($LASTEXITCODE -ne 0){throw 'Window lifecycle contract failed'}
& $java -cp $classes CrashJournalTest $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Crash journal regression failed'}
$activity=[IO.File]::ReadAllText("$source/StartupActivity.java")
$content=$activity.IndexOf('setContentView(new BrandSplashView');$style=$activity.IndexOf('StartupWindowStyle.install(')
if($content -lt 0 -or $style -lt 0 -or $content -ge $style -or $activity.Contains('getInsetsController()')){throw 'Unsafe startup ordering'}
Write-Output 'PASS: Java lifecycle contract and crash journal checks (not Android device tests).'
