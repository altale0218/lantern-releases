param([Parameter(Mandatory=$true)][string]$Apk)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
function Read-DynamicSymbols([byte[]]$bytes) {
    if([BitConverter]::ToUInt32($bytes,0) -ne 0x464c457f -or $bytes[4] -ne 2 -or $bytes[5] -ne 1){throw 'Expected little-endian ELF64'}
    $offset=[BitConverter]::ToUInt64($bytes,40)
    $size=[BitConverter]::ToUInt16($bytes,58)
    $count=[BitConverter]::ToUInt16($bytes,60)
    for($i=0;$i -lt $count;$i++) {
        $p=[int]($offset+$i*$size)
        if([BitConverter]::ToUInt32($bytes,$p+4) -ne 11){continue}
        $start=[BitConverter]::ToUInt64($bytes,$p+24)
        $length=[BitConverter]::ToUInt64($bytes,$p+32)
        $stride=[BitConverter]::ToUInt64($bytes,$p+56)
        $link=[BitConverter]::ToUInt32($bytes,$p+40)
        $strings=[BitConverter]::ToUInt64($bytes,[int]($offset+$link*$size+24))
        if($stride -lt 24){throw 'Invalid dynamic symbol entry size'}
        for($j=$start;$j -lt $start+$length;$j+=$stride) {
            $k=[int]$j
            $nameStart=[int]($strings+[BitConverter]::ToUInt32($bytes,$k))
            $end=$nameStart
            while($bytes[$end] -ne 0){$end++}
            [pscustomobject]@{
                Name=[Text.Encoding]::UTF8.GetString($bytes,$nameStart,$end-$nameStart)
                Binding=$bytes[$k+4] -shr 4
                Visibility=$bytes[$k+5] -band 3
                Defined=([BitConverter]::ToUInt16($bytes,$k+6) -ne 0)
            }
        }
    }
}
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk))
try {
    $abis=@($zip.Entries | Where-Object FullName -Match '^lib/[^/]+/libmonodroid\.so$' | ForEach-Object {$_.FullName.Split('/')[1]})
    if(!$abis.Count){throw 'No Mono Android runtime found'}
    foreach($abi in $abis) {
        $exports=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $imports=@()
        foreach($library in @('libmonodroid.so','libxamarin-app.so','libmonosgen-2.0.so')) {
            $entry=$zip.GetEntry("lib/$abi/$library")
            if(!$entry){throw "Missing $abi/$library"}
            $stream=$entry.Open();$mem=[IO.MemoryStream]::new()
            try{$stream.CopyTo($mem);$symbols=@(Read-DynamicSymbols $mem.ToArray())}finally{$stream.Dispose();$mem.Dispose()}
            foreach($symbol in $symbols) {
                if($symbol.Defined -and $symbol.Binding -in @(1,2) -and $symbol.Visibility -in @(0,3)){[void]$exports.Add($symbol.Name)}
            }
            if($library -eq 'libmonodroid.so'){$imports=@($symbols | Where-Object {!$_.Defined -and $_.Binding -eq 1})}
        }
        # App-generated data and Mono entry points must resolve within this APK.
        # Bionic/libandroid/liblog imports are provided by Android, outside this check.
        $contract=@($imports | Where-Object {$_.Name -match '^(mono|monovm|xamarin|compressed_assembly|uncompressed_assemblies|assembly_|bundled_assemblies|application_config|app_environment|app_system|dso_|aot_dso|map_|java_type|jni_remapping|mm_|marshal_methods)'})
        if(!($contract.Name -contains 'compressed_assembly_count')){throw "Unexpected runtime contract for $abi"}
        $missing=@($contract | Where-Object {!$exports.Contains($_.Name)} | ForEach-Object Name)
        if($missing.Count){throw "Unresolved APK runtime symbols ($abi): $($missing -join ', ')"}
        Write-Output "PASS: $abi runtime/app/Mono contract, $($contract.Count) strong imports resolved"
    }
}finally{$zip.Dispose()}
